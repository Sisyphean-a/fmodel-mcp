using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FModelMcp.Contracts;

namespace FModelMcp.Config;

public sealed class ConfigService
{
    public string? ConfigPath { get; }
    public GameConfig? Current { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string? LastErrorMessage { get; private set; }

    public ConfigService(string? configPath)
    {
        ConfigPath = string.IsNullOrWhiteSpace(configPath) ? null : Path.GetFullPath(configPath);
    }

    public void Load()
    {
        if (ConfigPath is null || !File.Exists(ConfigPath))
            return;

        try
        {
            var config = JsonSerializer.Deserialize<GameConfig>(File.ReadAllText(ConfigPath), JsonDefaults.Options);
            if (config is null)
            {
                SetError("CONFIG_INVALID", "配置文件为空");
                return;
            }

            var result = Validate(config);
            if (!result.IsValid)
            {
                SetError("CONFIG_INVALID", result.Message ?? "配置无效");
                return;
            }

            Current = result.Config;
            ClearError();
        }
        catch (JsonException ex)
        {
            SetError("CONFIG_INVALID", $"配置 JSON 无法解析：{ex.Message}");
        }
        catch (IOException ex)
        {
            SetError("CONFIG_INVALID", $"配置文件无法读取：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            SetError("CONFIG_INVALID", $"配置文件无法读取：{ex.Message}");
        }
    }

    public ConfigOperationResult BuildCandidate(ConfigPatch patch)
    {
        if ((patch.IsPresent("gameRoot") && patch.GameRoot is null) || (patch.IsPresent("ueVersion") && patch.UeVersion is null) ||
            (patch.IsPresent("language") && patch.Language is null) || (patch.IsPresent("archiveDir") && patch.ArchiveDir is null) ||
            (patch.IsPresent("outputRoot") && patch.OutputRoot is null) || (patch.IsPresent("cacheRoot") && patch.CacheRoot is null) ||
            (patch.IsPresent("aesKeys") && patch.AesKeys is null) || (patch.IsPresent("nativeLibraries") && patch.NativeLibraries is null) ||
            (patch.IsPresent("limits") && patch.Limits is null))
            return ConfigOperationResult.Invalid("不可空配置字段不能为 null");

        var current = Current;
        Dictionary<string, string> aesKeys;
        try
        {
            aesKeys = patch.IsPresent("aesKeys") || patch.AesKeys is not null
                ? NormalizeAesKeys(patch.AesKeys!)
                : NormalizeAesKeys(current?.AesKeys ?? []);
        }
        catch (ArgumentException)
        {
            return ConfigOperationResult.Invalid("aesKeys 含有重复 GUID");
        }
        var gameRoot = patch.IsPresent("gameRoot") || patch.GameRoot is not null ? patch.GameRoot : current?.GameRoot;
        var ueVersion = patch.IsPresent("ueVersion") || patch.UeVersion is not null ? patch.UeVersion : current?.UeVersion;
        var aesKey = patch.IsPresent("aesKey") || patch.AesKey is not null ? patch.AesKey : current?.AesKey;
        var usmap = patch.IsPresent("usmap") || patch.Usmap is not null ? patch.Usmap : current?.Usmap;
        var language = patch.IsPresent("language") || patch.Language is not null ? patch.Language : current?.Language;
        var archiveDir = patch.IsPresent("archiveDir") || patch.ArchiveDir is not null ? patch.ArchiveDir : current?.ArchiveDir;
        var outputRoot = patch.IsPresent("outputRoot") || patch.OutputRoot is not null ? patch.OutputRoot : current?.OutputRoot;
        var cacheRoot = patch.IsPresent("cacheRoot") || patch.CacheRoot is not null ? patch.CacheRoot : current?.CacheRoot;
        var candidate = new GameConfig
        {
            SchemaVersion = 1,
            GameRoot = NormalizePath(gameRoot),
            UeVersion = ueVersion ?? string.Empty,
            AesKey = NormalizeAes(aesKey),
            AesKeys = aesKeys,
            Usmap = NormalizeNullablePath(usmap),
            Language = language ?? "zh-Hans",
            ArchiveDir = archiveDir ?? "b1/Content/Paks",
            OutputRoot = NormalizePath(outputRoot),
            CacheRoot = NormalizePath(cacheRoot),
            NativeLibraries = patch.IsPresent("nativeLibraries") || patch.NativeLibraries is not null
                ? new NativeLibrariesConfig { OodlePath = NormalizeNullablePath(patch.NativeLibraries!.OodlePath) }
                : current?.NativeLibraries ?? new NativeLibrariesConfig(),
            Limits = patch.IsPresent("limits") || patch.Limits is not null
                ? ApplyLimitsPatch(patch.Limits!)
                : current?.Limits ?? LimitsConfig.Default
        };

        var validation = Validate(candidate);
        return validation.IsValid && validation.Config is not null
            ? ConfigOperationResult.Valid(validation.Config)
            : ConfigOperationResult.Invalid(validation.Message ?? "配置无效");
    }

    public async Task<ConfigPrepareResult> PreparePersistAsync(GameConfig candidate, CancellationToken cancellationToken)
    {
        if (ConfigPath is null)
            return ConfigPrepareResult.Failed("CONFIG_PERSIST_FAILED", "启动时未指定 --config，无法持久化");

        var directory = Path.GetDirectoryName(ConfigPath);
        if (string.IsNullOrWhiteSpace(directory))
            return ConfigPrepareResult.Failed("CONFIG_PERSIST_FAILED", "配置文件目录无效");

        string? temporary = null;
        try
        {
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(ConfigPath)}.{Guid.NewGuid():N}.tmp");
            var json = JsonSerializer.Serialize(candidate, JsonDefaults.Options);
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.SequentialScan))
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            var preparation = new ConfigPersistencePreparation(temporary, ConfigPath);
            temporary = null;
            return ConfigPrepareResult.Prepared(preparation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConfigPrepareResult.Failed("CONFIG_PERSIST_FAILED", $"配置持久化失败：{ex.Message}");
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    public ConfigPersistResult CommitPrepared(ConfigPersistencePreparation preparation)
    {
        try
        {
            if (File.Exists(preparation.TargetPath))
                File.Replace(preparation.TemporaryPath, preparation.TargetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(preparation.TemporaryPath, preparation.TargetPath);
            preparation.Committed = true;
            return ConfigPersistResult.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConfigPersistResult.Failed("CONFIG_PERSIST_FAILED", $"配置持久化失败：{ex.Message}");
        }
    }

    public void CleanupPrepared(ConfigPersistencePreparation? preparation)
    {
        if (preparation is null || preparation.Committed) return;
        try { File.Delete(preparation.TemporaryPath); } catch { }
    }

    public async Task<ConfigPersistResult> PersistAsync(GameConfig candidate, CancellationToken cancellationToken)
    {
        var prepared = await PreparePersistAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!prepared.Succeeded || prepared.Preparation is null)
            return ConfigPersistResult.Failed(prepared.Code ?? "CONFIG_PERSIST_FAILED", prepared.Message ?? "配置持久化准备失败");
        try { return CommitPrepared(prepared.Preparation); }
        finally { CleanupPrepared(prepared.Preparation); }
    }

    public void SetCurrent(GameConfig config)
    {
        Current = config;
        ClearError();
    }

    public object? ToSafeSummary(GameConfig? config = null)
    {
        config ??= Current;
        if (config is null) return null;

        return new
        {
            schemaVersion = config.SchemaVersion,
            gameRoot = config.GameRoot,
            ueVersion = config.UeVersion,
            aesConfigured = !string.IsNullOrWhiteSpace(config.AesKey) || config.AesKeys.Count > 0,
            aesKeyGuidCount = config.AesKeys.Count + (string.IsNullOrWhiteSpace(config.AesKey) ? 0 : 1),
            usmap = config.Usmap,
            language = config.Language,
            archiveDir = config.ArchiveDir,
            outputRoot = config.OutputRoot,
            cacheRoot = config.CacheRoot,
            nativeLibraries = new { oodleConfigured = !string.IsNullOrWhiteSpace(config.NativeLibraries.OodlePath) },
            limits = config.Limits
        };
    }

    public static ConfigValidationResult Validate(GameConfig config)
    {
        try { return ValidateCore(config); }
        catch (ArgumentException ex) { return ConfigValidationResult.Invalid($"配置路径无效：{ex.Message}"); }
        catch (IOException ex) { return ConfigValidationResult.Invalid($"配置路径无法访问：{ex.Message}"); }
    }

    private static ConfigValidationResult ValidateCore(GameConfig config)
    {
        var normalized = Normalize(config);
        if (normalized.SchemaVersion != 1)
            return ConfigValidationResult.Invalid("schemaVersion 必须为 1");
        if (!string.Equals(normalized.UeVersion, "GAME_BlackMythWukong", StringComparison.Ordinal))
            return ConfigValidationResult.Invalid("ueVersion 必须精确为 GAME_BlackMythWukong");
        if (normalized.Language is not ("zh-Hans" or "en"))
            return ConfigValidationResult.Invalid("language 只支持 zh-Hans 或 en");

        if (!IsAbsolute(normalized.GameRoot) || !Directory.Exists(normalized.GameRoot) || HasReparsePointInExistingPath(normalized.GameRoot) || !CanReadDirectory(normalized.GameRoot))
            return ConfigValidationResult.Invalid("gameRoot 必须是存在且可读取的、非联接绝对目录");
        if (!IsAbsolute(normalized.OutputRoot) || !IsAbsolute(normalized.CacheRoot))
            return ConfigValidationResult.Invalid("outputRoot 和 cacheRoot 必须是绝对路径");
        if (HasReparsePointInExistingPath(normalized.OutputRoot) || HasReparsePointInExistingPath(normalized.CacheRoot))
            return ConfigValidationResult.Invalid("outputRoot 和 cacheRoot 不得穿过联接或符号链接");
        if (Directory.Exists(normalized.OutputRoot) && !CanReadDirectory(normalized.OutputRoot) || Directory.Exists(normalized.CacheRoot) && !CanReadDirectory(normalized.CacheRoot))
            return ConfigValidationResult.Invalid("outputRoot 和 cacheRoot 已存在时必须可读取");
        if (Overlaps(normalized.GameRoot, normalized.OutputRoot) || Overlaps(normalized.GameRoot, normalized.CacheRoot) || Overlaps(normalized.OutputRoot, normalized.CacheRoot))
            return ConfigValidationResult.Invalid("gameRoot、outputRoot、cacheRoot 不得互相重叠");
        if (string.IsNullOrWhiteSpace(normalized.ArchiveDir) || ContainsParentSegment(normalized.ArchiveDir) || Path.IsPathRooted(normalized.ArchiveDir) || normalized.ArchiveDir.Contains(':'))
            return ConfigValidationResult.Invalid("archiveDir 必须是 gameRoot 内的非空相对目录，且不得包含 .. 或驱动器前缀");

        var archivePath = Path.GetFullPath(Path.Combine(normalized.GameRoot, normalized.ArchiveDir.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(normalized.GameRoot, archivePath) || !Directory.Exists(archivePath) || HasReparsePointInExistingPath(archivePath) || !CanReadDirectory(archivePath))
            return ConfigValidationResult.Invalid("archiveDir 必须位于 gameRoot 内、存在且可读取且不得穿过联接");

        if (!string.IsNullOrWhiteSpace(normalized.Usmap) && (!IsAbsolute(normalized.Usmap) || !File.Exists(normalized.Usmap) || HasReparsePointInExistingPath(normalized.Usmap) || !CanReadFile(normalized.Usmap)))
            return ConfigValidationResult.Invalid("usmap 非空时必须是可读取的、非联接绝对文件路径");
        if (!string.IsNullOrWhiteSpace(normalized.NativeLibraries.OodlePath) && (!IsAbsolute(normalized.NativeLibraries.OodlePath) || !File.Exists(normalized.NativeLibraries.OodlePath) || HasReparsePointInExistingPath(normalized.NativeLibraries.OodlePath) || !CanReadFile(normalized.NativeLibraries.OodlePath)))
            return ConfigValidationResult.Invalid("nativeLibraries.oodlePath 非空时必须是可读取的、非联接绝对文件路径");

        if (!ValidateAes(normalized.AesKey))
            return ConfigValidationResult.Invalid("aesKey 必须是 64 个十六进制字符，可带 0x 前缀");
        foreach (var pair in normalized.AesKeys)
        {
            if (!Guid.TryParse(pair.Key, out _))
                return ConfigValidationResult.Invalid("aesKeys 的 GUID 键无效");
            if (string.IsNullOrWhiteSpace(pair.Value) || !ValidateAes(pair.Value))
                return ConfigValidationResult.Invalid("aesKeys 的值必须是 64 个十六进制字符，可带 0x 前缀");
        }
        if (normalized.AesKeys.Keys.Any(key => Guid.TryParse(key, out var guid) && guid == Guid.Empty) && normalized.AesKey is not null)
            return ConfigValidationResult.Invalid("aesKey 与 aesKeys 的零 GUID 重复");
        if (!ValidateLimits(normalized.Limits, out var limitError))
            return ConfigValidationResult.Invalid(limitError!);

        return ConfigValidationResult.Valid(normalized);
    }

    private static GameConfig Normalize(GameConfig config)
        => new()
        {
            SchemaVersion = config.SchemaVersion,
            GameRoot = NormalizePath(config.GameRoot),
            UeVersion = config.UeVersion,
            AesKey = NormalizeAes(config.AesKey),
            AesKeys = NormalizeAesKeys(config.AesKeys ?? []),
            Usmap = NormalizeNullablePath(config.Usmap),
            Language = config.Language ?? "zh-Hans",
            ArchiveDir = (config.ArchiveDir ?? "b1/Content/Paks").Replace('\\', '/').Trim('/'),
            OutputRoot = NormalizePath(config.OutputRoot),
            CacheRoot = NormalizePath(config.CacheRoot),
            NativeLibraries = config.NativeLibraries is null ? new NativeLibrariesConfig() : new NativeLibrariesConfig { OodlePath = NormalizeNullablePath(config.NativeLibraries.OodlePath) },
            Limits = config.Limits ?? LimitsConfig.Default
        };

    private static LimitsConfig ApplyLimitsPatch(LimitsPatch patch)
        => new()
        {
            DefaultPageSize = patch.DefaultPageSize ?? LimitsConfig.Default.DefaultPageSize,
            MaxPageSize = patch.MaxPageSize ?? LimitsConfig.Default.MaxPageSize,
            MaxToolResultBytes = patch.MaxToolResultBytes ?? LimitsConfig.Default.MaxToolResultBytes,
            MaxProjectionBytes = patch.MaxProjectionBytes ?? LimitsConfig.Default.MaxProjectionBytes,
            MaxDepth = patch.MaxDepth ?? LimitsConfig.Default.MaxDepth,
            MaxVisitedJsonTokens = patch.MaxVisitedJsonTokens ?? LimitsConfig.Default.MaxVisitedJsonTokens,
            MaxIpcLineBytes = patch.MaxIpcLineBytes ?? LimitsConfig.Default.MaxIpcLineBytes,
            MaxPackageReadBytes = patch.MaxPackageReadBytes ?? LimitsConfig.Default.MaxPackageReadBytes,
            MaxWorkerPrivateBytes = patch.MaxWorkerPrivateBytes ?? LimitsConfig.Default.MaxWorkerPrivateBytes,
            OperationTimeoutSeconds = patch.OperationTimeoutSeconds ?? LimitsConfig.Default.OperationTimeoutSeconds,
            StartupTimeoutSeconds = patch.StartupTimeoutSeconds ?? LimitsConfig.Default.StartupTimeoutSeconds,
            ShutdownTimeoutSeconds = patch.ShutdownTimeoutSeconds ?? LimitsConfig.Default.ShutdownTimeoutSeconds,
            RegexTimeoutMilliseconds = patch.RegexTimeoutMilliseconds ?? LimitsConfig.Default.RegexTimeoutMilliseconds,
            MaxQueuedRequests = patch.MaxQueuedRequests ?? LimitsConfig.Default.MaxQueuedRequests,
            MaxExportBytes = patch.MaxExportBytes ?? LimitsConfig.Default.MaxExportBytes
        };

    private static string NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private static string? NormalizeNullablePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path);

    private static Dictionary<string, string> NormalizeAesKeys(IEnumerable<KeyValuePair<string, string>> values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            var key = pair.Key.Trim();
            if (Guid.TryParse(key, out var guid)) key = guid.ToString("D");
            result.Add(key, NormalizeAes(pair.Value) ?? string.Empty);
        }
        return result;
    }

    private static string? NormalizeAes(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var value = key.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return value.ToUpperInvariant();
    }

    private static bool IsAbsolute(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool ContainsParentSegment(string path)
        => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or "..");

    private static bool IsWithin(string parent, string child)
    {
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedChild = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedParent, normalizedChild, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Overlaps(string left, string right)
        => string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
           IsWithin(left, right) || IsWithin(right, left);

    private static bool ValidateAes(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return true;
        key = key.Trim();
        key = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key;
        return key.Length == 64 && key.All(Uri.IsHexDigit);
    }

    private static bool CanReadDirectory(string path)
    {
        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            _ = enumerator.MoveNext();
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool CanReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.CanRead;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool HasReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static bool HasReparsePointInExistingPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (root is null) return true;
            var current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current += Path.DirectorySeparatorChar + segment;
                if (!Directory.Exists(current) && !File.Exists(current)) break;
                if (HasReparsePoint(current)) return true;
            }
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
        catch (ArgumentException) { return true; }
    }

    private static bool ValidateLimits(LimitsConfig limits, out string? error)
    {
        error = null;
        if (limits.DefaultPageSize <= 0 || limits.MaxPageSize < limits.DefaultPageSize || limits.MaxPageSize > 10_000 ||
            limits.MaxToolResultBytes <= 0 || limits.MaxProjectionBytes <= 0 || limits.MaxDepth < 0 || limits.MaxDepth > 256 ||
            limits.MaxVisitedJsonTokens <= 0 || limits.MaxIpcLineBytes <= 0 || limits.MaxIpcLineBytes > 16 * 1024 * 1024 || limits.MaxPackageReadBytes <= 0 ||
            limits.MaxWorkerPrivateBytes <= 0 || limits.OperationTimeoutSeconds <= 0 || limits.OperationTimeoutSeconds > 86_400 ||
            limits.StartupTimeoutSeconds <= 0 || limits.StartupTimeoutSeconds > 86_400 || limits.ShutdownTimeoutSeconds <= 0 || limits.ShutdownTimeoutSeconds > 600 ||
            limits.RegexTimeoutMilliseconds <= 0 || limits.RegexTimeoutMilliseconds > 60_000 || limits.MaxQueuedRequests <= 0 ||
            limits.MaxExportBytes <= 0)
        {
            error = "limits 中的预算必须为正数，且 maxPageSize 不得小于 defaultPageSize";
            return false;
        }
        return true;
    }

    private void SetError(string code, string message)
    {
        LastErrorCode = code;
        LastErrorMessage = message;
        Current = null;
    }

    private void ClearError()
    {
        LastErrorCode = null;
        LastErrorMessage = null;
    }
}

public sealed record ConfigValidationResult(bool IsValid, GameConfig? Config, string? Message)
{
    public static ConfigValidationResult Valid(GameConfig config) => new(true, config, null);
    public static ConfigValidationResult Invalid(string message) => new(false, null, message);
}

public sealed record ConfigOperationResult(bool IsValid, GameConfig? Config, string? Message)
{
    public static ConfigOperationResult Invalid(string message) => new(false, null, message);
    public static ConfigOperationResult Valid(GameConfig config) => new(true, config, null);
}

public sealed class ConfigPersistencePreparation
{
    public ConfigPersistencePreparation(string temporaryPath, string targetPath)
    {
        TemporaryPath = temporaryPath;
        TargetPath = targetPath;
    }

    public string TemporaryPath { get; }
    public string TargetPath { get; }
    public bool Committed { get; set; }
}

public sealed record ConfigPrepareResult(bool Succeeded, ConfigPersistencePreparation? Preparation, string? Code, string? Message)
{
    public static ConfigPrepareResult Prepared(ConfigPersistencePreparation preparation) => new(true, preparation, null, null);
    public static ConfigPrepareResult Failed(string code, string message) => new(false, null, code, message);
}

public sealed record ConfigPersistResult(bool Succeeded, string? Code, string? Message)
{
    public static ConfigPersistResult Success() => new(true, null, null);
    public static ConfigPersistResult Failed(string code, string message) => new(false, code, message);
}
