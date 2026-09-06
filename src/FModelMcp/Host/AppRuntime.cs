using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using FModelMcp.Config;
using FModelMcp.Contracts;
using FModelMcp.Worker;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace FModelMcp.Host;

public sealed class AppRuntime : IAsyncDisposable
{
    private readonly ConfigService _configService;
    private readonly WorkerSupervisor _supervisor = new();
    private readonly ILogger<AppRuntime>? _logger;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private int _switchRequested;
    private int _queuedRequests;
    private readonly ConcurrentDictionary<string, JsonElement> _knownTaskData = new(StringComparer.Ordinal);
    private JsonElement? _lastStatusData;
    private ToolError? _startupError;
    private string _hostState = "unconfigured";
    private string? _sessionId;

    public AppRuntime(string? configPath, ILogger<AppRuntime>? logger = null)
    {
        _configService = new ConfigService(configPath);
        _logger = logger;
    }

    public ConfigService Config => _configService;
    public string? SessionId => _sessionId;
    public string HostState => _hostState;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _configService.Load();
        if (_configService.LastErrorCode is not null)
        {
            _hostState = "faulted";
            return;
        }
        if (_configService.Current is null)
        {
            _hostState = "unconfigured";
            return;
        }
        var session = Guid.NewGuid().ToString("N");
        _hostState = "starting";
        try
        {
            var result = await _supervisor.StartAsync(session, _configService.Current, cancellationToken).ConfigureAwait(false);
            _sessionId = session;
            _hostState = result.Ok ? "active" : "faulted";
            _startupError = result.Ok ? null : result.Error;
            if (!result.Ok) _logger?.LogWarning("Worker startup failed: {Code}", result.Error?.Code);
        }
        catch (Exception ex)
        {
            _sessionId = session;
            _hostState = "faulted";
            _startupError = new ToolError { Code = "WORKER_FAILED", Message = "Worker 启动异常：" + ex.GetType().Name, Retryable = true, SuggestedAction = "检查 get_status 或调用 remount" };
            _logger?.LogWarning(ex, "Worker startup failed");
        }
    }

    public async Task<CallToolResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_supervisor.IsRunning)
        {
            var result = await _supervisor.CallAsync("get_status", null, cancellationToken).ConfigureAwait(false);
            if (result.Ok && result.Result is { } json)
            {
                var status = ToolResults.FromWorker(_sessionId, json, _configService.Current?.Limits.MaxToolResultBytes);
                var structuredStatus = status.StructuredContent;
                if (status.IsError != true && structuredStatus.HasValue)
                {
                    var statusContent = structuredStatus.Value;
                    if (statusContent.ValueKind == JsonValueKind.Object && statusContent.TryGetProperty("data", out var statusData) && statusData.ValueKind == JsonValueKind.Object)
                        _lastStatusData = statusData.Clone();
                }
                return status;
            }
            if (result.Error?.Code is "WORKER_FAILED" or "WORKER_MEMORY_LIMIT" or "OPERATION_TIMEOUT") _hostState = "faulted";
            return ToolResults.Failure(_sessionId, result.Error?.Code ?? "WORKER_FAILED", result.Error?.Message ?? "Worker 不可用", result.Error?.Retryable ?? true, result.Error?.SuggestedAction, maxResultBytes: MaxResultBytes());
        }

        if (_startupError is { } startupError)
            return ToolResults.Failure(_sessionId, startupError.Code, startupError.Message, startupError.Retryable, startupError.SuggestedAction, details: startupError.Details, maxResultBytes: MaxResultBytes());

        if (_lastStatusData is { } lastStatus)
        {
            var stale = JsonNode.Parse(lastStatus.GetRawText())!.AsObject();
            stale["hostState"] = "faulted";
            stale["stale"] = true;
            stale["workerPid"] = null;
            stale["reportTimeUtc"] = DateTimeOffset.UtcNow;
            return ToolResults.Success(_sessionId, stale, maxResultBytes: MaxResultBytes());
        }

        var data = new
        {
            hostState = _hostState,
            sessionId = _sessionId,
            workerPid = (int?)null,
            reportTimeUtc = DateTimeOffset.UtcNow,
            stale = _sessionId is not null,
            mountState = _configService.Current is null ? "unmounted" : "failed",
            gameRoot = _configService.Current?.GameRoot,
            archiveDir = _configService.Current?.ArchiveDir,
            ueVersion = _configService.Current?.UeVersion,
            effectiveEngineVersion = (string?)null,
            language = _configService.Current?.Language,
            sourceFingerprint = (string?)null,
            catalogRevision = (string?)null,
            archives = new { discovered = 0, mounted = 0, unmounted = 0, unsupported = 0 },
            files = new { physicalEntries = 0, effectiveFiles = 0, packages = 0, conflictedPaths = 0 },
            keys = new { configuredGuids = 0, requiredGuids = 0, rejectedGuids = 0 },
            mappings = new { configured = _configService.Current?.Usmap is not null, loaded = false, path = _configService.Current?.Usmap, sha256 = (string?)null, requirement = "unknown" },
            native = Array.Empty<object>(),
            indexes = new Dictionary<string, object>(),
            activeTaskId = (string?)null,
            recentErrors = _configService.LastErrorCode is null ? Array.Empty<object>() : new[] { new { code = _configService.LastErrorCode, message = _configService.LastErrorMessage } }
        };
        return _configService.LastErrorCode is null
            ? ToolResults.Success(_sessionId, data, maxResultBytes: MaxResultBytes())
            : ToolResults.Failure(_sessionId, _configService.LastErrorCode, _configService.LastErrorMessage ?? "配置错误", false, "修正配置后调用 set_config", maxResultBytes: MaxResultBytes());
    }

    public async Task<CallToolResult> SetConfigAsync(ConfigPatch patch, bool persist, CancellationToken cancellationToken)
    {
        ConfigOperationResult candidate;
        try { candidate = _configService.BuildCandidate(patch); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ToolResults.Failure(_sessionId, "CONFIG_INVALID", "配置 patch 无效：" + ex.Message, false, "修正 patch 后重试");
        }
        if (!candidate.IsValid || candidate.Config is null)
            return ToolResults.Failure(_sessionId, "CONFIG_INVALID", candidate.Message ?? "配置无效", false, "修正 patch 后重试");
        if (!await TryEnterSwitchAsync(cancellationToken).ConfigureAwait(false))
            return ToolResults.Failure(_sessionId, "SESSION_BUSY", "当前会话正在切换", true, "等待当前切换完成");
        ConfigPersistencePreparation? prepared = null;
        var candidateApplied = false;
        try
        {
            if (Volatile.Read(ref _queuedRequests) > 0)
                return ToolResults.Failure(_sessionId, "SESSION_BUSY", "当前会话有请求正在排队或执行", true, "等待当前请求完成", maxResultBytes: MaxResultBytes());
            if (await HasActiveTaskAsync(cancellationToken).ConfigureAwait(false))
                return ToolResults.Failure(_sessionId, "SESSION_BUSY", "当前会话有后台任务正在运行", true, "先调用 cancel_task 并等待任务结束", maxResultBytes: MaxResultBytes());
            if (persist)
            {
                var preparedResult = await _configService.PreparePersistAsync(candidate.Config, cancellationToken).ConfigureAwait(false);
                if (!preparedResult.Succeeded || preparedResult.Preparation is null)
                    return ToolResults.Failure(_sessionId, preparedResult.Code ?? "CONFIG_PERSIST_FAILED", preparedResult.Message ?? "配置持久化准备失败", false, "检查配置目录权限", maxResultBytes: MaxResultBytes());
                prepared = preparedResult.Preparation;
            }

            _hostState = "switching";
            var previous = _sessionId;
            await _supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
            _lastStatusData = null;
            if (prepared is not null)
            {
                var persisted = _configService.CommitPrepared(prepared);
                if (!persisted.Succeeded)
                {
                    _hostState = "faulted";
                    return ToolResults.Failure(_sessionId, persisted.Code ?? "CONFIG_PERSIST_FAILED", persisted.Message ?? "配置持久化失败", false, "检查配置目录权限；旧 Worker 已停止", data: new { applied = false, persisted = false, previousSessionId = previous, sessionId = _sessionId, mountState = "failed", configSummary = _configService.ToSafeSummary() }, maxResultBytes: MaxResultBytes());
                }
            }

            _configService.SetCurrent(candidate.Config);
            candidateApplied = true;
            var next = Guid.NewGuid().ToString("N");
            var worker = await _supervisor.StartAsync(next, candidate.Config, cancellationToken).ConfigureAwait(false);
            _sessionId = next;
            _hostState = worker.Ok ? "active" : "faulted";
            _startupError = worker.Ok ? null : worker.Error;
            if (!worker.Ok)
            {
                return ToolResults.Failure(_sessionId, worker.Error?.Code ?? "WORKER_FAILED", worker.Error?.Message ?? "Worker 启动失败", worker.Error?.Retryable ?? true, worker.Error?.SuggestedAction, data: new { applied = true, persisted = persist, previousSessionId = previous, sessionId = next, mountState = "failed", configSummary = _configService.ToSafeSummary(candidate.Config) }, maxResultBytes: MaxResultBytes());
            }
            var mountState = Extract(worker.Result, "mountState") ?? "unknown";
            var mountError = ExtractRecentErrorCode(worker.Result);
            var appliedData = new { applied = true, persisted = persist, previousSessionId = previous, sessionId = next, mountState, configSummary = _configService.ToSafeSummary(candidate.Config) };
            if (mountState is "failed" or "partial")
                return ToolResults.Failure(_sessionId, mountError ?? "MOUNT_FAILED", "配置已应用但挂载不完整", true, "检查 get_status/list_archives 后补充 AES、usmap 或 Native", data: appliedData, maxResultBytes: MaxResultBytes());
            return ToolResults.Success(_sessionId, appliedData, maxResultBytes: MaxResultBytes());
        }
        catch (OperationCanceledException)
        {
            _hostState = _supervisor.IsRunning ? "active" : "faulted";
            return ToolResults.Failure(_sessionId, "SESSION_BUSY", "配置切换被取消；Host 已收敛到当前会话状态", true, "重新读取 get_status", maxResultBytes: MaxResultBytes());
        }
        catch (Exception ex)
        {
            _hostState = _supervisor.IsRunning ? "active" : "faulted";
            return ToolResults.Failure(_sessionId, "WORKER_FAILED", "配置切换失败：" + ex.GetType().Name, true, "重新读取 get_status 或调用 remount", data: new { applied = candidateApplied, persisted = persist, mountState = candidateApplied ? "failed" : "unchanged" }, maxResultBytes: MaxResultBytes());
        }
        finally
        {
            _configService.CleanupPrepared(prepared);
            _switchGate.Release();
            Volatile.Write(ref _switchRequested, 0);
        }
    }

    public async Task<CallToolResult> RemountAsync(CancellationToken cancellationToken)
    {
        if (_configService.Current is null)
            return ToolResults.Failure(_sessionId, "NOT_CONFIGURED", "当前没有可 remount 的内存配置", false, "先调用 set_config");
        if (!await TryEnterSwitchAsync(cancellationToken).ConfigureAwait(false))
            return ToolResults.Failure(_sessionId, "SESSION_BUSY", "当前会话正在切换", true, "等待当前切换完成");
        try
        {
            if (Volatile.Read(ref _queuedRequests) > 0)
                return ToolResults.Failure(_sessionId, "SESSION_BUSY", "当前会话有请求正在排队或执行", true, "等待当前请求完成", maxResultBytes: MaxResultBytes());
            if (await HasActiveTaskAsync(cancellationToken).ConfigureAwait(false))
                return ToolResults.Failure(_sessionId, "SESSION_BUSY", "当前会话有后台任务正在运行", true, "先调用 cancel_task 并等待任务结束", maxResultBytes: MaxResultBytes());
            _hostState = "switching";
            var previous = _sessionId;
            var next = Guid.NewGuid().ToString("N");
            _lastStatusData = null;
            await _supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
            var result = await _supervisor.StartAsync(next, _configService.Current, cancellationToken).ConfigureAwait(false);
            _sessionId = next;
            _hostState = result.Ok ? "active" : "faulted";
            _startupError = result.Ok ? null : result.Error;
            if (!result.Ok) return ToolResults.Failure(_sessionId, result.Error?.Code ?? "WORKER_FAILED", result.Error?.Message ?? "Worker remount 失败", result.Error?.Retryable ?? true, result.Error?.SuggestedAction, data: new { previousSessionId = previous, sessionId = next, mountState = "failed", changed = true }, maxResultBytes: MaxResultBytes());
            var mountState = Extract(result.Result, "mountState") ?? "unknown";
            var remountData = new { previousSessionId = previous, sessionId = next, mountState, sourceFingerprint = Extract(result.Result, "sourceFingerprint"), changed = !string.Equals(previous, next, StringComparison.Ordinal) };
            if (mountState is "failed" or "partial") return ToolResults.Failure(_sessionId, ExtractRecentErrorCode(result.Result) ?? "MOUNT_FAILED", "Worker remount 后挂载不完整", true, "检查 get_status/list_archives", data: remountData, maxResultBytes: MaxResultBytes());
            return ToolResults.Success(_sessionId, remountData, maxResultBytes: MaxResultBytes());
        }
        catch (OperationCanceledException)
        {
            _hostState = _supervisor.IsRunning ? "active" : "faulted";
            return ToolResults.Failure(_sessionId, "SESSION_BUSY", "remount 被取消；请重新读取 get_status", true, "重新读取 get_status", maxResultBytes: MaxResultBytes());
        }
        catch (Exception ex)
        {
            _hostState = "faulted";
            return ToolResults.Failure(_sessionId, "WORKER_FAILED", "Worker remount 异常：" + ex.GetType().Name, true, "调用 get_status 或重试 remount", maxResultBytes: MaxResultBytes());
        }
        finally
        {
            _switchGate.Release();
            Volatile.Write(ref _switchRequested, 0);
        }
    }

    public async Task<CallToolResult> CallAsync(string operation, object? payload, CancellationToken cancellationToken)
    {
        if (operation == "get_status") return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (_configService.Current is null)
            return ToolResults.Failure(_sessionId, "NOT_CONFIGURED", "当前没有有效配置", false, "先调用 set_config", maxResultBytes: MaxResultBytes());

        if (operation is "get_task" or "cancel_task")
        {
            var control = await _supervisor.CallAsync(operation, payload, cancellationToken).ConfigureAwait(false);
            if (control.Ok && control.Result is { } controlJson)
            {
                var result = ToolResults.FromWorker(_sessionId, controlJson, MaxResultBytes());
                RememberTask(result);
                return result;
            }
            if (operation == "get_task" && TryRecoverTask(payload, control.Error, out var recovered)) return recovered;
            return ToolResults.Failure(_sessionId, control.Error?.Code ?? "WORKER_FAILED", control.Error?.Message ?? "Worker 不可用", control.Error?.Retryable ?? true, control.Error?.SuggestedAction, maxResultBytes: MaxResultBytes());
        }

        if (Volatile.Read(ref _switchRequested) != 0)
            return ToolResults.Failure(_sessionId, "SESSION_BUSY", "当前会话正在切换", true, "等待当前切换完成", maxResultBytes: MaxResultBytes());
        var maxQueued = _configService.Current.Limits.MaxQueuedRequests;
        if (Interlocked.Increment(ref _queuedRequests) > maxQueued)
        {
            Interlocked.Decrement(ref _queuedRequests);
            return ToolResults.Failure(_sessionId, "SERVER_BUSY", "请求队列已满", true, "稍后重试或缩小请求范围", maxResultBytes: MaxResultBytes());
        }
        try
        {
            if (Volatile.Read(ref _switchRequested) != 0)
                return ToolResults.Failure(_sessionId, "SESSION_BUSY", "当前会话正在切换", true, "等待当前切换完成", maxResultBytes: MaxResultBytes());
            await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await _supervisor.CallAsync(operation, payload, cancellationToken).ConfigureAwait(false);
                if (result.Ok && result.Result is { } json)
                {
                    var toolResult = ToolResults.FromWorker(_sessionId, json, MaxResultBytes());
                    RememberTask(toolResult);
                    return toolResult;
                }
                if (result.Error?.Code is "WORKER_FAILED" or "WORKER_MEMORY_LIMIT" or "OPERATION_TIMEOUT") _hostState = "faulted";
                return ToolResults.Failure(_sessionId, result.Error?.Code ?? "WORKER_FAILED", result.Error?.Message ?? "Worker 不可用", result.Error?.Retryable ?? true, result.Error?.SuggestedAction, maxResultBytes: MaxResultBytes());
            }
            finally { _switchGate.Release(); }
        }
        finally { Interlocked.Decrement(ref _queuedRequests); }
    }

    private long? MaxResultBytes() => _configService.Current?.Limits.MaxToolResultBytes;

    private void RememberTask(CallToolResult result)
    {
        if (result.StructuredContent is not { } content || content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("taskId", out var id) || id.ValueKind != JsonValueKind.String) return;
        _knownTaskData[id.GetString()!] = data.Clone();
    }

    private bool TryRecoverTask(object? payload, ToolError? error, out CallToolResult recovered)
    {
        recovered = null!;
        if (payload is null) return false;
        var request = JsonSerializer.SerializeToElement(payload, JsonDefaults.Options);
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("taskId", out var id) || id.ValueKind != JsonValueKind.String || !_knownTaskData.TryGetValue(id.GetString()!, out var data)) return false;
        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        node["state"] = "interrupted";
        node["stale"] = true;
        node["finishedAtUtc"] = DateTimeOffset.UtcNow;
        _knownTaskData[id.GetString()!] = JsonSerializer.SerializeToElement(node, JsonDefaults.Options);
        recovered = ToolResults.Failure(_sessionId, error?.Code ?? "WORKER_FAILED", "Worker 已失联；返回最后已知任务状态", true, "调用 remount 后重新读取 get_task", data: node, maxResultBytes: MaxResultBytes());
        return true;
    }

    private async Task<bool> TryEnterSwitchAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _switchRequested, 1, 0) != 0) return false;
        try
        {
            if (await _switchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return true;
            Volatile.Write(ref _switchRequested, 0);
            return false;
        }
        catch
        {
            Volatile.Write(ref _switchRequested, 0);
            throw;
        }
    }

    private async Task<bool> HasActiveTaskAsync(CancellationToken cancellationToken)
    {
        if (!_supervisor.IsRunning) return false;
        var result = await _supervisor.CallAsync("get_status", null, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Result is not { } json) return false;
        return Extract(json, "activeTaskId") is not null;
    }

    private static string? Extract(JsonElement? result, string property)
    {
        if (result is not { } value || value.ValueKind != JsonValueKind.Object) return null;
        if (!value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        return data.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static string? ExtractRecentErrorCode(JsonElement? result)
    {
        if (result is not { } value || !value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("recentErrors", out var errors) || errors.ValueKind != JsonValueKind.Array) return null;
        foreach (var error in errors.EnumerateArray())
            if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String) return code.GetString();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        _switchGate.Dispose();
    }
}
