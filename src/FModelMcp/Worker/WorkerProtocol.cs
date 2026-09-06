using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FModelMcp.Contracts;

namespace FModelMcp.Worker;

internal static class WorkerMode
{
    public static async Task RunAsync(string[] args)
    {
        try { await RunCoreAsync(args).ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"worker fatal: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static async Task RunCoreAsync(string[] args)
    {
        var protocolOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(TextWriter.Null);
        await using var engine = new WorkerEngine();
        string? sessionId = null;
        var maxLineBytes = 1_048_576;

        using var outputGate = new SemaphoreSlim(1, 1);
        using var operationGate = new SemaphoreSlim(1, 1);
        var requestCancellations = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var preCancelledRequests = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var dispatchTasks = new ConcurrentBag<Task>();
        var inputLines = new BoundedLineReader(Console.In);

        async Task WriteSerializedResultAsync(string id, string currentSessionId, object result, bool preserveFailureResult)
        {
            await outputGate.WaitAsync().ConfigureAwait(false);
            try { await WriteResultAsync(protocolOut, id, currentSessionId, result, maxLineBytes, preserveFailureResult).ConfigureAwait(false); }
            finally { outputGate.Release(); }
        }

        async Task WriteSerializedErrorAsync(string id, string currentSessionId, string code, string message)
        {
            await outputGate.WaitAsync().ConfigureAwait(false);
            try { await WriteErrorAsync(protocolOut, id, currentSessionId, code, message).ConfigureAwait(false); }
            finally { outputGate.Release(); }
        }

        async Task DispatchOperationAsync(IpcMessage message, bool serialized)
        {
            if (serialized && !await operationGate.WaitAsync(0).ConfigureAwait(false))
            {
                await WriteSerializedErrorAsync(message.Id, message.SessionId, "SERVER_BUSY", "已有解析操作正在运行").ConfigureAwait(false);
                return;
            }
            using var requestCancellation = new CancellationTokenSource();
            requestCancellations[message.Id] = requestCancellation;
            if (preCancelledRequests.TryRemove(message.Id, out _)) requestCancellation.Cancel();
            try
            {
                var operationResult = await engine.HandleAsync(message.Operation, message.Payload, requestCancellation.Token).ConfigureAwait(false);
                if (!requestCancellation.IsCancellationRequested)
                    await WriteSerializedResultAsync(message.Id, message.SessionId, operationResult, preserveFailureResult: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!requestCancellation.IsCancellationRequested)
                    await WriteSerializedErrorAsync(message.Id, message.SessionId, "WORKER_FAILED", "Worker 操作异常：" + ex.GetType().Name).ConfigureAwait(false);
            }
            finally
            {
                requestCancellations.TryRemove(message.Id, out _);
                if (serialized) operationGate.Release();
            }
        }

        async Task DrainOperationsAsync()
        {
            foreach (var request in requestCancellations.Values) request.Cancel();
            try { await Task.WhenAll(dispatchTasks).ConfigureAwait(false); } catch { }
        }

        while (true)
        {
            string? line;
            try { line = await inputLines.ReadLineAsync(maxLineBytes).ConfigureAwait(false); }
            catch (IpcLineTooLongException)
            {
                await WriteSerializedErrorAsync("", sessionId ?? "", "INVALID_ARGUMENT", "IPC 行超过预算").ConfigureAwait(false);
                break;
            }
            if (line is null) break;

            IpcMessage? message;
            try { message = JsonSerializer.Deserialize<IpcMessage>(line, JsonDefaults.Options); }
            catch (JsonException)
            {
                await WriteSerializedErrorAsync("", sessionId ?? "", "INVALID_ARGUMENT", "IPC 消息不是有效 JSON").ConfigureAwait(false);
                continue;
            }
            if (message is null) continue;
            sessionId ??= message.SessionId;
            if (!string.Equals(sessionId, message.SessionId, StringComparison.Ordinal))
            {
                await WriteSerializedErrorAsync(message.Id, message.SessionId, "WORKER_FAILED", "sessionId 不匹配").ConfigureAwait(false);
                continue;
            }

            if (message.Kind == "cancel" && message.Operation == "cancel_request")
            {
                if (message.Payload is { ValueKind: JsonValueKind.Object } cancelPayload && cancelPayload.TryGetProperty("requestId", out var requestId) && requestId.ValueKind == JsonValueKind.String)
                {
                    var requestIdValue = requestId.GetString()!;
                    if (requestCancellations.TryGetValue(requestIdValue, out var requestCancellation))
                    {
                        try { requestCancellation.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    else
                    {
                        if (preCancelledRequests.Count >= 1024) preCancelledRequests.Clear();
                        preCancelledRequests[requestIdValue] = 0;
                    }
                }
                continue;
            }

            if (message.Operation == "initialize")
            {
                try
                {
                    var init = message.Payload?.Deserialize<WorkerInit>(JsonDefaults.Options)
                        ?? throw new InvalidDataException("缺少 Worker init payload");
                    maxLineBytes = init.Config.Limits.MaxIpcLineBytes;
                    var result = await engine.InitializeAsync(message.SessionId, init, CancellationToken.None).ConfigureAwait(false);
                    await WriteSerializedResultAsync(message.Id, message.SessionId, result, preserveFailureResult: false).ConfigureAwait(false);
                    if (result is ToolEnvelope { Ok: false }) break;
                }
                catch (Exception ex)
                {
                    await WriteSerializedErrorAsync(message.Id, message.SessionId, "WORKER_FAILED", "Worker 初始化异常：" + ex.GetType().Name).ConfigureAwait(false);
                    break;
                }
                continue;
            }

            if (message.Operation == "shutdown")
            {
                await WriteSerializedResultAsync(message.Id, message.SessionId, new ToolEnvelope { Ok = true, SessionId = message.SessionId }, preserveFailureResult: false).ConfigureAwait(false);
                await DrainOperationsAsync().ConfigureAwait(false);
                break;
            }

            var control = message.Operation is "get_status" or "get_task" or "cancel_task";
            dispatchTasks.Add(Task.Run(() => DispatchOperationAsync(message, serialized: !control)));
        }
        await DrainOperationsAsync().ConfigureAwait(false);
    }

    private static async Task WriteResultAsync(StreamWriter writer, string id, string sessionId, object result, int maxLineBytes, bool preserveFailureResult)
    {
        var resultElement = JsonSerializer.SerializeToElement(result, result.GetType(), JsonDefaults.Options);
        var envelope = result as ToolEnvelope;
        var response = new IpcResponse
        {
            Id = id,
            SessionId = sessionId,
            Ok = envelope?.Ok ?? true,
            Result = envelope?.Ok == false && !preserveFailureResult ? null : resultElement,
            Error = envelope?.Ok == false ? envelope.Error : null
        };
        var line = JsonSerializer.Serialize(response, JsonDefaults.Options);
        if (Encoding.UTF8.GetByteCount(line) > maxLineBytes)
        {
            response = new IpcResponse
            {
                Id = id,
                SessionId = sessionId,
                Ok = false,
                Error = new ToolError { Code = "OUTPUT_BUDGET_EXCEEDED", Message = "IPC 响应超过行预算", Retryable = false, SuggestedAction = "缩小请求范围" }
            };
            line = JsonSerializer.Serialize(response, JsonDefaults.Options);
        }
        await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(StreamWriter writer, string id, string sessionId, string code, string message)
    {
        var response = new IpcResponse
        {
            Id = id,
            SessionId = sessionId,
            Ok = false,
            Error = new ToolError { Code = code, Message = message, Retryable = code is "WORKER_FAILED" or "SERVER_BUSY" }
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonDefaults.Options)).ConfigureAwait(false);
    }
}

internal sealed class IpcLineTooLongException : Exception;

internal sealed class BoundedLineReader(TextReader reader)
{
    private readonly char[] _buffer = new char[4096];
    private int _offset;
    private int _count;

    public async ValueTask<string?> ReadLineAsync(int maxBytes, CancellationToken cancellationToken = default)
    {
        var line = new StringBuilder();
        var bytes = 0;
        char? highSurrogate = null;
        while (true)
        {
            var next = await ReadCharAsync(cancellationToken).ConfigureAwait(false);
            if (next < 0)
            {
                if (highSurrogate is not null) bytes += 3;
                if (bytes > maxBytes) throw new IpcLineTooLongException();
                return line.Length == 0 ? null : line.ToString();
            }
            var character = (char)next;
            if (character == '\n')
            {
                if (highSurrogate is not null) bytes += 3;
                if (bytes > maxBytes) throw new IpcLineTooLongException();
                return line.ToString().TrimEnd('\r');
            }
            if (highSurrogate is not null)
            {
                if (char.IsLowSurrogate(character))
                {
                    line.Append(character);
                    bytes += 4;
                    highSurrogate = null;
                    if (bytes > maxBytes) throw new IpcLineTooLongException();
                    continue;
                }
                bytes += 3;
                highSurrogate = null;
            }
            if (char.IsHighSurrogate(character))
            {
                line.Append(character);
                highSurrogate = character;
                continue;
            }
            line.Append(character);
            bytes += character <= 0x7F ? 1 : character <= 0x7FF ? 2 : 3;
            if (bytes > maxBytes) throw new IpcLineTooLongException();
        }
    }

    private async ValueTask<int> ReadCharAsync(CancellationToken cancellationToken)
    {
        if (_offset >= _count)
        {
            _count = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            _offset = 0;
            if (_count == 0) return -1;
        }
        return _buffer[_offset++];
    }
}

public sealed class WorkerSupervisor : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private string? _sessionId;
    private int _maxLineBytes = 1_048_576;
    private int _operationTimeoutMilliseconds = 120_000;
    private int _shutdownTimeoutMilliseconds = 10_000;
    private long _maxWorkerPrivateBytes = long.MaxValue;
    private IntPtr _jobHandle;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcResponse?>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _ignoredResponses = new(StringComparer.Ordinal);
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private string[] _secretVariants = [];

    public string? SessionId => _sessionId;
    public bool IsRunning => _process is { HasExited: false };

    public async Task<WorkerCallResult> StartAsync(string sessionId, GameConfig config, CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前运行时");
        var isDotnet = Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var entry = Assembly.GetEntryAssembly()?.Location;
        if (isDotnet && string.IsNullOrWhiteSpace(entry)) throw new InvalidOperationException("无法定位当前程序集");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            Arguments = isDotnet ? $"\"{entry}\" --worker" : "--worker",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data)) Console.Error.WriteLine($"worker[{sessionId}] {Redact(eventArgs.Data)}");
        };
        if (!process.Start()) return WorkerCallResult.Failed(new ToolError { Code = "WORKER_FAILED", Message = "无法启动 Worker", Retryable = true });
        process.BeginErrorReadLine();
        _process = process;
        _input = process.StandardInput;
        _output = process.StandardOutput;
        _sessionId = sessionId;
        if (!TryAttachJob(process, out var jobError))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            _process = null;
            _input = null;
            _output = null;
            _sessionId = null;
            return WorkerCallResult.Failed(new ToolError { Code = "WORKER_FAILED", Message = jobError ?? "无法建立 Worker 进程隔离", Retryable = false, SuggestedAction = "检查 Windows Job Object 权限" });
        }
        _secretVariants = config.AesKeys.Values
            .Append(config.AesKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => new[] { value!, "0x" + value!, "0X" + value! })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ToArray();
        _maxLineBytes = config.Limits.MaxIpcLineBytes;
        _operationTimeoutMilliseconds = checked(config.Limits.OperationTimeoutSeconds * 1000);
        _shutdownTimeoutMilliseconds = checked(config.Limits.ShutdownTimeoutSeconds * 1000);
        _maxWorkerPrivateBytes = config.Limits.MaxWorkerPrivateBytes;
        _readerCts = new CancellationTokenSource();
        _readerTask = Task.Run(ReadResponsesAsync);
        try
        {
            var initPayload = JsonSerializer.SerializeToElement(new WorkerInit { Config = config }, JsonDefaults.Options);
            var result = await SendCoreAsync(new IpcMessage(Guid.NewGuid().ToString("N"), sessionId, "request", "initialize", initPayload), checked(config.Limits.StartupTimeoutSeconds * 1000), cancellationToken).ConfigureAwait(false);
            if (!result.Ok) await StopAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async Task<WorkerCallResult> CallAsync(string operation, object? payload, CancellationToken cancellationToken)
    {
        if (!IsRunning || _sessionId is null) return WorkerCallResult.Failed(new ToolError { Code = "WORKER_FAILED", Message = "Worker 不可用", Retryable = true, SuggestedAction = "调用 remount" });
        var json = payload is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonDefaults.Options);
        return await SendCoreAsync(new IpcMessage(Guid.NewGuid().ToString("N"), _sessionId, "request", operation, json), _operationTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkerCallResult> SendCoreAsync(IpcMessage message, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (_input is null || _output is null || !IsRunning)
            return WorkerCallResult.Failed(new ToolError { Code = "WORKER_FAILED", Message = "Worker 管道未建立", Retryable = true });
        if (MemoryExceeded())
        {
            KillWorker();
            return WorkerCallResult.Failed(new ToolError { Code = "WORKER_MEMORY_LIMIT", Message = "Worker 私有内存超过预算", Retryable = false, SuggestedAction = "缩小范围或 remount" });
        }

        var completion = new TaskCompletionSource<IpcResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(message.Id, completion))
            return WorkerCallResult.Failed(new ToolError { Code = "WORKER_FAILED", Message = "Worker 请求 ID 冲突", Retryable = false });
        try
        {
            var line = JsonSerializer.Serialize(message, JsonDefaults.Options);
            if (Encoding.UTF8.GetByteCount(line) > _maxLineBytes)
                return WorkerCallResult.Failed(new ToolError { Code = "OUTPUT_BUDGET_EXCEEDED", Message = "IPC 请求超过预算", Retryable = false });
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_input is null || !IsRunning) throw new IOException("Worker 管道已关闭");
                await _input.WriteLineAsync(line).ConfigureAwait(false);
                await _input.FlushAsync().ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMilliseconds);
            var response = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (response is null || !string.Equals(response.Id, message.Id, StringComparison.Ordinal) || !string.Equals(response.SessionId, message.SessionId, StringComparison.Ordinal))
            {
                KillWorker();
                return WorkerCallResult.Failed(new ToolError { Code = "WORKER_FAILED", Message = "Worker 响应关联信息无效", Retryable = false });
            }
            if (MemoryExceeded())
            {
                KillWorker();
                return WorkerCallResult.Failed(new ToolError { Code = "WORKER_MEMORY_LIMIT", Message = "Worker 私有内存超过预算", Retryable = false, SuggestedAction = "缩小范围或 remount" });
            }
            return response.Ok || response.Result is not null
                ? WorkerCallResult.Succeeded(response.Result)
                : WorkerCallResult.Failed(response.Error ?? new ToolError { Code = "WORKER_FAILED", Message = "Worker 请求失败", Retryable = true });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_ignoredResponses.Count >= 1024) _ignoredResponses.Clear();
            _ignoredResponses[message.Id] = 0;
            await SendCancellationAsync(message.Id).ConfigureAwait(false);
            return WorkerCallResult.Failed(new ToolError { Code = "OPERATION_CANCELLED", Message = "请求已取消；Worker 将在安全点停止该操作", Retryable = true });
        }
        catch (OperationCanceledException)
        {
            KillWorker();
            return WorkerCallResult.Failed(new ToolError { Code = "OPERATION_TIMEOUT", Message = "Worker 请求超时", Retryable = true, SuggestedAction = "调用 remount 回收失控 Worker" });
        }
        catch (IOException)
        {
            KillWorker();
            return WorkerCallResult.Failed(new ToolError { Code = "WORKER_FAILED", Message = "Worker 管道异常", Retryable = true });
        }
        catch (Exception ex)
        {
            return WorkerCallResult.Failed(new ToolError { Code = "WORKER_FAILED", Message = "Worker 管道异常：" + ex.GetType().Name, Retryable = true });
        }
        finally { _pending.TryRemove(message.Id, out _); }
    }

    private async Task SendCancellationAsync(string requestId)
    {
        if (_input is null || _sessionId is null || !IsRunning) return;
        var cancellation = new IpcMessage(Guid.NewGuid().ToString("N"), _sessionId, "cancel", "cancel_request", JsonSerializer.SerializeToElement(new { requestId }, JsonDefaults.Options));
        try
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_input is not null && IsRunning)
                {
                    await _input.WriteLineAsync(JsonSerializer.Serialize(cancellation, JsonDefaults.Options)).ConfigureAwait(false);
                    await _input.FlushAsync().ConfigureAwait(false);
                }
            }
            finally { _writeGate.Release(); }
        }
        catch { }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Process? process;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            process = _process;
            if (process is { HasExited: false } && _input is not null && _output is not null)
            {
                try
                {
                    var response = await SendCoreAsync(new IpcMessage(Guid.NewGuid().ToString("N"), _sessionId ?? "", "request", "shutdown"), _shutdownTimeoutMilliseconds, CancellationToken.None).ConfigureAwait(false);
                    _ = response;
                }
                catch { }
            }
            if (process is { HasExited: false })
            {
                using var timeout = new CancellationTokenSource();
                timeout.CancelAfter(_shutdownTimeoutMilliseconds);
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch { try { process.Kill(entireProcessTree: true); } catch { } }
            }
            _readerCts?.Cancel();
            if (_readerTask is not null)
            {
                try { await _readerTask.ConfigureAwait(false); } catch { }
            }
            FailPending();
            _ignoredResponses.Clear();
            process?.Dispose();
        }
        finally
        {
            CloseJob();
            _readerCts?.Dispose();
            _readerCts = null;
            _readerTask = null;
            _process = null;
            _input = null;
            _output = null;
            _sessionId = null;
            _secretVariants = [];
            _gate.Release();
        }
    }

    private async Task ReadResponsesAsync()
    {
        try
        {
            if (_output is null || _readerCts is null) return;
            var lines = new BoundedLineReader(_output);
            while (_readerCts is not null)
            {
                string? line;
                try { line = await lines.ReadLineAsync(_maxLineBytes, _readerCts.Token).ConfigureAwait(false); }
                catch (IpcLineTooLongException)
                {
                    FailPending();
                    KillProcessOnly();
                    return;
                }
                if (line is null) break;
                IpcResponse? response;
                try { response = JsonSerializer.Deserialize<IpcResponse>(line, JsonDefaults.Options); }
                catch (JsonException)
                {
                    FailPending();
                    KillProcessOnly();
                    return;
                }
                if (response is null || string.IsNullOrWhiteSpace(response.Id))
                {
                    FailPending();
                    KillProcessOnly();
                    return;
                }
                if (_pending.TryRemove(response.Id, out var completion)) completion.TrySetResult(response);
                else if (!_ignoredResponses.TryRemove(response.Id, out _))
                {
                    FailPending();
                    KillProcessOnly();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_readerCts?.IsCancellationRequested == true) { }
        catch { FailPending(); }
        finally { FailPending(); }
    }

    private void FailPending()
    {
        foreach (var pair in _pending.ToArray())
            if (_pending.TryRemove(pair.Key, out var completion)) completion.TrySetResult(null);
    }

    private void KillProcessOnly()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
    }

    private bool MemoryExceeded()
    {
        try { return _process is { HasExited: false } && _process.PrivateMemorySize64 > _maxWorkerPrivateBytes; }
        catch { return false; }
    }

    private void KillWorker()
    {
        var process = _process;
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch { }
        _readerCts?.Cancel();
        FailPending();
        _ignoredResponses.Clear();
        try { process?.Dispose(); } catch { }
        _process = null;
        _input = null;
        _output = null;
        _sessionId = null;
        CloseJob();
    }

    private bool TryAttachJob(Process process, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows()) return true;
        _jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero) { error = "CreateJobObject 失败"; return false; }
        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x00002000u }
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (!SetInformationJobObject(_jobHandle, 9, pointer, (uint)size)) { error = "SetInformationJobObject 失败"; CloseJob(); return false; }
            if (!AssignProcessToJobObject(_jobHandle, process.Handle)) { error = "AssignProcessToJobObject 失败"; CloseJob(); return false; }
            return true;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private void CloseJob()
    {
        if (_jobHandle == IntPtr.Zero) return;
        try { CloseHandle(_jobHandle); } catch { }
        _jobHandle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private string Redact(string value)
    {
        foreach (var secret in _secretVariants)
            value = value.Replace(secret, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        return value.Length > 4096 ? value[..4096] + "…" : value;
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        _gate.Dispose();
        _writeGate.Dispose();
    }
}

public sealed record WorkerCallResult(bool Ok, JsonElement? Result, ToolError? Error)
{
    public static WorkerCallResult Succeeded(JsonElement? result) => new(true, result, null);
    public static WorkerCallResult Failed(ToolError error) => new(false, null, error);
}
