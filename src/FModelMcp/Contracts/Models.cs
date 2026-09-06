using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace FModelMcp.Contracts;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed class ToolEnvelope
{
    public required bool Ok { get; init; }
    public string? SessionId { get; init; }
    public object? Data { get; init; }
    public PageInfo? Page { get; init; }
    public CoverageInfo? Coverage { get; init; }
    public IReadOnlyList<WarningInfo> Warnings { get; init; } = [];
    public ToolError? Error { get; init; }
}

public sealed record PageInfo(int Limit, int Returned, string? NextCursor);

public sealed class CoverageInfo
{
    public string? SnapshotId { get; init; }
    public string? Scope { get; init; }
    public int? TotalUnits { get; init; }
    public int? SucceededUnits { get; init; }
    public int? FailedUnits { get; init; }
    public int? SkippedUnits { get; init; }
    public bool Complete { get; init; }
    public bool CatalogComplete { get; init; }
    public string? SourceFingerprint { get; init; }
    public object? CapabilityCoverage { get; init; }
}

public sealed record WarningInfo(string Code, string Message, string? Subject = null);

public sealed class ToolError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Subject { get; init; }
    public bool Retryable { get; init; }
    public string? SuggestedAction { get; init; }
    public object? Details { get; init; }
}

[JsonConverter(typeof(ConfigPatchJsonConverter))]
public sealed class ConfigPatch
{
    private readonly HashSet<string> _presentFields = new(StringComparer.Ordinal);
    public string? GameRoot { get; set; }
    public string? UeVersion { get; set; }
    public string? AesKey { get; set; }
    public Dictionary<string, string>? AesKeys { get; set; }
    public string? Usmap { get; set; }
    public string? Language { get; set; }
    public string? ArchiveDir { get; set; }
    public string? OutputRoot { get; set; }
    public string? CacheRoot { get; set; }
    public NativeLibrariesPatch? NativeLibraries { get; set; }
    public LimitsPatch? Limits { get; set; }

    internal void MarkPresent(string name) => _presentFields.Add(name);
    public bool IsPresent(string name) => _presentFields.Contains(name);
}

// 仅供 MCP schema exporter 使用；运行时 ConfigPatch 仍保留 omitted/null 的存在性语义。
public sealed class ConfigPatchSchema
{
    public string? GameRoot { get; set; }
    public string? UeVersion { get; set; }
    public string? AesKey { get; set; }
    public Dictionary<string, string>? AesKeys { get; set; }
    public string? Usmap { get; set; }
    public string? Language { get; set; }
    public string? ArchiveDir { get; set; }
    public string? OutputRoot { get; set; }
    public string? CacheRoot { get; set; }
    public NativeLibrariesPatch? NativeLibraries { get; set; }
    public LimitsPatch? Limits { get; set; }
}

public sealed class ConfigPatchJsonConverter : JsonConverter<ConfigPatch>
{
    public override ConfigPatch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("patch 必须是 object");
        var patch = new ConfigPatch();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            patch.MarkPresent(property.Name);
            switch (property.Name)
            {
                case "gameRoot": patch.GameRoot = property.Value.Deserialize<string?>(options); break;
                case "ueVersion": patch.UeVersion = property.Value.Deserialize<string?>(options); break;
                case "aesKey": patch.AesKey = property.Value.Deserialize<string?>(options); break;
                case "aesKeys": patch.AesKeys = property.Value.Deserialize<Dictionary<string, string>?>(options); break;
                case "usmap": patch.Usmap = property.Value.Deserialize<string?>(options); break;
                case "language": patch.Language = property.Value.Deserialize<string?>(options); break;
                case "archiveDir": patch.ArchiveDir = property.Value.Deserialize<string?>(options); break;
                case "outputRoot": patch.OutputRoot = property.Value.Deserialize<string?>(options); break;
                case "cacheRoot": patch.CacheRoot = property.Value.Deserialize<string?>(options); break;
                case "nativeLibraries": patch.NativeLibraries = property.Value.Deserialize<NativeLibrariesPatch?>(options); break;
                case "limits":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("limits 不能为 null");
                    if (property.Value.ValueKind != JsonValueKind.Object || property.Value.EnumerateObject().Any(item => item.Value.ValueKind == JsonValueKind.Null)) throw new JsonException("limits 字段不能为 null");
                    patch.Limits = property.Value.Deserialize<LimitsPatch?>(options);
                    break;
                default: throw new JsonException($"patch 包含未知字段: {property.Name}");
            }
        }
        return patch;
    }

    public override void Write(Utf8JsonWriter writer, ConfigPatch value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        Write(writer, "gameRoot", value.GameRoot, options);
        Write(writer, "ueVersion", value.UeVersion, options);
        Write(writer, "aesKey", value.AesKey, options);
        Write(writer, "aesKeys", value.AesKeys, options);
        Write(writer, "usmap", value.Usmap, options);
        Write(writer, "language", value.Language, options);
        Write(writer, "archiveDir", value.ArchiveDir, options);
        Write(writer, "outputRoot", value.OutputRoot, options);
        Write(writer, "cacheRoot", value.CacheRoot, options);
        Write(writer, "nativeLibraries", value.NativeLibraries, options);
        Write(writer, "limits", value.Limits, options);
        writer.WriteEndObject();
    }

    private static void Write<T>(Utf8JsonWriter writer, string name, T? value, JsonSerializerOptions options)
    {
        if (value is null) return;
        writer.WritePropertyName(name);
        JsonSerializer.Serialize(writer, value, options);
    }
}

public sealed class NativeLibrariesPatch
{
    public string? OodlePath { get; init; }
}

public sealed class LimitsPatch
{
    public int? DefaultPageSize { get; init; }
    public int? MaxPageSize { get; init; }
    public long? MaxToolResultBytes { get; init; }
    public long? MaxProjectionBytes { get; init; }
    public int? MaxDepth { get; init; }
    public int? MaxVisitedJsonTokens { get; init; }
    public int? MaxIpcLineBytes { get; init; }
    public long? MaxPackageReadBytes { get; init; }
    public long? MaxWorkerPrivateBytes { get; init; }
    public int? OperationTimeoutSeconds { get; init; }
    public int? StartupTimeoutSeconds { get; init; }
    public int? ShutdownTimeoutSeconds { get; init; }
    public int? RegexTimeoutMilliseconds { get; init; }
    public int? MaxQueuedRequests { get; init; }
    public long? MaxExportBytes { get; init; }
}

public sealed class GameConfig
{
    public int SchemaVersion { get; init; } = 1;
    public required string GameRoot { get; init; }
    public required string UeVersion { get; init; }
    public string? AesKey { get; init; }
    public Dictionary<string, string> AesKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Usmap { get; init; }
    public string Language { get; init; } = "zh-Hans";
    public string ArchiveDir { get; init; } = "b1/Content/Paks";
    public required string OutputRoot { get; init; }
    public required string CacheRoot { get; init; }
    public NativeLibrariesConfig NativeLibraries { get; init; } = new();
    public LimitsConfig Limits { get; init; } = LimitsConfig.Default;
}

public sealed class NativeLibrariesConfig
{
    public string? OodlePath { get; init; }
}

public sealed class LimitsConfig
{
    public int DefaultPageSize { get; init; } = 50;
    public int MaxPageSize { get; init; } = 200;
    public long MaxToolResultBytes { get; init; } = 524_288;
    public long MaxProjectionBytes { get; init; } = 196_608;
    public int MaxDepth { get; init; } = 32;
    public int MaxVisitedJsonTokens { get; init; } = 200_000;
    public int MaxIpcLineBytes { get; init; } = 1_048_576;
    public long MaxPackageReadBytes { get; init; } = 536_870_912;
    public long MaxWorkerPrivateBytes { get; init; } = 4_294_967_296;
    public int OperationTimeoutSeconds { get; init; } = 120;
    public int StartupTimeoutSeconds { get; init; } = 300;
    public int ShutdownTimeoutSeconds { get; init; } = 10;
    public int RegexTimeoutMilliseconds { get; init; } = 200;
    public int MaxQueuedRequests { get; init; } = 16;
    public long MaxExportBytes { get; init; } = 2_147_483_648;

    public static LimitsConfig Default { get; } = new();
}

public sealed class TaskReceipt
{
    public required string TaskId { get; init; }
    public required string Kind { get; init; }
    public required string State { get; init; }
    public required string SessionId { get; init; }
    public string? Scope { get; init; }
}

public sealed class TaskInfo
{
    public required string TaskId { get; init; }
    public required string Kind { get; init; }
    public required string State { get; set; }
    public required string SessionId { get; init; }
    public string? Scope { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string? Phase { get; set; }
    public string? CurrentSubject { get; set; }
    public int? TotalUnits { get; set; }
    public int ProcessedUnits { get; set; }
    public int SucceededUnits { get; set; }
    public int FailedUnits { get; set; }
    public int SkippedUnits { get; set; }
    public bool CancelRequested { get; set; }
    public bool CanInterruptCurrentUnit { get; set; } = true;
    public bool Stale { get; set; }
    public int ErrorsVersion { get; set; }
    public string? SnapshotId { get; set; }
    public string? SourceFingerprint { get; set; }
    public object? Result { get; set; }
    public List<TaskErrorInfo> Errors { get; } = [];
    public CancellationTokenSource Cancellation { get; } = new();
}

public sealed record TaskErrorInfo(int Sequence, string Subject, string Stage, string Code, string Message);

public sealed record IpcMessage(
    string Id,
    string SessionId,
    string Kind,
    string Operation,
    JsonElement? Payload = null);

public sealed class IpcResponse
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required bool Ok { get; init; }
    public JsonElement? Result { get; init; }
    public ToolError? Error { get; init; }
}

public static class ToolResults
{
    public static CallToolResult Success(string? sessionId, object? data = null, PageInfo? page = null,
        CoverageInfo? coverage = null, IReadOnlyList<WarningInfo>? warnings = null, long? maxResultBytes = null)
        => Create(new ToolEnvelope
        {
            Ok = true,
            SessionId = sessionId,
            Data = data,
            Page = page,
            Coverage = coverage,
            Warnings = warnings ?? []
        }, maxResultBytes);

    public static CallToolResult Failure(string? sessionId, string code, string message,
        bool retryable = false, string? suggestedAction = null, object? details = null,
        object? data = null, PageInfo? page = null, CoverageInfo? coverage = null,
        IReadOnlyList<WarningInfo>? warnings = null, string? subject = null, long? maxResultBytes = null)
        => Create(new ToolEnvelope
        {
            Ok = false,
            SessionId = sessionId,
            Data = data,
            Page = page,
            Coverage = coverage,
            Warnings = warnings ?? [],
            Error = new ToolError
            {
                Code = code,
                Message = message,
                Subject = subject,
                Retryable = retryable,
                SuggestedAction = suggestedAction,
                Details = details
            }
        }, maxResultBytes);

    public static CallToolResult FromWorker(string? sessionId, JsonElement response, long? maxResultBytes = null)
    {
        try
        {
            var json = response.GetRawText();
            using var document = JsonDocument.Parse(json);
            var envelope = document.RootElement.Deserialize<ToolEnvelope>(JsonDefaults.Options);
            if (envelope is null)
                envelope = new ToolEnvelope { Ok = false, SessionId = sessionId, Error = new ToolError { Code = "WORKER_FAILED", Message = "Worker returned an invalid envelope" } };
            return Create(envelope, maxResultBytes);
        }
        catch (JsonException)
        {
            return Failure(sessionId, "WORKER_FAILED", "Worker 返回的结果包络无效", false, "调用 remount", maxResultBytes: maxResultBytes);
        }
    }

    private static CallToolResult Create(ToolEnvelope envelope, long? maxResultBytes = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        if (maxResultBytes is > 0 && bytes.LongLength > maxResultBytes.Value / 2)
        {
            envelope = new ToolEnvelope
            {
                Ok = false,
                SessionId = envelope.SessionId,
                Error = new ToolError
                {
                    Code = "OUTPUT_BUDGET_EXCEEDED",
                    Message = "最终 MCP 结果超过 maxToolResultBytes",
                    Retryable = false,
                    SuggestedAction = "缩小 limit、fields 或改用导出"
                }
            };
            bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        }
        using var document = JsonDocument.Parse(bytes);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        return new CallToolResult
        {
            StructuredContent = document.RootElement.Clone(),
            Content = [new TextContentBlock { Text = text }],
            IsError = !envelope.Ok
        };
    }
}
