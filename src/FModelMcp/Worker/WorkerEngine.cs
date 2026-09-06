using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JsonSerializer = System.Text.Json.JsonSerializer;
using CUE4Parse;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Internationalization;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Engine.Curves;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.Utils;
using FModelMcp.Config;
using FModelMcp.Contracts;
using FModelMcp.Indexing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Data.Sqlite;

namespace FModelMcp.Worker;

public sealed class WorkerInit
{
    public required GameConfig Config { get; init; }
}

public sealed class WorkerEngine : IAsyncDisposable
{
    private readonly SemaphoreSlim _parseGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly TaskRegistry _tasks = new();
    private readonly List<WarningInfo> _warnings = [];
    private PakOnlyFileProvider? _provider;
    private Catalog? _catalog;
    private IndexStore? _indexStore;
    private FileStream? _cacheLock;
    private string _sessionId = string.Empty;
    private GameConfig _config = null!;
    private string? _sourceFingerprint;
    private string _mountState = "unmounted";
    private string? _mappingError;
    private bool _mappingRequiredObserved;
    private int _foregroundParsingRequests;
    private bool _initialized;
    private bool _faulted;
    private readonly ConcurrentDictionary<string, string> _archiveDiagnostics = new(StringComparer.OrdinalIgnoreCase);

    public string SessionId => _sessionId;
    public string? SourceFingerprint => _sourceFingerprint;
    public string MountState => _mountState;
    public bool IsInitialized => _initialized;

    public async Task<object> InitializeAsync(string sessionId, WorkerInit init, CancellationToken cancellationToken)
    {
        _sessionId = sessionId;
        _config = init.Config;
        _initialized = false;
        _faulted = false;
        _mountState = "unmounted";
        _warnings.Clear();
        _archiveDiagnostics.Clear();
        _mappingRequiredObserved = false;
        var cacheDirectoryReady = false;

        try
        {
            Directory.CreateDirectory(_config.CacheRoot);
            cacheDirectoryReady = true;
            var gameId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_config.GameRoot)))).ToLowerInvariant();
            var lockPath = Path.Combine(_config.CacheRoot, gameId + ".lock");
            _cacheLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _indexStore = new IndexStore(Path.Combine(_config.CacheRoot, gameId + ".index-v1.sqlite"));
            _tasks.Attach(_indexStore);

            Globals.FatalObjectSerializationErrors = true;
            await InitializeOodleAsync(cancellationToken).ConfigureAwait(false);
            var versions = new VersionContainer(EGame.GAME_BlackMythWukong);
            _provider = new PakOnlyFileProvider(
                Path.Combine(_config.GameRoot, _config.ArchiveDir.Replace('/', Path.DirectorySeparatorChar)),
                versions,
                StringComparer.OrdinalIgnoreCase);
            _provider.VfsMountFailed += OnVfsMountFailed;
            _provider.UseLazyPackageSerialization = true;
            _provider.ReadScriptData = true;
            _provider.ReadShaderMaps = false;
            _provider.ReadNaniteData = false;

            if (!string.IsNullOrWhiteSpace(_config.Usmap))
            {
                try
                {
                    _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(_config.Usmap, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    _mappingError = ex.GetType().Name;
                    _warnings.Add(new WarningInfo("MAPPINGS_LOAD_FAILED", "usmap 无法加载", _mappingError));
                }
            }

            _provider.Initialize();
            foreach (var diagnostic in _provider.DiscoveryDiagnostics)
            {
                var separator = diagnostic.LastIndexOf(':');
                if (separator > 0)
                {
                    var path = diagnostic[..separator];
                    var detail = diagnostic[(separator + 1)..];
                    _archiveDiagnostics.TryAdd(Path.GetFullPath(path), detail.Equals("REPARSE_POINT", StringComparison.OrdinalIgnoreCase) ? "PATH_NOT_ALLOWED" : "MOUNT_FAILED");
                }
            }
            foreach (var diagnostic in _provider.DiscoveryDiagnostics.Take(20))
                _warnings.Add(new WarningInfo("ARCHIVE_DISCOVERY_FAILED", "pak 发现或注册失败", diagnostic));
            if (_provider.UnsupportedArchives.Count > 0)
                _warnings.Add(new WarningInfo("UNSUPPORTED_CONTAINER", "发现 v1 不支持的 IoStore 容器", _provider.UnsupportedArchives.Count.ToString(CultureInfo.InvariantCulture)));
            _provider.Mount();
            await SubmitConfiguredKeysAsync(cancellationToken).ConfigureAwait(false);
            if (_provider.RequiredKeys.Count > 0)
                _warnings.Add(new WarningInfo(string.IsNullOrWhiteSpace(_config.AesKey) && _config.AesKeys.Count == 0 ? "AES_KEY_REQUIRED" : "AES_KEY_REJECTED", "仍有容器需要未接受的 AES key", _provider.RequiredKeys.Count.ToString(CultureInfo.InvariantCulture)));
            _provider.PostMount();
            if (!_provider.TryChangeCulture(_config.Language))
                _warnings.Add(new WarningInfo("LOCALIZATION_NOT_AVAILABLE", "当前语言没有可用的文化映射；保留源文本证据", _config.Language));
            _catalog = Catalog.Build(_provider, _config.GameRoot);
            _sourceFingerprint = ComputeSourceFingerprint(_config, _provider, _catalog);
            _indexStore.SetSourceFingerprint(_sourceFingerprint);
            foreach (var file in _catalog.Files.Where(file => file.IsPackage))
                file.AssetTypes = _indexStore.GetAssetTypes(file.PackageKey);
            RecoverExports();
            _mountState = DetermineMountState();
            _initialized = true;
            return StatusEnvelope();
        }
        catch (IOException ex) when (cacheDirectoryReady && _cacheLock is null && IsSharingViolation(ex))
        {
            _faulted = true;
            _mountState = "failed";
            return ErrorEnvelope("CACHE_LOCKED", "当前游戏缓存已被另一个 Worker 占用", true, "关闭其他会话后重试");
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException or SqliteException) && _indexStore is null)
        {
            _faulted = true;
            _mountState = "failed";
            _warnings.Add(new WarningInfo("CACHE_IO_FAILED", "索引缓存无法读写", ex.GetType().Name));
            return ErrorEnvelope("CACHE_IO_FAILED", "索引缓存无法读写", true, "检查 cacheRoot 权限和磁盘空间");
        }
        catch (Exception ex)
        {
            _faulted = true;
            _mountState = "failed";
            _warnings.Add(new WarningInfo("WORKER_INIT_FAILED", "Worker 初始化失败", ex.GetType().Name));
            return ErrorEnvelope("MOUNT_FAILED", "解析 Worker 初始化失败", true, "检查游戏目录、AES、usmap 和 Native 依赖");
        }
    }

    public async Task<object> HandleAsync(string operation, JsonElement? payload, CancellationToken cancellationToken)
    {
        if (operation == "get_status") return StatusEnvelope();
        if (operation is not ("get_task" or "cancel_task") && _initialized && SourceChanged())
            return ErrorEnvelope("SOURCE_CHANGED", "已挂载容器的长度或修改时间发生变化", false, "调用 remount 重新建立目录和索引");
        if (!_initialized || _provider is null || _catalog is null)
            return ErrorEnvelope(_faulted ? "WORKER_FAILED" : "SESSION_NOT_READY", "Worker 尚未建立可用目录", true, "先检查 get_status 或重新 remount");

        var foregroundParsing = IsForegroundParsingOperation(operation);
        if (foregroundParsing) Interlocked.Increment(ref _foregroundParsingRequests);
        try
        {
            return operation switch
            {
                "list_archives" => ListArchives(payload),
                "search_files" => await SearchFilesAsync(payload, cancellationToken).ConfigureAwait(false),
                "list_directory" => ListDirectory(payload),
                "list_objects" => await ListObjectsAsync(payload, cancellationToken).ConfigureAwait(false),
                "get_object" => await GetObjectAsync(payload, cancellationToken).ConfigureAwait(false),
                "get_datatable" => await GetDataTableAsync(payload, cancellationToken).ConfigureAwait(false),
                "get_class_info" => await GetClassInfoAsync(payload, cancellationToken).ConfigureAwait(false),
                "get_function" => await GetFunctionAsync(payload, cancellationToken).ConfigureAwait(false),
                "get_string_table" => await GetStringTableAsync(payload, cancellationToken).ConfigureAwait(false),
                "get_curve" => await GetCurveAsync(payload, cancellationToken).ConfigureAwait(false),
                "build_asset_index" => StartIndexTask("asset", payload, cancellationToken),
                "list_asset_types" => ListAssetTypes(payload),
                "search_symbols" => SearchSymbols(payload),
                "build_text_index" => StartIndexTask("text", payload, cancellationToken),
                "search_text" => SearchText(payload),
                "build_reference_index" => StartIndexTask("reference", payload, cancellationToken),
                "find_references" => FindReferences(payload),
                "export_raw" => StartExportTask("exportRaw", payload, cancellationToken),
                "export_json" => StartExportTask("exportJson", payload, cancellationToken),
                "get_task" => GetTask(payload),
                "cancel_task" => CancelTask(payload),
                _ => ErrorEnvelope("INVALID_ARGUMENT", $"未知 Worker operation: {operation}", false, null)
            };
        }
        catch (OperationCanceledException)
        {
            return ErrorEnvelope("OPERATION_TIMEOUT", "操作被取消或超过预算", true, "缩小范围或显式取消任务");
        }
        catch (KeyNotFoundException ex)
        {
            return ErrorEnvelope("FILE_NOT_FOUND", ex.Message, false, "先使用 search_files 或 list_directory");
        }
        catch (CursorException ex)
        {
            return ErrorEnvelope(ex.Code, "游标与当前会话或查询不匹配", false, "重新从第一页开始查询");
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ErrorEnvelope("INVALID_ARGUMENT", "请求参数不是有效的契约 JSON", false, ex.Message);
        }
        catch (FieldSelectionException ex)
        {
            return ErrorEnvelope("FIELD_NOT_FOUND", ex.Message, false, "检查 JSON Pointer");
        }
        catch (CUE4Parse.UE4.Exceptions.MappingException ex)
        {
            _mappingRequiredObserved = true;
            return ErrorEnvelope("MAPPINGS_REQUIRED", "当前对象解析需要可用的 usmap 映射", false, "提供与游戏版本匹配的 usmap", details: new { exceptionType = ex.GetType().Name });
        }
        catch (ArgumentException ex)
        {
            return ErrorEnvelope("INVALID_ARGUMENT", ex.Message, false, "检查虚拟路径、limit 和过滤条件");
        }
        catch (Exception ex)
        {
            _warnings.Add(new WarningInfo("WORKER_OPERATION_FAILED", "操作失败", ex.GetType().Name));
            return ErrorEnvelope(MapExceptionCode(ex), "解析操作失败", true, "检查任务错误或缩小读取范围");
        }
        finally
        {
            if (foregroundParsing) Interlocked.Decrement(ref _foregroundParsingRequests);
        }
    }

    private static bool IsForegroundParsingOperation(string operation)
        => operation is "list_objects" or "get_object" or "get_datatable" or "get_class_info" or "get_function" or "get_string_table" or "get_curve";

    private async Task InitializeOodleAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_config.NativeLibraries.OodlePath))
            {
                await OodleHelper.InitializeAsync(_config.NativeLibraries.OodlePath, cancellationToken).ConfigureAwait(false);
            }
            else if (CUE4ParseNatives.IsFeatureAvailable("Oodle\0"u8))
            {
                await OodleHelper.InitializeAsync(null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _warnings.Add(new WarningInfo("NATIVE_DEPENDENCY_MISSING", "未发现本地 Oodle Native；不会自动下载或修复", "Oodle"));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _warnings.Add(new WarningInfo("NATIVE_DEPENDENCY_MISSING", "Oodle Native 初始化失败；不会自动下载或修复", ex.GetType().Name));
        }
    }

    private async Task SubmitConfiguredKeysAsync(CancellationToken cancellationToken)
    {
        if (_provider is null) return;
        var keys = new List<KeyValuePair<FGuid, FAesKey>>();
        if (!string.IsNullOrWhiteSpace(_config.AesKey))
            keys.Add(new KeyValuePair<FGuid, FAesKey>(new FGuid(), new FAesKey(_config.AesKey)));
        foreach (var pair in _config.AesKeys)
        {
            if (Guid.TryParse(pair.Key, out var guid))
                keys.Add(new KeyValuePair<FGuid, FAesKey>((FGuid)guid, new FAesKey(pair.Value)));
        }
        if (keys.Count == 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        await _provider.SubmitKeysAsync(keys).ConfigureAwait(false);
    }

    private void OnVfsMountFailed(object? sender, VfsMountFailureDiagnostic diagnostic)
    {
        var code = diagnostic.ExceptionType.Contains("InvalidAes", StringComparison.OrdinalIgnoreCase)
            ? "AES_KEY_REJECTED"
            : diagnostic.ExceptionType.Contains("Oodle", StringComparison.OrdinalIgnoreCase)
                ? "DECOMPRESSION_FAILED"
                : diagnostic.Phase.Contains("precheck", StringComparison.OrdinalIgnoreCase) && diagnostic.Message.Contains("AES", StringComparison.OrdinalIgnoreCase)
                    ? "AES_KEY_REQUIRED"
                    : "MOUNT_FAILED";
        _archiveDiagnostics[Path.GetFullPath(diagnostic.ArchivePath)] = code;
        _warnings.Add(new WarningInfo(code, "容器挂载失败", Path.GetFileName(diagnostic.ArchivePath)));
    }

    private static int UnsupportedContainerCount(PakOnlyFileProvider provider)
        => provider.UnsupportedArchives.Count;

    private string DetermineMountState()
    {
        if (_provider is null) return "failed";
        if (_provider.MountedVfs.Count == 0) return "failed";
        if (_mappingError is not null || _provider.UnloadedVfs.Count > 0 || _catalog?.ConflictedPaths.Count > 0 || _provider.UnsupportedArchives.Count > 0 || _provider.DiscoveryDiagnostics.Count > 0)
            return "partial";
        return "mounted";
    }

    private object StatusEnvelope()
    {
        var provider = _provider;
        var catalog = _catalog;
        var unsupportedCount = provider is null ? 0 : UnsupportedContainerCount(provider);
        var archives = provider is null
            ? new { discovered = 0, mounted = 0, unmounted = 0, unsupported = 0 }
            : new
            {
                discovered = provider.DiscoveredArchives.Count + unsupportedCount,
                mounted = provider.MountedVfs.Count,
                unmounted = Math.Max(0, provider.DiscoveredArchives.Count - provider.MountedVfs.Count),
                unsupported = unsupportedCount
            };
        var keys = provider is null
            ? new { configuredGuids = 0, requiredGuids = 0, rejectedGuids = 0 }
            : new
            {
                configuredGuids = (string.IsNullOrWhiteSpace(_config?.AesKey) ? 0 : 1) + (_config?.AesKeys.Count ?? 0),
                requiredGuids = provider.RequiredKeys.Count,
                rejectedGuids = _warnings.Count(w => w.Code == "AES_KEY_REJECTED")
            };
        var mapping = new
        {
            configured = !string.IsNullOrWhiteSpace(_config?.Usmap),
            loaded = provider?.MappingsContainer is not null,
            path = _config?.Usmap,
            sha256 = HashFile(_config?.Usmap),
            requirement = _mappingRequiredObserved ? "requiredObserved" : "unknown"
        };
        var native = new[]
        {
            new
            {
                name = "CUE4Parse-Natives",
                available = NativeAvailable(),
                path = _config?.NativeLibraries.OodlePath ?? "CUE4Parse-Natives.dll",
                version = (string?)null,
                errorCode = NativeAvailable() ? null : "NATIVE_DEPENDENCY_MISSING"
            }
        };
        var data = new
        {
            hostState = _faulted ? "faulted" : (_initialized ? "active" : "starting"),
            sessionId = string.IsNullOrEmpty(_sessionId) ? null : _sessionId,
            workerPid = Environment.ProcessId,
            reportTimeUtc = DateTimeOffset.UtcNow,
            stale = false,
            mountState = _mountState,
            gameRoot = _config?.GameRoot,
            archiveDir = _config?.ArchiveDir,
            ueVersion = _config?.UeVersion,
            effectiveEngineVersion = _provider?.Versions.Game.ToString(),
            language = _config?.Language,
            sourceFingerprint = _sourceFingerprint,
            catalogRevision = _sourceFingerprint,
            archives,
            files = new
            {
                physicalEntries = catalog?.PhysicalEntryCount ?? 0,
                effectiveFiles = catalog?.Files.Count ?? 0,
                packages = catalog?.Files.Count(f => f.IsPackage && !f.Conflict) ?? 0,
                conflictedPaths = catalog?.ConflictedPaths.Count ?? 0
            },
            keys,
            mappings = mapping,
            native,
            indexes = _indexStore?.GetHeads() ?? new Dictionary<string, object>(),
            activeTaskId = _tasks.ActiveTaskId,
            recentErrors = _warnings.TakeLast(20).ToArray()
        };
        return SuccessEnvelope(data, warnings: _warnings.TakeLast(20).ToArray());
    }

    private object ListArchives(JsonElement? payload)
    {
        var args = Deserialize<ListArgs>(payload);
        var items = new List<object>();
        var seenArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reader in (_provider?.MountedVfs ?? []).Concat(_provider?.UnloadedVfs ?? []).Distinct().OrderBy(reader => reader.Path, StringComparer.Ordinal))
        {
            seenArchives.Add(reader.Path);
            var fileInfo = new FileInfo(reader.Path);
            var mounted = _provider!.MountedVfs.Contains(reader);
            var errorCode = _archiveDiagnostics.TryGetValue(Path.GetFullPath(reader.Path), out var recordedError) ? recordedError : null;
            if (!mounted && reader.IsEncrypted && _provider.RequiredKeys.Contains(reader.EncryptionKeyGuid) && (errorCode is null or "MOUNT_FAILED")) errorCode = "AES_KEY_REQUIRED";
            items.Add(new
            {
                archiveId = ArchiveId(reader),
                relativePath = Path.GetRelativePath(_config.GameRoot, reader.Path).Replace('\\', '/'),
                format = "pak",
                bytes = fileInfo.Exists ? fileInfo.Length : (long?)null,
                encryptionKeyGuid = reader.EncryptionKeyGuid.ToString(),
                indexEncrypted = (bool?)reader.IsEncrypted,
                entryEncryptionState = reader.EncryptedFileCount == 0 ? "none" : reader.EncryptedFileCount >= reader.FileCount ? "all" : "some",
                mounted,
                fileCount = (int?)reader.FileCount,
                readOrder = (long?)reader.ReadOrder,
                mountPoint = reader.MountPoint,
                mountError = errorCode
            });
        }
        foreach (var path in _provider?.UnsupportedArchives ?? [])
        {
            seenArchives.Add(path);
            items.Add(new
            {
                archiveId = ArchiveId(path),
                relativePath = Path.GetRelativePath(_config.GameRoot, path).Replace('\\', '/'),
                format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                bytes = File.Exists(path) ? new FileInfo(path).Length : (long?)null,
                encryptionKeyGuid = (string?)null,
                indexEncrypted = (bool?)null,
                entryEncryptionState = "unknown",
                mounted = false,
                fileCount = (int?)null,
                readOrder = (long?)null,
                mountPoint = (string?)null,
                mountError = "UNSUPPORTED_CONTAINER"
            });
        }
        foreach (var path in _provider?.DiscoveredArchives ?? [])
        {
            if (!seenArchives.Add(path)) continue;
            var errorCode = _archiveDiagnostics.TryGetValue(Path.GetFullPath(path), out var recordedError) ? recordedError : null;
            items.Add(new
            {
                archiveId = ArchiveId(path),
                relativePath = Path.GetRelativePath(_config.GameRoot, path).Replace('\\', '/'),
                format = "pak",
                bytes = File.Exists(path) ? new FileInfo(path).Length : (long?)null,
                encryptionKeyGuid = (string?)null,
                indexEncrypted = (bool?)null,
                entryEncryptionState = "unknown",
                mounted = false,
                fileCount = (int?)null,
                readOrder = (long?)null,
                mountPoint = (string?)null,
                mountError = errorCode ?? "MOUNT_FAILED"
            });
        }
        items = items.OrderBy(item => item.GetType().GetProperty("relativePath")?.GetValue(item)?.ToString(), StringComparer.Ordinal).ToList();
        var page = Page(items, args.Limit, args.Cursor, "list_archives", new { args.Limit }, _sourceFingerprint);
        return SuccessEnvelope(new { items = page.Items }, page.Page);
    }

    private async Task<object> SearchFilesAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        var args = Deserialize<SearchFilesArgs>(payload);
        if (string.IsNullOrWhiteSpace(args.Pattern)) return ErrorEnvelope("INVALID_ARGUMENT", "pattern 不能为空", false, null);
        if (args.Mode is not ("glob" or "regex")) return ErrorEnvelope("INVALID_ARGUMENT", "mode 必须为 glob 或 regex", false, null);
        var scope = NormalizeCatalogScope(args.Scope);
        if (args.AssetType is not null && !_indexStore!.HasPublished("asset", scope))
            return ErrorEnvelope(_indexStore.HasAnyPublished("asset") ? "INDEX_SCOPE_MISMATCH" : "INDEX_NOT_READY", "asset 索引尚未覆盖请求范围", true, "按请求 scope 重建 build_asset_index");

        Regex? regex = null;
        try
        {
            regex = args.Mode.Equals("regex", StringComparison.OrdinalIgnoreCase)
                ? new Regex(args.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(_config.Limits.RegexTimeoutMilliseconds))
                : new Regex(GlobToRegex(args.Pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(_config.Limits.RegexTimeoutMilliseconds));
        }
        catch (ArgumentException ex)
        {
            return ErrorEnvelope("INVALID_ARGUMENT", $"pattern 无效：{ex.Message}", false, null);
        }

        List<object> matches;
        try
        {
            matches = _catalog!.Files.Where(file => InScope(file.DisplayPath, scope) || InProviderScope(file.ProviderPath, scope))
                .Where(file => args.Extension is null || file.Extension.Equals(NormalizeExtension(args.Extension), StringComparison.OrdinalIgnoreCase))
                .Where(file => regex!.IsMatch(file.DisplayPath) || regex.IsMatch(file.ProviderPath))
                .Where(file => args.AssetType is null || file.AssetTypes.Contains(args.AssetType, StringComparer.OrdinalIgnoreCase))
                .OrderBy(file => file.DisplayPath, StringComparer.Ordinal)
                .ThenBy(file => file.ProviderPath, StringComparer.Ordinal)
                .Select(file => (object)new
                {
                    filePath = file.ProviderPath,
                    packagePath = file.IsPackage ? PackageDisplay(file.DisplayPath) : null,
                    extension = file.Extension,
                    size = file.Size,
                    archiveId = file.ArchiveId,
                    readOrder = file.ReadOrder,
                    assetTypes = file.AssetTypes.Count == 0 ? null : file.AssetTypes,
                    typeState = file.Conflict ? "conflict" : file.AssetTypes.Count == 0 ? "unknown" : "known",
                    shadowedEntryCount = file.ShadowedCount
                }).ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            return ErrorEnvelope("REGEX_TIMEOUT", "pattern 匹配超过时间预算", true, "缩短 pattern 或改用 glob");
        }
        var page = Page(matches, args.Limit, args.Cursor, "search_files", args, _sourceFingerprint);
        return SuccessEnvelope(new { items = page.Items }, page.Page, CoverageForCatalog(scope));
    }

    private object ListDirectory(JsonElement? payload)
    {
        var args = Deserialize<DirectoryArgs>(payload);
        var path = NormalizeCatalogScope(string.IsNullOrWhiteSpace(args.Path) ? "/Game" : args.Path);
        if (!_catalog!.Files.Any(file => InScope(file.DisplayPath, path) || string.Equals(file.DisplayPath, path, StringComparison.OrdinalIgnoreCase)))
            return ErrorEnvelope("DIRECTORY_NOT_FOUND", "目录不存在", false, "使用 /Game 或先搜索有效文件");

        var directoryChildren = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, CatalogFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _catalog.Files.Where(file => InScope(file.DisplayPath, path)))
        {
            var rest = file.DisplayPath[path.Length..].TrimStart('/');
            if (rest.Length == 0) continue;
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                files[file.DisplayPath] = file;
            }
            else
            {
                var child = path.TrimEnd('/') + "/" + rest[..slash];
                var next = rest[(slash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
                if (!directoryChildren.TryGetValue(child, out var children)) directoryChildren[child] = children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                children.Add(next);
            }
        }
        var items = directoryChildren.Select(pair => (object)new { kind = "directory", path = pair.Key, directChildCount = pair.Value.Count })
            .Concat(files.Values.Select(file => (object)new { kind = "file", path = file.DisplayPath, extension = file.Extension, size = file.Size, archiveId = file.ArchiveId, conflict = file.Conflict }))
            .OrderBy(item => item.GetType().GetProperty("kind")!.GetValue(item)!.ToString() == "directory" ? 0 : 1)
            .ThenBy(item => item.GetType().GetProperty("path")!.GetValue(item)!.ToString(), StringComparer.Ordinal)
            .ToList();
        var page = Page(items, args.Limit, args.Cursor, "list_directory", args, _sourceFingerprint);
        return SuccessEnvelope(new { items = page.Items }, page.Page, CoverageForCatalog(path));
    }

    private async Task<object> ListObjectsAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        var args = Deserialize<ObjectListArgs>(payload);
        var resolved = ResolvePackage(args.Path);
        if (!resolved.Success) return resolved.Error!;
        var package = await LoadPackageAsync(resolved.File!, cancellationToken).ConfigureAwait(false);
        var items = new List<object>();
        var unresolvedMetadataCount = 0;
        for (var i = 0; i < package.ExportsLazy.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CUE4Parse.UE4.Assets.ResolvedObject? metadata = null;
            string? objectPath = null;
            string? objectName = null;
            string? classPath = null;
            string? className = null;
            string? outerPath = null;
            var metadataResolutionFailed = false;
            try
            {
                metadata = package.ResolvePackageIndex(new FPackageIndex(package, i + 1));
                if (metadata is not null)
                {
                    objectPath = ObjectDisplayPath(resolved.DisplayPackage!, metadata);
                    objectName = metadata.Name.Text;
                    classPath = metadata.Class is { } type ? ObjectDisplayPath(resolved.DisplayPackage!, type) : null;
                    className = metadata.Class?.Name.Text;
                    outerPath = metadata.Outer is { } outer ? ObjectDisplayPath(resolved.DisplayPackage!, outer) : null;
                }
            }
            catch { metadata = null; metadataResolutionFailed = true; /* 元数据损坏时保留 export 行并标记 unresolved。 */ }
            if (metadata is null && (metadataResolutionFailed || objectName is null)) unresolvedMetadataCount++;
            if (args.Type is not null && !string.Equals(args.Type, className, StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(new
            {
                exportIndex = i,
                objectPath,
                objectName,
                classPath,
                className,
                outerPath,
                objectFlags = GetExportFlags(package, i),
                serialSize = GetSerialSize(package, i),
                metadataState = metadata is null ? "unresolved" : "resolved"
            });
        }
        var page = Page(items, args.Limit, args.Cursor, "list_objects", args, _sourceFingerprint);
        return SuccessEnvelope(new { packagePath = resolved.DisplayPackage, items = page.Items }, page.Page,
            new CoverageInfo { Scope = resolved.DisplayPackage, TotalUnits = package.ExportsLazy.Length, SucceededUnits = package.ExportsLazy.Length - unresolvedMetadataCount, FailedUnits = unresolvedMetadataCount, SkippedUnits = 0, Complete = unresolvedMetadataCount == 0, CatalogComplete = _catalog!.CatalogComplete, SourceFingerprint = _sourceFingerprint });
    }

    private async Task<object> GetObjectAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        var args = Deserialize<ObjectArgs>(payload);
        var resolved = ResolveObject(args.Path, args.ExportIndex, cancellationToken);
        if (!resolved.Success) return resolved.Error!;
        var obj = await LoadObjectAsync(resolved.Package!, resolved.ExportIndex, cancellationToken).ConfigureAwait(false);
        var json = JsonConvert.SerializeObject(obj, Formatting.None);
        var token = JToken.Parse(json);
        var projection = Project(token, args.Fields, args.Depth);
        if (!projection.Success) return ErrorEnvelope(projection.Code!, projection.Message!, false, projection.SuggestedAction);
        if (Encoding.UTF8.GetByteCount(projection.Token!.ToString(Formatting.None)) > _config.Limits.MaxProjectionBytes)
            return ErrorEnvelope("OUTPUT_BUDGET_EXCEEDED", "对象投影超过 maxProjectionBytes", false, "缩小 fields/depth 或使用 export_json");
        var value = ToJsonElement(projection.Token);
        return SuccessEnvelope(new
        {
            @object = new { objectPath = resolved.ObjectPath, exportIndex = resolved.ExportIndex, classPath = obj.Class is { } type ? ObjectDisplayPath(resolved.DisplayPackage!, type) : null, sourceFilePath = resolved.File!.ProviderPath, archiveId = resolved.File.ArchiveId },
            value,
            projection = new { depth = args.Depth, fields = args.Fields, omissions = projection.Omissions },
            parseStatus = "parsed"
        }, coverage: CoverageForCatalog(resolved.DisplayPackage));
    }

    private async Task<object> GetDataTableAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        var args = Deserialize<DataTableArgs>(payload);
        var mode = args.Mode ?? "rows";
        if (mode is not ("rows" or "rowNames" or "schema")) return ErrorEnvelope("INVALID_ARGUMENT", "mode 必须为 rows、rowNames 或 schema", false, null);
        if (args.Fields is { Length: 0 }) return ErrorEnvelope("INVALID_ARGUMENT", "fields 不能为空数组", false, "省略 fields 或提供字段");
        if (mode == "schema" && (args.RowName is not null || args.Where is { Length: > 0 } || args.Fields is { Length: > 0 }))
            return ErrorEnvelope("INVALID_ARGUMENT", "schema 模式不能使用 rowName、where 或 fields", false, null);
        if (args.Where is { } filters && filters.Any(filter => string.IsNullOrWhiteSpace(filter.Field) || filter.Op is not ("eq" or "ne" or "contains" or "gt" or "gte" or "lt" or "lte" or "exists")))
            return ErrorEnvelope("INVALID_ARGUMENT", "where 的 field 或 op 无效", false, null);
        var resolved = ResolveObject(args.Path, args.ExportIndex, cancellationToken);
        if (!resolved.Success) return resolved.Error!;
        var obj = await LoadObjectAsync(resolved.Package!, resolved.ExportIndex, cancellationToken).ConfigureAwait(false);
        if (obj is not UDataTable table)
            return ErrorEnvelope("TYPE_MISMATCH", "目标对象不是 UDataTable", false, "先使用 list_objects 检查类型");
        if (table.RowMap is null)
            return ErrorEnvelope("PACKAGE_PARSE_FAILED", "DataTable RowMap 未初始化", false, "检查 usmap 和包解析诊断");
        var rows = new List<(string Name, JToken Value)>();
        foreach (var pair in table.RowMap)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add((pair.Key.Text, JToken.Parse(JsonConvert.SerializeObject(pair.Value, Formatting.None))));
        }
        var allRows = rows;
        if (args.Where is { Length: > 0 } && HasUnknownFilterField(allRows, args.Where))
            return ErrorEnvelope("FIELD_NOT_FOUND", "where 使用了不存在的字段", false, "检查 JSON Pointer");
        if (args.Fields is { Length: > 0 } && HasUnknownField(allRows, args.Fields))
            return ErrorEnvelope("FIELD_NOT_FOUND", "fields 使用了不存在的字段", false, "检查 JSON Pointer");
        if (args.RowName is not null)
        {
            var matchingRows = rows.Where(item => string.Equals(item.Name, args.RowName, StringComparison.Ordinal)).ToArray();
            if (matchingRows.Length == 0) return ErrorEnvelope("ROW_NOT_FOUND", "行不存在", false, "使用 rowNames 模式检查精确大小写");
            rows = [matchingRows[0]];
        }
        var filtered = new List<(string Name, JToken Value)>();
        foreach (var row in rows)
        {
            if (MatchesFilters(row.Value, args.Where, out var filterError))
                filtered.Add(row);
            else if (filterError is not null)
                return ErrorEnvelope("INVALID_ARGUMENT", filterError, false, "检查 where 的 value 类型");
        }
        if (mode == "schema")
            return SuccessEnvelope(new { rowStruct = table.RowStructName, items = InferSchema(rows) }, coverage: CoverageForCatalog(resolved.DisplayPackage));

        var resultItems = mode == "rowNames"
            ? filtered.Select(row => (object)new { rowName = row.Name }).ToList()
            : filtered.Select(row => (object)new { rowName = row.Name, value = ApplyFields(row.Value, args.Fields) }).ToList();
        var page = Page(resultItems, args.Limit, args.Cursor, "get_datatable", args, _sourceFingerprint);
        return SuccessEnvelope(new { rowStruct = table.RowStructName, items = page.Items, projection = mode == "rows" ? new { fields = args.Fields } : null }, page.Page, CoverageForCatalog(resolved.DisplayPackage));
    }

    private async Task<object> GetClassInfoAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        var args = Deserialize<ClassInfoArgs>(payload);
        var resolved = ResolveObject(args.Path, args.ExportIndex, cancellationToken);
        if (!resolved.Success) return resolved.Error!;
        var obj = await LoadObjectAsync(resolved.Package!, resolved.ExportIndex, cancellationToken).ConfigureAwait(false);
        if (obj is not UClass cls)
            return ErrorEnvelope("TYPE_MISMATCH", "目标对象不是 UClass", false, "使用 list_objects 检查类型");
        var section = args.Section ?? "summary";
        if (section is "parents" or "properties" or "functions")
        {
            var items = section switch
            {
                "parents" => GetParents(cls, resolved.DisplayPackage!),
                "properties" => GetProperties(cls, resolved.ObjectPath!, resolved.DisplayPackage!, args.IncludeInherited),
                _ => GetFunctions(cls, resolved.ObjectPath!, resolved.DisplayPackage!, args.IncludeInherited)
            };
            var page = Page(items, args.Limit, args.Cursor, "get_class_info", args, _sourceFingerprint);
            return SuccessEnvelope(new { classPath = resolved.ObjectPath, items = page.Items }, page.Page, CoverageForCatalog(resolved.DisplayPackage));
        }
        if (section != "summary") return ErrorEnvelope("INVALID_ARGUMENT", "section 无效", false, null);
        return SuccessEnvelope(new
        {
            classPath = resolved.ObjectPath,
            superPath = cls.SuperStruct?.ResolvedObject is { } super ? ObjectDisplayPath(resolved.DisplayPackage!, super) : null,
            classFlags = cls.ClassFlags.ToString(),
            interfaces = cls.Interfaces?.Select(item => item.Class?.ResolvedObject is { } type ? ObjectDisplayPath(resolved.DisplayPackage!, type) : null).Where(item => item is not null).ToArray(),
            cdoPath = cls.ClassDefaultObject?.ResolvedObject is { } cdo ? ObjectDisplayPath(resolved.DisplayPackage!, cdo) : null,
            ownPropertyCount = cls.ChildProperties?.Length ?? 0,
            ownFunctionCount = cls.FuncMap?.Count ?? 0,
            availability = "available"
        }, coverage: CoverageForCatalog(resolved.DisplayPackage));
    }

    private async Task<object> GetFunctionAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        var args = Deserialize<FunctionArgs>(payload);
        var resolved = ResolveObject(args.Path, args.ExportIndex, cancellationToken);
        if (!resolved.Success) return resolved.Error!;
        var obj = await LoadObjectAsync(resolved.Package!, resolved.ExportIndex, cancellationToken).ConfigureAwait(false);
        if (obj is not UFunction function)
            return ErrorEnvelope("TYPE_MISMATCH", "目标对象不是 UFunction", false, "使用 list_objects 检查类型");
        var status = function.ScriptReadState;
        var section = args.Section ?? "summary";
        if (section == "parameters")
        {
            var items = GetParameters(function);
            var page = Page(items, args.Limit, args.Cursor, "get_function", args, _sourceFingerprint);
            return SuccessEnvelope(new { functionPath = resolved.ObjectPath, items = page.Items }, page.Page, CoverageForCatalog(resolved.DisplayPackage));
        }
        if (section == "bytecode")
        {
            var items = function.ScriptBytecode?.Select((expression, index) => (object)new { expressionOrdinal = index, statementIndex = expression.StatementIndex, token = expression.Token.ToString(), expression = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(expression, Formatting.None)) }).ToList() ?? [];
            var page = Page(items, args.Limit, args.Cursor, "get_function", args, _sourceFingerprint);
            var data = new { functionPath = resolved.ObjectPath, items = page.Items, scriptStatus = status, parseDiagnostic = function.ScriptFailure };
            if (status is "partial" or "failed")
                return ErrorEnvelope("SCRIPT_PARSE_FAILED", "Kismet 字节码只能提供带完整性声明的前缀", false, "使用已返回的前缀并保留 scriptStatus", data: data, page: page.Page, coverage: CoverageForCatalog(resolved.DisplayPackage));
            return SuccessEnvelope(data, page.Page, CoverageForCatalog(resolved.DisplayPackage));
        }
        if (section != "summary") return ErrorEnvelope("INVALID_ARGUMENT", "section 无效", false, null);
        return SuccessEnvelope(new
        {
            functionPath = resolved.ObjectPath,
            ownerClassPath = function.Outer is { } owner ? ObjectDisplayPath(resolved.DisplayPackage!, owner) : null,
            functionFlags = function.FunctionFlags.ToString(),
            eventGraphFunction = function.EventGraphFunction?.ResolvedObject is { } graph ? ObjectDisplayPath(resolved.DisplayPackage!, graph) : null,
            eventGraphCallOffset = function.EventGraphCallOffset,
            scriptStatus = status,
            implementation = function.FunctionFlags.HasFlag(EFunctionFlags.FUNC_Native) && (status is "notRead" or "empty") ? "native" : null,
            serializedScriptBytes = function.SerializedScriptSize,
            parsedScriptBytes = function.ParsedScriptSize,
            expressionCount = function.ScriptBytecode?.Length ?? 0,
            availability = "available"
        }, coverage: CoverageForCatalog(resolved.DisplayPackage));
    }

    private async Task<object> GetStringTableAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        var args = Deserialize<StringTableArgs>(payload);
        var resolved = ResolveObject(args.Path, args.ExportIndex, cancellationToken);
        if (resolved.Success)
        {
            var obj = await LoadObjectAsync(resolved.Package!, resolved.ExportIndex, cancellationToken).ConfigureAwait(false);
            if (obj is not UStringTable table)
                return ErrorEnvelope("TYPE_MISMATCH", "目标对象不是 StringTable", false, "locres 应直接传文件路径");
            var items = table.StringTable.KeysToEntries
                .Where(pair => args.Key is null || string.Equals(pair.Key, args.Key, StringComparison.Ordinal))
                .Select(pair =>
                {
                    string? localized = null;
                    var hasLocalized = _provider!.Internationalization.TryGetValue(table.StringTable.TableNamespace, out var entries) && entries.TryGetValue(pair.Key, out localized);
                    return new { @namespace = table.StringTable.TableNamespace, key = pair.Key, sourceText = pair.Value, localizedText = hasLocalized ? localized : null, resolvedText = hasLocalized ? localized! : pair.Value, resolution = hasLocalized ? "resolvedLocalized" : "sourceOnly", metadata = table.StringTable.KeysToMetaData?.GetValueOrDefault(pair.Key) };
                })
                .Where(item => args.Namespace is null || string.Equals(item.@namespace, args.Namespace, StringComparison.Ordinal))
                .OrderBy(item => item.@namespace, StringComparer.Ordinal)
                .ThenBy(item => item.key, StringComparer.Ordinal)
                .ToList();
            if (args.Key is not null && items.Count == 0) return ErrorEnvelope("KEY_NOT_FOUND", "StringTable key 不存在", false, null);
            var page = Page(items, args.Limit, args.Cursor, "get_string_table", args, _sourceFingerprint);
            return SuccessEnvelope(new { sourceKind = "stringTable", language = _config.Language, items = page.Items }, page.Page, CoverageForCatalog(resolved.DisplayPackage));
        }

        var file = _catalog!.FindFile(args.Path);
        if (file is null || !file.DisplayPath.EndsWith(".locres", StringComparison.OrdinalIgnoreCase))
            return resolved.Error!;
        using var reader = file.GameFile!.CreateReader();
        var resource = new FTextLocalizationResource(reader);
        var locItems = resource.Entries.SelectMany(ns => ns.Value.Select(pair => new { @namespace = ns.Key.Str, key = pair.Key.Str, sourceText = (string?)null, localizedText = pair.Value.LocalizedString, resolvedText = pair.Value.LocalizedString, resolution = "resolvedLocalized", metadata = (object?)new { pair.Value.LocResName, pair.Value.SourceStringHash } }))
            .Where(item => (args.Key is null || string.Equals(item.key, args.Key, StringComparison.Ordinal)) && (args.Namespace is null || string.Equals(item.@namespace, args.Namespace, StringComparison.Ordinal)))
            .OrderBy(item => item.@namespace, StringComparer.Ordinal)
            .ThenBy(item => item.key, StringComparer.Ordinal)
            .ToList();
        if (args.Key is not null && locItems.Count == 0) return ErrorEnvelope("KEY_NOT_FOUND", "locres key 不存在", false, null);
        var locPage = Page(locItems, args.Limit, args.Cursor, "get_string_table", args, _sourceFingerprint);
        return SuccessEnvelope(new { sourceKind = "locres", language = _config.Language, items = locPage.Items }, locPage.Page, CoverageForCatalog(file.DisplayPath));
    }

    private async Task<object> GetCurveAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        var args = Deserialize<CurveArgs>(payload);
        var resolved = ResolveObject(args.Path, args.ExportIndex, cancellationToken);
        if (!resolved.Success) return resolved.Error!;
        var obj = await LoadObjectAsync(resolved.Package!, resolved.ExportIndex, cancellationToken).ConfigureAwait(false);
        if (obj is not UCurveTable && obj is not UCurveFloat)
            return ErrorEnvelope("TYPE_MISMATCH", "目标对象不是 v1 支持的 UCurveTable 或 UCurveFloat", false, "使用 list_objects 检查类型");
        var json = JObject.Parse(JsonConvert.SerializeObject(obj, Formatting.None));
        var properties = json["Properties"] as JObject ?? json;
        var curves = obj is UCurveTable
            ? properties["Rows"] as JObject ?? new JObject()
            : properties["FloatCurve"] is not null ? new JObject { ["FloatCurve"] = properties["FloatCurve"] } : new JObject();
        var names = curves.Properties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return ErrorEnvelope("TYPE_MISMATCH", "目标对象不是可读取的曲线或曲线表", false, "使用 list_objects 检查类型");
        var mode = args.Mode ?? "keys";
        if (mode is not ("names" or "keys")) return ErrorEnvelope("INVALID_ARGUMENT", "mode 必须为 names 或 keys", false, null);
        if (mode == "names")
        {
            var nameItems = names.Select(name => (object)new { curveName = name }).ToList();
            var namePage = Page(nameItems, args.Limit, args.Cursor, "get_curve", args, _sourceFingerprint);
            return SuccessEnvelope(new { curveKind = obj.GetType().Name, items = namePage.Items }, namePage.Page, CoverageForCatalog(resolved.DisplayPackage));
        }
        var name = args.CurveName ?? (names.Length == 1 ? names[0] : null);
        if (name is null) return ErrorEnvelope("AMBIGUOUS_OBJECT", "多个曲线需要指定 curveName", false, "先使用 names 模式");
        var curve = curves[name];
        if (curve is null) return ErrorEnvelope("KEY_NOT_FOUND", "曲线不存在", false, "先使用 names 模式");
        var curveItems = ExtractCurveItems(curve);
        var curvePage = Page(curveItems, args.Limit, args.Cursor, "get_curve", args, _sourceFingerprint);
        return SuccessEnvelope(new
        {
            curveName = name,
            curveKind = obj.GetType().Name,
            defaultValue = curve["DefaultValue"],
            preInfinityExtrap = curve["PreInfinityExtrap"],
            postInfinityExtrap = curve["PostInfinityExtrap"],
            items = curvePage.Items
        }, curvePage.Page, CoverageForCatalog(resolved.DisplayPackage));
    }

    private static IReadOnlyList<object> ExtractCurveItems(JToken curve)
    {
        var keys = curve["Keys"] as JArray ?? (curve is JArray array ? array : []);
        return keys.Select((key, index) => new
            {
                keyIndex = key["KeyIndex"]?.Value<int?>() ?? index,
                time = key["Time"]?.Value<double?>(),
                value = key["Value"],
                interpMode = key["InterpMode"],
                tangentMode = key["TangentMode"],
                tangentWeightMode = key["TangentWeightMode"],
                arriveTangent = key["ArriveTangent"],
                leaveTangent = key["LeaveTangent"],
                arriveTangentWeight = key["ArriveTangentWeight"],
                leaveTangentWeight = key["LeaveTangentWeight"]
            })
            .OrderBy(item => item.time)
            .ThenBy(item => item.keyIndex)
            .Cast<object>()
            .ToArray();
    }

    private object StartIndexTask(string kind, JsonElement? payload, CancellationToken cancellationToken)
    {
        string scope;
        var includeSymbols = false;
        if (kind == "asset")
        {
            var args = Deserialize<AssetIndexArgs>(payload);
            scope = NormalizeCatalogScope(args.Scope);
            includeSymbols = args.IncludeSymbols;
        }
        else
        {
            var args = Deserialize<ScopeArgs>(payload);
            scope = NormalizeCatalogScope(args.Scope);
        }
        try
        {
            if (!_tasks.TryStart(kind, _sessionId, scope, _sourceFingerprint, out var task))
                return ErrorEnvelope("TASK_BUSY", "已有后台任务正在运行", true, "先使用 get_task/cancel_task");
            _ = Task.Run(() => BuildIndexAsync(task, kind, scope, includeSymbols, task.Cancellation.Token), CancellationToken.None);
            return SuccessEnvelope(new TaskReceipt { TaskId = task.TaskId, Kind = kind, State = task.State, SessionId = _sessionId, Scope = scope });
        }
        catch (IOException)
        {
            return ErrorEnvelope("CACHE_IO_FAILED", "任务状态无法写入索引缓存", true, "检查 cacheRoot 权限和磁盘空间");
        }
        catch (UnauthorizedAccessException)
        {
            return ErrorEnvelope("CACHE_IO_FAILED", "任务状态无法写入索引缓存", true, "检查 cacheRoot 权限和磁盘空间");
        }
        catch (SqliteException)
        {
            return ErrorEnvelope("CACHE_IO_FAILED", "任务状态无法写入索引缓存", true, "检查 cacheRoot 权限和磁盘空间");
        }
    }

    private object StartExportTask(string kind, JsonElement? payload, CancellationToken cancellationToken)
    {
        var args = Deserialize<ExportArgs>(payload);
        if (string.IsNullOrWhiteSpace(args.Path) || string.IsNullOrWhiteSpace(args.OutDir))
            return ErrorEnvelope("INVALID_ARGUMENT", "path 和 outDir 必填", false, null);
        try
        {
            if (!_tasks.TryStart(kind, _sessionId, args.Path, _sourceFingerprint, out var task))
                return ErrorEnvelope("TASK_BUSY", "已有后台任务正在运行", true, "先使用 get_task/cancel_task");
            _ = Task.Run(() => ExportAsync(task, args, kind, task.Cancellation.Token), CancellationToken.None);
            return SuccessEnvelope(new TaskReceipt { TaskId = task.TaskId, Kind = kind, State = task.State, SessionId = _sessionId, Scope = args.Path });
        }
        catch (IOException)
        {
            return ErrorEnvelope("CACHE_IO_FAILED", "任务状态无法写入索引缓存", true, "检查 cacheRoot 权限和磁盘空间");
        }
        catch (UnauthorizedAccessException)
        {
            return ErrorEnvelope("CACHE_IO_FAILED", "任务状态无法写入索引缓存", true, "检查 cacheRoot 权限和磁盘空间");
        }
        catch (SqliteException)
        {
            return ErrorEnvelope("CACHE_IO_FAILED", "任务状态无法写入索引缓存", true, "检查 cacheRoot 权限和磁盘空间");
        }
    }

    private object ListAssetTypes(JsonElement? payload)
    {
        var args = Deserialize<ListArgs>(payload);
        var scope = NormalizeCatalogScope(args.Dir);
        if (!_indexStore!.HasPublished("asset", scope)) return ErrorEnvelope(_indexStore.HasAnyPublished("asset") ? "INDEX_SCOPE_MISMATCH" : "INDEX_NOT_READY", "asset 索引尚未覆盖请求范围", true, "按请求 dir 重建 build_asset_index");
        var items = _indexStore.ListAssetTypes(scope);
        var page = Page(items, args.Limit, args.Cursor, "list_asset_types", args, _indexStore.GetCoverage("asset", scope)?.SnapshotId);
        return SuccessEnvelope(new { countUnit = "export", items = page.Items, unknownExportCount = _indexStore.GetUnknownAssetExportCount(scope) }, page.Page, _indexStore.GetCoverage("asset", scope));
    }

    private object SearchSymbols(JsonElement? payload)
    {
        var args = Deserialize<SearchSymbolsArgs>(payload);
        if (string.IsNullOrWhiteSpace(args.Pattern)) return ErrorEnvelope("INVALID_ARGUMENT", "pattern 不能为空", false, null);
        if (args.Kind is not ("any" or "class" or "function" or "property")) return ErrorEnvelope("INVALID_ARGUMENT", "kind 无效", false, null);
        var scope = NormalizeCatalogScope(args.Scope);
        if (!_indexStore!.HasSymbolCoverage(scope)) return ErrorEnvelope(_indexStore.HasAnyPublished("asset") ? "INDEX_SCOPE_MISMATCH" : "INDEX_NOT_READY", "asset symbols 索引尚未覆盖请求范围", true, "按请求 scope 重建 build_asset_index(includeSymbols=true)");
        Regex regex;
        try
        {
            regex = new Regex(GlobToRegex(args.Pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(_config.Limits.RegexTimeoutMilliseconds));
        }
        catch (ArgumentException ex)
        {
            return ErrorEnvelope("INVALID_ARGUMENT", $"pattern 无效：{ex.Message}", false, null);
        }
        try
        {
            var items = _indexStore.SearchSymbols(scope, regex, args.Kind ?? "any");
            var page = Page(items, args.Limit, args.Cursor, "search_symbols", args, _indexStore.GetCoverage("asset", scope)?.SnapshotId);
            return SuccessEnvelope(new { items = page.Items }, page.Page, _indexStore.GetCoverage("asset", scope));
        }
        catch (RegexMatchTimeoutException)
        {
            return ErrorEnvelope("REGEX_TIMEOUT", "pattern 匹配超过时间预算", true, "缩短 pattern 或改用更具体的 glob");
        }
    }

    private object SearchText(JsonElement? payload)
    {
        var args = Deserialize<SearchTextArgs>(payload);
        if (string.IsNullOrWhiteSpace(args.Keyword)) return ErrorEnvelope("INVALID_ARGUMENT", "keyword 不能为空", false, null);
        if (args.SourceKind is not null && args.SourceKind is not ("datatable" or "stringTable" or "locres")) return ErrorEnvelope("INVALID_ARGUMENT", "sourceKind 无效", false, "使用 datatable、stringTable 或 locres");
        var scope = NormalizeCatalogScope(args.Scope);
        if (!_indexStore!.HasPublished("text", scope)) return ErrorEnvelope(_indexStore.HasAnyPublished("text") ? "INDEX_SCOPE_MISMATCH" : "INDEX_NOT_READY", "text 索引尚未覆盖请求范围", true, "按请求 scope 重建 build_text_index");
        var items = _indexStore.SearchText(args.Keyword, scope, args.SourceKind);
        var page = Page(items, args.Limit, args.Cursor, "search_text", args, _indexStore.GetCoverage("text", scope)?.SnapshotId);
        return SuccessEnvelope(new { items = page.Items }, page.Page, _indexStore.GetCoverage("text", scope));
    }

    private object FindReferences(JsonElement? payload)
    {
        var args = Deserialize<ReferenceArgs>(payload);
        var direction = args.Direction ?? "outgoing";
        if (direction is not ("incoming" or "outgoing")) return ErrorEnvelope("INVALID_ARGUMENT", "direction 必须为 incoming 或 outgoing", false, null);
        var resolved = ResolvePackage(args.Path);
        if (!resolved.Success && LooksLikeObjectSelector(args.Path))
        {
            var objectResolved = ResolveObject(args.Path, null);
            if (!objectResolved.Success) return objectResolved.Error!;
            resolved = ResolvedPackage.Succeeded(objectResolved.File!, objectResolved.DisplayPackage!, objectResolved.File!.PackageKey);
        }
        if (!resolved.Success) return resolved.Error!;
        if (!_indexStore!.HasAnyPublished("reference")) return ErrorEnvelope("INDEX_NOT_READY", "reference 索引尚未发布", true, "调用 build_reference_index");
        if (direction == "outgoing" && !_indexStore.HasPublished("reference", resolved.DisplayPackage!)) return ErrorEnvelope("INDEX_SCOPE_MISMATCH", "outgoing 查询超出 reference 快照范围", true, "按更大 scope 重建 reference 索引");
        var items = _indexStore.FindReferences(resolved.PackageKey!, direction);
        var coverage = _indexStore.GetHeadCoverage("reference");
        var page = Page(items, args.Limit, args.Cursor, "find_references", args, coverage?.SnapshotId);
        return SuccessEnvelope(new { requestedPath = args.Path, queryPackagePath = resolved.DisplayPackage, granularity = "package", items = page.Items, completeFor = "indexedScope" }, page.Page, coverage);
    }

    private object GetTask(JsonElement? payload)
    {
        var args = Deserialize<TaskArgs>(payload);
        if (!args.IncludeErrors && (!string.IsNullOrWhiteSpace(args.Cursor) || args.Limit is not null))
            return ErrorEnvelope("INVALID_ARGUMENT", "limit/cursor 只用于 includeErrors=true 的错误分页", false, "设置 includeErrors=true");
        if (!_tasks.TryGet(args.TaskId, out var task)) return ErrorEnvelope("TASK_NOT_FOUND", "任务不存在", false, null);
        PageInfo? errorPage = null;
        IReadOnlyList<object>? errors = null;
        TaskView view;
        TaskErrorInfo[] errorSnapshot;
        lock (task)
        {
            view = new TaskView(task.TaskId, task.Kind, task.State, task.SessionId, task.Stale, task.Scope, task.StartedAtUtc, task.FinishedAtUtc, task.Phase, task.CurrentSubject, task.TotalUnits, task.ProcessedUnits, task.SucceededUnits, task.FailedUnits, task.SkippedUnits, task.CancelRequested, task.CanInterruptCurrentUnit, task.SnapshotId, task.Result, task.ErrorsVersion);
            errorSnapshot = task.Errors.ToArray();
        }
        if (args.IncludeErrors)
        {
            var pagedErrors = Page(errorSnapshot.Cast<object>().ToArray(), args.Limit, args.Cursor, "get_task", args, $"{view.TaskId}:errors:{view.ErrorsVersion}");
            errors = pagedErrors.Items;
            errorPage = pagedErrors.Page;
        }
        return SuccessEnvelope(new
        {
            taskId = view.TaskId,
            kind = view.Kind,
            state = view.State,
            sessionId = view.SessionId,
            stale = view.Stale,
            scope = view.Scope,
            startedAtUtc = view.StartedAtUtc,
            finishedAtUtc = view.FinishedAtUtc,
            phase = view.Phase,
            currentSubject = view.CurrentSubject,
            totalUnits = view.TotalUnits,
            processedUnits = view.ProcessedUnits,
            succeededUnits = view.SucceededUnits,
            failedUnits = view.FailedUnits,
            skippedUnits = view.SkippedUnits,
            cancelRequested = view.CancelRequested,
            canInterruptCurrentUnit = view.CanInterruptCurrentUnit,
            result = view.Result,
            errors
        }, errorPage, view.SnapshotId is null ? null : _indexStore?.GetCoverage(view.Kind, view.Scope ?? "/Game"));
    }

    private object CancelTask(JsonElement? payload)
    {
        var args = Deserialize<TaskArgs>(payload);
        if (!_tasks.TryCancel(args.TaskId, out var previous, out var current, out var canInterrupt)) return ErrorEnvelope("TASK_NOT_FOUND", "任务不存在", false, null);
        return SuccessEnvelope(new { taskId = args.TaskId, previousState = previous, state = current, cancelRequested = current is "cancelling" or "cancelled", canInterruptCurrentUnit = canInterrupt });
    }

    private async Task BuildIndexAsync(TaskInfo task, string kind, string scope, bool includeSymbols, CancellationToken cancellationToken)
    {
        if (task.CancelRequested)
        {
            task.State = "cancelled";
            task.FinishedAtUtc = DateTimeOffset.UtcNow;
            _tasks.Save(task);
            return;
        }
        task.State = "running";
        task.Phase = "enumerate";
        _tasks.Save(task);
        var files = _catalog!.Files.Where(file => (kind == "text" ? file.IsPackage || file.Extension.Equals("locres", StringComparison.OrdinalIgnoreCase) : file.IsPackage) && !file.Conflict && InScope(file.DisplayPath, scope)).ToList();
        task.TotalUnits = files.Count;
        var snapshotId = Guid.NewGuid().ToString("N");
        task.SnapshotId = snapshotId;
        var published = false;
        var symbolsComplete = true;
        var metadataComplete = true;
        try
        {
            _indexStore!.BeginSnapshot(kind, snapshotId, scope, _sourceFingerprint ?? string.Empty, kind == "text" ? _config.Language : null, kind == "asset" && includeSymbols);
            foreach (var file in files)
            {
                await WaitForForegroundTurnAsync(task, cancellationToken).ConfigureAwait(false);
                if (task.CancelRequested) { task.State = "cancelled"; break; }
                task.CurrentSubject = file.DisplayPath;
                task.CanInterruptCurrentUnit = false;
                var scanPhase = kind switch { "asset" => "metadata", "text" => "text", "reference" => "references", _ => kind };
                var unitCountersPrepared = false;
                var unitProgressCommitted = false;
                var unitFailurePersisted = false;
                try
                {
                    await _parseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        task.SucceededUnits++;
                        task.ProcessedUnits++;
                        unitCountersPrepared = true;
                        switch (kind)
                        {
                            case "asset":
                                var fileSymbolsComplete = await _indexStore!.IndexAssetAsync(snapshotId, file, _provider!, includeSymbols, cancellationToken, task).ConfigureAwait(false);
                                symbolsComplete &= fileSymbolsComplete;
                                metadataComplete &= _indexStore.LastAssetMetadataComplete;
                                break;
                            case "text":
                                await _indexStore!.IndexTextAsync(snapshotId, file, _provider!, _config.Language, cancellationToken, task).ConfigureAwait(false);
                                break;
                            case "reference":
                                await _indexStore!.IndexReferenceAsync(snapshotId, file, _provider!, cancellationToken, task).ConfigureAwait(false);
                                break;
                        }
                        unitProgressCommitted = true;
                    }
                    finally { _parseGate.Release(); }
                }
                catch (OperationCanceledException) when (task.CancelRequested || cancellationToken.IsCancellationRequested)
                {
                    if (unitCountersPrepared && !unitProgressCommitted)
                    {
                        task.SucceededUnits--;
                        task.ProcessedUnits--;
                    }
                    task.State = "cancelled";
                    break;
                }
                catch (Exception ex)
                {
                    if (ex is CUE4Parse.UE4.Exceptions.MappingException) _mappingRequiredObserved = true;
                    if (unitCountersPrepared && !unitProgressCommitted) task.SucceededUnits--;
                    if (unitCountersPrepared && !unitProgressCommitted) task.ProcessedUnits--;
                    var code = MapExceptionCode(ex);
                    task.FailedUnits++;
                    task.ProcessedUnits++;
                    task.CanInterruptCurrentUnit = true;
                    _tasks.AddErrorWithoutPersist(task, file.DisplayPath, kind, code, "单元解析失败");
                    _indexStore!.RecordScanUnitAndSaveTask(task, snapshotId, file.DisplayPath, scanPhase, "failed", code, "单元解析失败");
                    unitFailurePersisted = true;
                }
                finally
                {
                    task.CanInterruptCurrentUnit = true;
                    if (!unitFailurePersisted) _tasks.Save(task);
                }
            }
            if (task.State == "cancelled" || task.CancelRequested || cancellationToken.IsCancellationRequested)
            {
                task.State = "cancelled";
                task.Result = null;
                _indexStore!.DiscardSnapshot(snapshotId);
            }
            else if (SourceChanged())
            {
                _indexStore!.DiscardSnapshot(snapshotId);
                task.State = "failed";
                _tasks.AddError(task, scope, kind, "SOURCE_CHANGED", "扫描期间源容器发生变化");
            }
            else
            {
                task.State = task.FailedUnits > 0 ? "completedWithErrors" : "completed";
                task.Result = kind == "reference"
                    ? new { snapshotId, scope, sourceFingerprint = _sourceFingerprint, granularity = "package", edgeKind = "packageImport" }
                    : new { snapshotId, scope, sourceFingerprint = _sourceFingerprint };
                task.FinishedAtUtc = DateTimeOffset.UtcNow;
                task.CurrentSubject = null;
                task.Phase = null;
                task.CanInterruptCurrentUnit = true;
                _indexStore!.Publish(kind, snapshotId, scope, _sourceFingerprint ?? string.Empty, task.FailedUnits == 0 && metadataComplete, task.SucceededUnits, task.FailedUnits, task.TotalUnits ?? 0, _catalog!.CatalogComplete, kind == "asset" && includeSymbols && symbolsComplete, kind == "text" ? _config.Language : null, kind == "asset" && includeSymbols, task);
                if (kind == "asset")
                    foreach (var catalogFile in _catalog.Files)
                        catalogFile.AssetTypes = _indexStore.GetAssetTypes(catalogFile.PackageKey);
                published = true;
                try { _indexStore.CleanupOldSnapshots(kind, snapshotId); }
                catch (Exception cleanup)
                {
                    _tasks.AddError(task, scope, "cleanup", "CACHE_IO_FAILED", "旧索引快照清理失败：" + cleanup.GetType().Name);
                    task.State = "completedWithErrors";
                }
                _tasks.Save(task);
            }
        }
        catch (OperationCanceledException) when (task.CancelRequested || cancellationToken.IsCancellationRequested)
        {
            if (!published) _indexStore!.DiscardSnapshot(snapshotId);
            task.State = "cancelled";
        }
        catch (Exception ex)
        {
            if (ex is CUE4Parse.UE4.Exceptions.MappingException) _mappingRequiredObserved = true;
            if (!published) _indexStore!.DiscardSnapshot(snapshotId);
            task.State = "failed";
            _tasks.AddError(task, scope, kind, MapExceptionCode(ex), "索引任务失败");
        }
        finally
        {
            task.FinishedAtUtc = DateTimeOffset.UtcNow;
            task.CurrentSubject = null;
            task.Phase = null;
            task.CanInterruptCurrentUnit = true;
            _tasks.Save(task);
        }
    }

    private async Task ExportAsync(TaskInfo task, ExportArgs args, string kind, CancellationToken cancellationToken)
    {
        task.State = "running";
        task.Phase = "resolve";
        _tasks.Save(task);
        if (task.CancelRequested)
        {
            task.State = "cancelled";
            task.FinishedAtUtc = DateTimeOffset.UtcNow;
            _tasks.Save(task);
            return;
        }
        ResolvedPackage resolved;
        try { resolved = ResolvePackage(args.Path); }
        catch (Exception ex)
        {
            CompletePreExportFailure(task, args.Path, ex);
            return;
        }
        if (task.CancelRequested)
        {
            CompletePreExportFailure(task, args.Path, new OperationCanceledException());
            return;
        }
        if (!resolved.Success)
        {
            task.Result = null;
            task.State = "failed";
            _tasks.AddError(task, args.Path, "resolve", resolved.Error?.Error?.Code ?? "FILE_NOT_FOUND", resolved.Error?.Error?.Message ?? "包不存在");
            task.FinishedAtUtc = DateTimeOffset.UtcNow;
            _tasks.Save(task);
            return;
        }
        if (kind == "exportRaw" && LooksLikeObjectSelector(args.Path))
        {
            task.Result = null;
            task.State = "failed";
            _tasks.AddError(task, args.Path, "resolve", "INVALID_ARGUMENT", "export_raw 只接受包路径");
            task.FinishedAtUtc = DateTimeOffset.UtcNow;
            _tasks.Save(task);
            return;
        }
        ResolvedObject? selectedObject = null;
        if (kind == "exportJson" && (args.ExportIndex is not null || LooksLikeObjectSelector(args.Path)))
        {
            try { selectedObject = ResolveObject(args.Path, args.ExportIndex, cancellationToken); }
            catch (Exception ex)
            {
                CompletePreExportFailure(task, args.Path, ex);
                return;
            }
            if (!selectedObject.Success)
            {
                task.Result = null;
                task.State = "failed";
                _tasks.AddError(task, args.Path, "resolve", selectedObject.Error?.Error?.Code ?? "OBJECT_NOT_FOUND", selectedObject.Error?.Error?.Message ?? "对象不存在");
                task.FinishedAtUtc = DateTimeOffset.UtcNow;
                _tasks.Save(task);
                return;
            }
            resolved = ResolvedPackage.Succeeded(selectedObject.File!, selectedObject.DisplayPackage!, selectedObject.File!.PackageKey);
        }
        if (task.CancelRequested)
        {
            CompletePreExportFailure(task, args.Path, new OperationCanceledException());
            return;
        }
        string? outDir;
        try { outDir = ResolveOutputDirectory(args.OutDir); }
        catch (Exception ex)
        {
            CompletePreExportFailure(task, args.OutDir, ex);
            return;
        }
        if (outDir is null)
        {
            task.Result = null;
            task.State = "failed";
            _tasks.AddError(task, args.OutDir, "validate", "PATH_NOT_ALLOWED", "导出目录必须位于 outputRoot 内");
            task.FinishedAtUtc = DateTimeOffset.UtcNow;
            _tasks.Save(task);
            return;
        }
        var exportId = Guid.NewGuid().ToString("N");
        var parent = Path.Combine(outDir, $".{exportId}.partial");
        var final = Path.Combine(outDir, exportId);
        try
        {
            if (task.CancelRequested) throw new OperationCanceledException();
            Directory.CreateDirectory(outDir);
            if (HasReparsePointInExistingPath(outDir)) throw new PathNotAllowedException(outDir);
            if (Directory.Exists(final) || File.Exists(final)) throw new ExportDestinationException();
            Directory.CreateDirectory(parent);
            if (HasReparsePointInExistingPath(parent)) throw new PathNotAllowedException(parent);
            if (kind == "exportRaw")
            {
                var files = await ReadPackageFilesAsync(resolved.File!, task, cancellationToken).ConfigureAwait(false);
                task.TotalUnits = files.Count;
                task.Phase = "write";
                _tasks.Save(task);
                var manifestFiles = new List<object>();
                long bytes = 0;
                foreach (var pair in files)
                {
                    if (task.CancelRequested) throw new OperationCanceledException();
                    task.CurrentSubject = pair.Key;
                    bytes = checked(bytes + pair.Value.LongLength);
                    if (bytes > _config.Limits.MaxExportBytes) throw new BudgetException("EXPORT_BUDGET_EXCEEDED");
                    var relative = pair.Key.Replace('/', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".." || IsReservedWindowsName(part))) throw new PathNotAllowedException(relative);
                    var target = Path.GetFullPath(Path.Combine(parent, relative));
                    if (!IsWithin(parent, target)) throw new PathNotAllowedException(relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (HasReparsePointInExistingPath(Path.GetDirectoryName(target)!)) throw new PathNotAllowedException(target);
                    await File.WriteAllBytesAsync(target, pair.Value, cancellationToken).ConfigureAwait(false);
                    manifestFiles.Add(new { relativePath = pair.Key, bytes = pair.Value.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant(), sourceArchiveId = resolved.File!.ArchiveId });
                    task.ProcessedUnits++;
                    task.SucceededUnits++;
                    _tasks.Save(task);
                }
                var manifest = new { exportId, format = "legacyPackage", compatibility = "notVerified", sourceFingerprint = _sourceFingerprint, sourceArchiveId = resolved.File!.ArchiveId, files = manifestFiles };
                await WriteManifestAsync(parent, manifest, bytes, cancellationToken).ConfigureAwait(false);
                task.Result = new { exportId, outputDirectory = final, manifestPath = Path.Combine(final, "manifest.json"), format = "legacyPackage", compatibility = "notVerified", files = manifestFiles };
                _tasks.Save(task);
            }
            else
            {
                await WaitForForegroundTurnAsync(task, cancellationToken).ConfigureAwait(false);
                var package = await LoadPackageAsync(resolved.File!, cancellationToken).ConfigureAwait(false);
                var jsonPath = Path.Combine(parent, "package.json");
                var source = JsonSerializer.Serialize(new { packagePath = resolved.DisplayPackage, sourceFilePath = resolved.File!.ProviderPath, archiveId = resolved.File!.ArchiveId }, JsonDefaults.Options);
                var objectCount = 0;
                var selectedCount = selectedObject is null ? package.ExportsLazy.Length : 1;
                task.TotalUnits = selectedCount;
                task.Phase = "write";
                _tasks.Save(task);
                await using (var stream = new FileStream(jsonPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                await using (var boundedStream = new BudgetWriteStream(stream, _config.Limits.MaxExportBytes))
                await using (var writer = new StreamWriter(boundedStream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
                {
                    using var jsonWriter = new JsonTextWriter(writer) { Formatting = Formatting.None, CloseOutput = false };
                    var ueSerializer = Newtonsoft.Json.JsonSerializer.CreateDefault();
                    jsonWriter.WriteStartObject();
                    jsonWriter.WritePropertyName("formatVersion");
                    jsonWriter.WriteValue(1);
                    jsonWriter.WritePropertyName("source");
                    jsonWriter.WriteRawValue(source);
                    jsonWriter.WritePropertyName("exports");
                    jsonWriter.WriteStartArray();
                    jsonWriter.Flush();
                    var indices = selectedObject is null ? Enumerable.Range(0, package.ExportsLazy.Length) : [selectedObject.ExportIndex];
                    foreach (var i in indices)
                    {
                        if (task.CancelRequested) throw new OperationCanceledException();
                        cancellationToken.ThrowIfCancellationRequested();
                        var obj = await LoadObjectAsync(package, i, cancellationToken).ConfigureAwait(false);
                        if (obj is UStruct script && script.ScriptReadState is "partial" or "failed") throw new ScriptParseException();
                        jsonWriter.WriteStartObject();
                        jsonWriter.WritePropertyName("exportIndex");
                        jsonWriter.WriteValue(i);
                        jsonWriter.WritePropertyName("value");
                        ueSerializer.Serialize(jsonWriter, obj);
                        jsonWriter.WriteEndObject();
                        objectCount++;
                        task.ProcessedUnits++;
                        task.SucceededUnits++;
                        task.CurrentSubject = obj.GetPathName();
                        _tasks.Save(task);
                        jsonWriter.Flush();
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        if (stream.Length > _config.Limits.MaxExportBytes) throw new BudgetException("EXPORT_BUDGET_EXCEEDED");
                    }
                    jsonWriter.WriteEndArray();
                    jsonWriter.WriteEndObject();
                    jsonWriter.Flush();
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (stream.Length > _config.Limits.MaxExportBytes) throw new BudgetException("EXPORT_BUDGET_EXCEEDED");
                }
                var info = new FileInfo(jsonPath);
                var manifest = new { exportId, format = "cue4parseJson", sourceFingerprint = _sourceFingerprint, sourceArchiveId = resolved.File!.ArchiveId, files = new[] { new { relativePath = "package.json", bytes = info.Length, sha256 = HashFile(jsonPath), sourceArchiveId = resolved.File!.ArchiveId } }, objectCount, parserBaseline = "CUE4Parse-source-snapshot" };
                await WriteManifestAsync(parent, manifest, info.Length, cancellationToken).ConfigureAwait(false);
                task.Result = new { exportId, outputDirectory = final, manifestPath = Path.Combine(final, "manifest.json"), format = "cue4parseJson", files = manifest.files, objectCount, parserBaseline = manifest.parserBaseline };
                _tasks.Save(task);
            }
            if (task.CancelRequested) throw new OperationCanceledException();
            if (SourceChanged()) throw new SourceChangedException();
            try { Directory.Move(parent, final); }
            catch (IOException) when (Directory.Exists(final) || File.Exists(final)) { throw new ExportDestinationException(); }
            task.State = "completed";
        }
        catch (OperationCanceledException)
        {
            task.Result = null;
            task.State = "cancelled";
            if (!TryDelete(parent)) _tasks.AddError(task, parent, "cleanup", "EXPORT_FAILED", "导出已取消，但临时目录清理失败；请手动处理该路径");
        }
        catch (BudgetException ex)
        {
            task.Result = null;
            task.State = "failed";
            _tasks.AddError(task, args.Path, "export", ex.Code, "导出超过预算");
            if (!TryDelete(parent)) _tasks.AddError(task, parent, "cleanup", "EXPORT_FAILED", "临时目录清理失败；请手动处理该路径");
        }
        catch (Exception ex)
        {
            if (ex is CUE4Parse.UE4.Exceptions.MappingException) _mappingRequiredObserved = true;
            task.Result = null;
            task.State = "failed";
            _tasks.AddError(task, args.Path, "export", ex is IOException ? "EXPORT_FAILED" : MapExceptionCode(ex), ex is ExportDestinationException ? "导出目标已存在" : ex is PathNotAllowedException ? "导出路径不允许" : "导出失败");
            if (!TryDelete(parent)) _tasks.AddError(task, parent, "cleanup", "EXPORT_FAILED", "临时目录清理失败；请手动处理该路径");
        }
        finally
        {
            task.FinishedAtUtc = DateTimeOffset.UtcNow;
            task.CurrentSubject = null;
            task.Phase = null;
            task.CanInterruptCurrentUnit = true;
            _tasks.Save(task);
        }
    }

    private void CompletePreExportFailure(TaskInfo task, string subject, Exception exception)
    {
        task.Result = null;
        task.State = exception is OperationCanceledException ? "cancelled" : "failed";
        if (exception is not OperationCanceledException)
            _tasks.AddError(task, subject, "resolve", exception is IOException ? "EXPORT_FAILED" : MapExceptionCode(exception), "导出解析失败");
        task.FinishedAtUtc = DateTimeOffset.UtcNow;
        task.Phase = null;
        _tasks.Save(task);
    }

    private async Task WaitForForegroundTurnAsync(TaskInfo? task, CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _foregroundParsingRequests) > 0)
        {
            if (task is not null)
            {
                task.CanInterruptCurrentUnit = true;
                if (task.CancelRequested) throw new OperationCanceledException(cancellationToken);
            }
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, byte[]>> ReadPackageFilesAsync(CatalogFile file, TaskInfo task, CancellationToken cancellationToken)
    {
        await WaitForForegroundTurnAsync(task, cancellationToken).ConfigureAwait(false);
        await _parseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<string, byte[]> data;
            if (file.GameFile is VfsEntry entry)
            {
                var basePath = file.GameFile.PathWithoutExtension;
                var selected = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [file.GameFile.Path] = file.GameFile.Read() };
                foreach (var suffix in new[] { ".uexp", ".ubulk", ".uptnl" })
                {
                    var companionPath = basePath + suffix;
                    if (entry.Vfs.Files.TryGetValue(companionPath, out var companion))
                    {
                        selected[companion.Path] = companion.Read();
                    }
                    else if (_provider!.TryGetGameFile(companionPath, out var fallback) && fallback is VfsEntry fallbackEntry && !ReferenceEquals(fallbackEntry.Vfs, entry.Vfs))
                    {
                        throw new PayloadVersionException(companionPath);
                    }
                }
                data = selected;
            }
            else
            {
                data = _provider!.SavePackage(file.ProviderPath);
            }
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long totalBytes = 0;
            foreach (var pair in data)
            {
                totalBytes = checked(totalBytes + pair.Value.LongLength);
                if (totalBytes > _config.Limits.MaxPackageReadBytes) throw new BudgetException("PACKAGE_BUDGET_EXCEEDED");
                result[pair.Key.Replace('\\', '/')] = pair.Value;
            }
            return result;
        }
        finally { _parseGate.Release(); }
    }

    private void RecoverExports()
    {
        foreach (var task in _tasks.Snapshot().Where(task => task.Kind is "exportRaw" or "exportJson" && task.State == "interrupted" && task.Result is not null))
        {
            if (!TryReadExportResult(task.Result, out var exportId, out var outputDirectory, out var manifestPath))
                continue;
            string final;
            string manifest;
            try
            {
                final = Path.GetFullPath(outputDirectory);
                manifest = Path.GetFullPath(manifestPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                continue;
            }
            var committed = string.Equals(Path.GetFileName(final), exportId, StringComparison.OrdinalIgnoreCase)
                && IsWithin(_config.OutputRoot, final)
                && IsWithin(final, manifest)
                && Directory.Exists(final)
                && File.Exists(manifest)
                && ManifestMatches(manifest, exportId);
            lock (task)
            {
                if (committed)
                {
                    task.State = "completed";
                    task.FinishedAtUtc ??= File.GetLastWriteTimeUtc(manifest);
                    task.Phase = null;
                    task.CurrentSubject = null;
                }
                else
                {
                    task.Result = null;
                    var parent = Path.Combine(Path.GetDirectoryName(final) ?? _config.OutputRoot, $".{exportId}.partial");
                    if (IsWithin(_config.OutputRoot, parent)) TryDelete(parent);
                }
                _tasks.Save(task);
            }
        }

        foreach (var partial in EnumeratePartialExportDirectories(_config.OutputRoot))
            TryDelete(partial);
    }

    private static bool TryReadExportResult(object? result, out string exportId, out string outputDirectory, out string manifestPath)
    {
        exportId = outputDirectory = manifestPath = string.Empty;
        JsonElement element;
        try
        {
            element = result switch
            {
                JsonElement json => json,
                _ => JsonSerializer.SerializeToElement(result, JsonDefaults.Options)
            };
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("exportId", out var id)
                || !element.TryGetProperty("outputDirectory", out var output)
                || !element.TryGetProperty("manifestPath", out var manifest)
                || id.ValueKind != JsonValueKind.String
                || output.ValueKind != JsonValueKind.String
                || manifest.ValueKind != JsonValueKind.String)
                return false;
            exportId = id.GetString() ?? string.Empty;
            outputDirectory = output.GetString() ?? string.Empty;
            manifestPath = manifest.GetString() ?? string.Empty;
            return exportId.Length == 32 && exportId.All(Uri.IsHexDigit) && outputDirectory.Length > 0 && manifestPath.Length > 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool ManifestMatches(string manifestPath, string exportId)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("exportId", out var id)
                && string.Equals(id.GetString(), exportId, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumeratePartialExportDirectories(string root)
    {
        var pending = new Stack<string>([Path.GetFullPath(root)]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(current); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var directory in directories)
            {
                FileAttributes attributes;
                try { attributes = new DirectoryInfo(directory).Attributes; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var name = Path.GetFileName(directory);
                if (name.StartsWith(".", StringComparison.Ordinal) && name.EndsWith(".partial", StringComparison.Ordinal) && name.Length == 41 && name[33] == '.' && name[1..33].All(Uri.IsHexDigit))
                {
                    yield return directory;
                    continue;
                }
                pending.Push(directory);
            }
        }
    }

    private async Task WriteManifestAsync(string directory, object manifest, long existingBytes, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonDefaults.Options);
        if (checked(existingBytes + content.LongLength) > _config.Limits.MaxExportBytes)
            throw new BudgetException("EXPORT_BUDGET_EXCEEDED");
        await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IPackage> LoadPackageAsync(CatalogFile file, CancellationToken cancellationToken)
    {
        ValidatePayloadSource(file);
        ValidatePackageReadBudget(file);
        await _parseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _provider!.LoadPackage(file.ProviderPath); }
        finally { _parseGate.Release(); }
    }

    private void ValidatePackageReadBudget(CatalogFile file)
    {
        long total = file.Size;
        if (file.GameFile is VfsEntry entry)
        {
            var basePath = file.GameFile.PathWithoutExtension;
            foreach (var suffix in new[] { ".uexp", ".ubulk", ".uptnl" })
                if (entry.Vfs.Files.TryGetValue(basePath + suffix, out var companion)) total = checked(total + companion.Size);
        }
        if (total > _config.Limits.MaxPackageReadBytes) throw new BudgetException("PACKAGE_BUDGET_EXCEEDED");
    }

    private void ValidatePayloadSource(CatalogFile file)
    {
        if (file.GameFile is not VfsEntry entry || _provider is null) return;
        var basePath = file.GameFile.PathWithoutExtension;
        foreach (var suffix in new[] { ".uexp", ".ubulk", ".uptnl" })
        {
            var companionPath = basePath + suffix;
            if (entry.Vfs.Files.ContainsKey(companionPath)) continue;
            if (_provider.TryGetGameFile(companionPath, out var fallback) && fallback is VfsEntry fallbackEntry && !ReferenceEquals(fallbackEntry.Vfs, entry.Vfs))
                throw new PayloadVersionException(companionPath);
        }
    }

    private async Task<UObject> LoadObjectAsync(IPackage package, int exportIndex, CancellationToken cancellationToken)
    {
        await _parseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return package.GetExport(exportIndex) ?? throw new ParserException("对象无法解析"); }
        finally { _parseGate.Release(); }
    }

    private ResolvedPackage ResolvePackage(string path)
    {
        var packagePath = NormalizePackageSelector(path);
        var dot = packagePath.LastIndexOf('.');
        if (dot > packagePath.LastIndexOf('/'))
        {
            var suffix = packagePath[(dot + 1)..];
            if (!suffix.Equals("uasset", StringComparison.OrdinalIgnoreCase) && !suffix.Equals("umap", StringComparison.OrdinalIgnoreCase)) packagePath = packagePath[..dot];
        }
        var normalized = packagePath.Replace('\\', '/');
        if (!normalized.StartsWith('/')) normalized = "/" + normalized;
        var key = normalized.TrimEnd('/').ToLowerInvariant();
        var providerKey = normalized.TrimStart('/');
        var candidates = _catalog!.Files.Where(file => file.IsPackage &&
            (file.PackageKey.Equals(key, StringComparison.OrdinalIgnoreCase) || file.DisplayPath.Equals(normalized, StringComparison.OrdinalIgnoreCase) || file.ProviderPath.Equals(providerKey, StringComparison.OrdinalIgnoreCase) || PackagePathWithoutExtension(file.ProviderPath).Equals(providerKey, StringComparison.OrdinalIgnoreCase))).ToList();
        if (!packagePath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && !packagePath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
        {
            var extensions = candidates.Select(file => file.Extension).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (extensions.Length > 1) return ResolvedPackage.Failed(ErrorEnvelope("AMBIGUOUS_PACKAGE", "同一路径同时存在 uasset 和 umap", false, "指定完整文件路径"));
        }
        var file = candidates.FirstOrDefault();
        if (file is null) return ResolvedPackage.Failed(ErrorEnvelope("FILE_NOT_FOUND", "包不存在", false, "先使用 search_files"));
        if (file.Conflict) return ResolvedPackage.Failed(ErrorEnvelope("AMBIGUOUS_OVERRIDE", "该路径存在同级最高优先级冲突", false, "修复容器冲突后 remount"));
        return ResolvedPackage.Succeeded(file, PackageDisplay(file.DisplayPath), file.PackageKey);
    }

    private ResolvedObject ResolveObject(string path, int? exportIndex, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePackageSelector(path);
        var packagePath = normalizedPath;
        string? objectPart = null;
        var ext = normalizedPath.LastIndexOf(".uasset", StringComparison.OrdinalIgnoreCase);
        var extMap = normalizedPath.LastIndexOf(".umap", StringComparison.OrdinalIgnoreCase);
        var packageExt = Math.Max(ext, extMap);
        if (packageExt >= 0)
        {
            var extensionLength = ext >= extMap ? ".uasset".Length : ".umap".Length;
            packagePath = normalizedPath[..(packageExt + extensionLength)];
            objectPart = normalizedPath[(packageExt + extensionLength)..].TrimStart('.', ':');
        }
        else
        {
            var slash = normalizedPath.LastIndexOf('/');
            var dot = normalizedPath.IndexOf('.', slash + 1);
            var colon = normalizedPath.IndexOf(':', slash + 1);
            var separator = dot < 0 ? colon : colon < 0 ? dot : Math.Min(dot, colon);
            if (separator > slash)
            {
                packagePath = normalizedPath[..separator];
                objectPart = normalizedPath[(separator + 1)..];
            }
        }
        if (exportIndex is not null && !string.IsNullOrWhiteSpace(objectPart))
            return ResolvedObject.Failed(ErrorEnvelope("INVALID_ARGUMENT", "path 已包含对象部分时不能同时指定 exportIndex", false, "二选一使用对象路径或 exportIndex"));
        var package = ResolvePackage(packagePath);
        if (!package.Success) return ResolvedObject.Failed(package.Error!);
        var loaded = LoadPackageAsync(package.File!, cancellationToken).GetAwaiter().GetResult();
        var index = exportIndex;
        if (index is null && !string.IsNullOrWhiteSpace(objectPart))
        {
            var candidates = FindExportIndices(loaded, objectPart);
            if (candidates.Count != 1) return ResolvedObject.Failed(ErrorEnvelope(candidates.Count == 0 ? "OBJECT_NOT_FOUND" : "AMBIGUOUS_OBJECT", "对象选择不唯一", false, "使用 list_objects 并指定 exportIndex"));
            index = candidates[0];
        }
        if (index is null)
        {
            if (loaded.ExportsLazy.Length != 1) return ResolvedObject.Failed(ErrorEnvelope("AMBIGUOUS_OBJECT", "包内有多个 export，必须指定 exportIndex", false, "先使用 list_objects"));
            index = 0;
        }
        if (index < 0 || index >= loaded.ExportsLazy.Length) return ResolvedObject.Failed(ErrorEnvelope("OBJECT_NOT_FOUND", "exportIndex 不存在", false, null));
        var metadata = loaded.ResolvePackageIndex(new FPackageIndex(loaded, index.Value + 1));
        if (metadata is null) return ResolvedObject.Failed(ErrorEnvelope("OBJECT_NOT_FOUND", "对象元数据无法解析", false, null));
        return ResolvedObject.Succeeded(package.File!, loaded, index.Value, ObjectDisplayPath(package.DisplayPackage!, metadata), package.DisplayPackage!);
    }

    private static List<int> FindExportIndices(IPackage package, string objectPart)
    {
        var result = new List<int>();
        for (var i = 0; i < package.ExportsLazy.Length; i++)
        {
            CUE4Parse.UE4.Assets.ResolvedObject? metadata;
            try { metadata = package.ResolvePackageIndex(new FPackageIndex(package, i + 1)); }
            catch { continue; }
            if (metadata is not null && (metadata.Name.Text.Equals(objectPart, StringComparison.Ordinal) || metadata.GetPathName().Equals(objectPart, StringComparison.Ordinal) || metadata.GetPathName().EndsWith("." + objectPart, StringComparison.Ordinal) || metadata.GetPathName().EndsWith(":" + objectPart, StringComparison.Ordinal))) result.Add(i);
        }
        return result;
    }

    private static bool LooksLikeObjectSelector(string path)
    {
        var normalized = path.Replace('\\', '/');
        var packageEnd = Math.Max(normalized.LastIndexOf(".uasset", StringComparison.OrdinalIgnoreCase), normalized.LastIndexOf(".umap", StringComparison.OrdinalIgnoreCase));
        if (packageEnd >= 0) return normalized.Length > packageEnd + (normalized[packageEnd..].StartsWith(".uasset", StringComparison.OrdinalIgnoreCase) ? ".uasset".Length : ".umap".Length);
        var slash = normalized.LastIndexOf('/');
        var dot = normalized.LastIndexOf('.');
        var colon = normalized.LastIndexOf(':');
        var separator = Math.Max(dot, colon);
        return separator > slash && separator < normalized.Length - 1;
    }

    private static string NormalizePackageSelector(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path 不能为空", nameof(path));
        var value = path.Replace('\\', '/').Trim();
        var colon = value.IndexOf(':');
        var drivePrefix = value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':';
        var objectColon = colon > value.LastIndexOf('/') && (value.StartsWith('/') || value.Contains(".uasset", StringComparison.OrdinalIgnoreCase) || value.Contains(".umap", StringComparison.OrdinalIgnoreCase));
        if (drivePrefix || value.StartsWith("//", StringComparison.Ordinal) || (colon >= 0 && !objectColon) || value.Contains('*') || value.Contains('?') || value.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new ArgumentException("path 必须是 Provider 虚拟路径，不能包含磁盘前缀、glob 或 ..", nameof(path));
        return value.StartsWith('/') ? value : "/" + value;
    }

    private static bool IsWithin(string parent, string child)
    {
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedChild = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedParent, normalizedChild, StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveOutputDirectory(string relative)
    {
        var parts = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || parts.Any(part => part is "." or ".." || IsReservedWindowsName(part))) return null;
        var full = Path.GetFullPath(Path.Combine(_config.OutputRoot, relative));
        return IsWithin(_config.OutputRoot, full) && !HasReparsePointInExistingPath(full) ? full : null;
    }

    private static bool IsReservedWindowsName(string segment)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var name = segment.TrimEnd('.', ' ');
        var stem = name.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');
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
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
        catch (ArgumentException) { return true; }
    }

    private static bool TryDelete(string path)
    {
        if (!Directory.Exists(path)) return true;
        try
        {
            Directory.Delete(path, true);
            return !Directory.Exists(path);
        }
        catch { return false; }
    }

    private object SuccessEnvelope(object? data = null, PageInfo? page = null, CoverageInfo? coverage = null, IReadOnlyList<WarningInfo>? warnings = null)
        => new ToolEnvelope { Ok = true, SessionId = _sessionId, Data = data, Page = page, Coverage = coverage, Warnings = warnings ?? [] };

    private ToolEnvelope ErrorEnvelope(string code, string message, bool retryable, string? suggestedAction, object? data = null, PageInfo? page = null, CoverageInfo? coverage = null, object? details = null)
        => new() { Ok = false, SessionId = _sessionId, Data = data, Page = page, Coverage = coverage, Warnings = _warnings.TakeLast(20).ToArray(), Error = new ToolError { Code = code, Message = message, Retryable = retryable, SuggestedAction = suggestedAction, Details = details } };

    private T Deserialize<T>(JsonElement? payload)
        => payload is { ValueKind: JsonValueKind.Object }
            ? payload.Value.Deserialize<T>(JsonDefaults.Options)!
            : JsonSerializer.Deserialize<T>("{}", JsonDefaults.Options)!;

    private CoverageInfo CoverageForCatalog(string? scope)
    {
        var normalized = NormalizeCatalogScope(scope);
        var total = _catalog?.Files.Count(file => InScope(file.DisplayPath, normalized) || InProviderScope(file.ProviderPath, normalized)) ?? 0;
        return new CoverageInfo
        {
            Scope = normalized,
            TotalUnits = total,
            SucceededUnits = total,
            FailedUnits = 0,
            SkippedUnits = 0,
            Complete = _catalog?.CatalogComplete ?? false,
            CatalogComplete = _catalog?.CatalogComplete ?? false,
            SourceFingerprint = _sourceFingerprint
        };
    }

    private string NormalizeCatalogScope(string? scope)
    {
        var normalized = NormalizeScope(scope);
        if (_catalog is null || _catalog.Files.Any(file => InScope(file.DisplayPath, normalized))) return normalized;
        var providerScope = normalized.TrimStart('/');
        var matches = _catalog.Files.Where(file => InProviderScope(file.ProviderPath, providerScope)).ToArray();
        if (matches.Length == 0) return normalized;
        foreach (var file in matches)
        {
            var relative = file.ProviderPath.Length == providerScope.Length
                ? string.Empty
                : file.ProviderPath[(providerScope.Length + 1)..];
            if (relative.Length == 0) return file.DisplayPath;
            var suffix = "/" + relative;
            if (file.DisplayPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return file.DisplayPath[..^suffix.Length];
        }
        return normalized;
    }

    private static string NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return "/Game";
        var value = scope.Replace('\\', '/').Trim();
        if (value.Contains(':') || value.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".." || part.Contains('*') || part.Contains('?')))
            throw new ArgumentException("scope/dir 必须是无 glob、无 .. 的 Provider 虚拟目录");
        if (!value.StartsWith('/')) value = "/" + value;
        return value.TrimEnd('/');
    }

    private static bool InScope(string path, string scope)
        => path.StartsWith(scope.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) || string.Equals(path, scope, StringComparison.OrdinalIgnoreCase);

    private static bool InProviderScope(string path, string scope)
    {
        var normalizedPath = path.TrimStart('/');
        var normalizedScope = scope.TrimStart('/').TrimEnd('/');
        return normalizedPath.StartsWith(normalizedScope + "/", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedPath, normalizedScope, StringComparison.OrdinalIgnoreCase);
    }

    private static string PackageDisplay(string displayPath)
        => displayPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || displayPath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) ? displayPath[..displayPath.LastIndexOf('.')].Replace("/Content/", "/", StringComparison.OrdinalIgnoreCase) : displayPath;

    private static string PackagePathWithoutExtension(string path)
        => path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) ? path[..path.LastIndexOf('.')].TrimEnd('/') : path;

    private static string ObjectDisplayPath(string packagePath, CUE4Parse.UE4.Assets.ResolvedObject metadata)
    {
        var displayPackage = PackageDisplay(packagePath);
        var raw = metadata.GetPathName().Replace('\\', '/');
        var packageName = metadata.Package.Name.Replace('\\', '/').TrimEnd('/');
        var suffix = raw.StartsWith(packageName + ".", StringComparison.OrdinalIgnoreCase) || raw.StartsWith(packageName + ":", StringComparison.OrdinalIgnoreCase)
            ? raw[(packageName.Length + 1)..]
            : raw.StartsWith(displayPackage + ".", StringComparison.OrdinalIgnoreCase) || raw.StartsWith(displayPackage + ":", StringComparison.OrdinalIgnoreCase)
                ? raw[(displayPackage.Length + 1)..]
                : raw;
        if (packageName.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) || packageName.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(suffix) ? packageName : packageName + "." + suffix.TrimStart('.', ':');
        return string.IsNullOrWhiteSpace(suffix) ? displayPackage : displayPackage + "." + suffix.TrimStart('.', ':');
    }

    private static string NormalizeExtension(string extension) => extension.StartsWith('.') ? extension[1..] : extension;

    private static string GlobToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') { builder.Append(".*"); i++; }
            else if (c == '*') builder.Append("[^/]*");
            else if (c == '?') builder.Append("[^/]");
            else builder.Append(Regex.Escape(c.ToString()));
        }
        return builder.Append('$').ToString();
    }

    private int NormalizeLimit(int? limit)
    {
        var value = limit ?? _config.Limits.DefaultPageSize;
        if (value <= 0 || value > _config.Limits.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
        return value;
    }

    private Paged<T> Page<T>(IReadOnlyList<T> items, int? limit, string? cursor, string operation, object filter, string? snapshotId = null)
    {
        var pageSize = NormalizeLimit(limit);
        var offset = CursorOffset(cursor, operation, filter, snapshotId);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            CursorData data;
            try { data = DecodeCursorData(cursor); }
            catch (CursorException) { throw; }
            catch { throw new CursorException("INVALID_CURSOR"); }
            if (offset <= 0 || offset >= items.Count || data.LastKey is not null && !string.Equals(data.LastKey, SortKey(items[offset - 1]), StringComparison.Ordinal))
                throw new CursorException("STALE_CURSOR");
        }
        var selected = items.Skip(offset).Take(pageSize).ToList();
        var next = offset + selected.Count < items.Count ? CursorEncode(operation, filter, offset + selected.Count, snapshotId, selected.Count == 0 ? null : SortKey(selected[^1])) : null;
        return new Paged<T>(selected, new PageInfo(pageSize, selected.Count, next));
    }

    private CursorData DecodeCursorData(string cursor)
    {
        var json = Encoding.UTF8.GetString(Base64UrlDecode(cursor));
        return JsonSerializer.Deserialize<CursorData>(json, JsonDefaults.Options) ?? throw new FormatException();
    }

    private int CursorOffset(string? cursor, string operation, object filter, string? snapshotId = null)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var data = DecodeCursorData(cursor);
            if (data.Version != 1 || !string.Equals(data.SessionId, _sessionId, StringComparison.Ordinal) || !string.Equals(data.CatalogRevision, _sourceFingerprint, StringComparison.Ordinal) || !string.Equals(data.SnapshotId, snapshotId, StringComparison.Ordinal))
                throw new CursorException("STALE_CURSOR");
            var hash = HashObject(filter);
            if (!string.Equals(data.Operation, operation, StringComparison.Ordinal) || !string.Equals(data.FilterHash, hash, StringComparison.Ordinal)) throw new InvalidDataException("cursor query mismatch");
            return data.Offset < 0 ? throw new InvalidDataException() : data.Offset;
        }
        catch (CursorException) { throw; }
        catch (InvalidDataException) { throw new CursorException("CURSOR_QUERY_MISMATCH"); }
        catch { throw new CursorException("INVALID_CURSOR"); }
    }

    private string CursorEncode(string operation, object filter, int offset, string? snapshotId, string? lastKey)
    {
        var json = JsonSerializer.Serialize(new CursorData(1, _sessionId, operation, HashObject(filter), offset, _sourceFingerprint, snapshotId, lastKey), JsonDefaults.Options);
        return Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    }

    private static string SortKey<T>(T value)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options))).ToLowerInvariant();

    private static string HashObject(object value)
    {
        var node = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options));
        if (node is JsonObject objectNode) objectNode.Remove("cursor");
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(node, JsonDefaults.Options))).ToLowerInvariant();
    }
    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));

    private ProjectionResult Project(JToken token, string[]? fields, int depth)
    {
        if (depth < 0 || depth > _config.Limits.MaxDepth)
            return ProjectionResult.Failed("OUTPUT_BUDGET_EXCEEDED", "depth 超过配置预算", "降低 depth");
        if (fields is { Length: 0 })
            return ProjectionResult.Failed("INVALID_ARGUMENT", "fields 不能为空数组", "省略 fields 或提供至少一个 JSON Pointer");
        JToken clone;
        try { clone = fields is null ? token.DeepClone() : FilterFields(token, fields); }
        catch (FieldSelectionException ex) { return ProjectionResult.Failed("FIELD_NOT_FOUND", ex.Message, "检查 JSON Pointer 转义"); }
        var omissions = new List<object>();
        var visited = 0;
        if (fields is not null && !CountTokens(token, _config.Limits.MaxVisitedJsonTokens, ref visited))
            return ProjectionResult.Failed("OUTPUT_BUDGET_EXCEEDED", "对象遍历超过 maxVisitedJsonTokens", "缩小 fields 或 depth");
        if (!ApplyDepth(clone, depth, "", omissions, ref visited))
            return ProjectionResult.Failed("OUTPUT_BUDGET_EXCEEDED", "对象遍历超过 maxVisitedJsonTokens", "缩小 fields 或 depth");
        return ProjectionResult.Succeeded(clone, omissions);
    }

    private static JToken FilterFields(JToken token, string[] fields)
    {
        JToken root = token is JArray ? new JArray() : new JObject();
        foreach (var field in fields)
        {
            if (!TrySelectPointer(token, field, out var source)) throw new FieldSelectionException($"字段不存在: {field}");
            if (field.Length == 0) return source!.DeepClone();
            var pointer = ParsePointer(field);
            AddPointer(root, pointer, source!, token);
        }
        return root;
    }

    private static bool TrySelectPointer(JToken token, string pointer, out JToken? value)
    {
        value = token;
        if (pointer.Length == 0) return true;
        if (!pointer.StartsWith('/')) { value = null; return false; }
        foreach (var segment in ParsePointer(pointer))
        {
            value = value switch
            {
                JObject obj when obj.TryGetValue(segment, StringComparison.Ordinal, out var child) => child,
                JArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null
            };
            if (value is null) return false;
        }
        return true;
    }

    private static string[] ParsePointer(string pointer)
    {
        if (!pointer.StartsWith('/')) throw new FieldSelectionException($"字段不是 JSON Pointer: {pointer}");
        return pointer.Split('/').Skip(1).Select(segment =>
        {
            for (var i = 0; i < segment.Length; i++)
                if (segment[i] == '~' && (i + 1 >= segment.Length || segment[i + 1] is not ('0' or '1')))
                    throw new FieldSelectionException($"字段不是有效 JSON Pointer: {pointer}");
            return segment.Replace("~1", "/").Replace("~0", "~");
        }).ToArray();
    }

    private static void AddPointer(JToken root, IReadOnlyList<string> pointer, JToken source, JToken sourceRoot)
        => AddPointerPart(root, pointer, 0, source, sourceRoot);

    private static void AddPointerPart(JToken current, IReadOnlyList<string> pointer, int position, JToken source, JToken sourceRoot)
    {
        var segment = pointer[position];
        var last = position == pointer.Count - 1;
        if (current is JObject objectNode)
        {
            if (last) { objectNode[segment] = source.DeepClone(); return; }
            var child = objectNode[segment];
            if (child is JValue) return;
            if (child is null) objectNode[segment] = child = SelectPointerPrefix(sourceRoot, pointer, position + 1) is JArray ? new JArray() : new JObject();
            AddPointerPart(child, pointer, position + 1, source, sourceRoot);
            return;
        }
        if (current is JArray array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0)
        {
            while (array.Count <= index) array.Add(JValue.CreateNull());
            if (last) { array[index] = source.DeepClone(); return; }
            var child = array[index];
            if (child is JValue) return;
            if (child is null) array[index] = child = SelectPointerPrefix(sourceRoot, pointer, position + 1) is JArray ? new JArray() : new JObject();
            AddPointerPart(child, pointer, position + 1, source, sourceRoot);
            return;
        }
        throw new FieldSelectionException($"数组路径必须明确十进制索引: /{string.Join('/', pointer)}");
    }

    private static JToken? SelectPointerPrefix(JToken root, IReadOnlyList<string> pointer, int count)
    {
        var value = root;
        for (var i = 0; i < count; i++)
        {
            value = value switch
            {
                JObject obj when obj.TryGetValue(pointer[i], StringComparison.Ordinal, out var child) => child,
                JArray array when int.TryParse(pointer[i], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null
            };
            if (value is null) return null;
        }
        return value;
    }

    private static bool CountTokens(JToken token, int maximum, ref int count)
    {
        if (++count > maximum) return false;
        if (token is not JContainer container) return true;
        foreach (var child in container.Children())
            if (!CountTokens(child is JProperty property ? property.Value : child, maximum, ref count)) return false;
        return true;
    }

    private bool ApplyDepth(JToken token, int depth, string pointer, List<object> omissions, ref int visited)
    {
        if (++visited > _config.Limits.MaxVisitedJsonTokens) return false;
        if (token is not JContainer container) return true;
        if (depth <= 0)
        {
            if (container is JObject objectNode)
            {
                foreach (var property in objectNode.Properties().ToList())
                {
                    var childPointer = pointer + "/" + property.Name.Replace("~", "~0").Replace("/", "~1");
                    property.Value = JValue.CreateNull();
                    omissions.Add(new { pointer = childPointer, reason = "depth" });
                    if (++visited > _config.Limits.MaxVisitedJsonTokens) return false;
                }
            }
            else if (container is JArray arrayNode)
            {
                for (var index = 0; index < arrayNode.Count; index++)
                {
                    arrayNode[index] = JValue.CreateNull();
                    omissions.Add(new { pointer = pointer + "/" + index.ToString(CultureInfo.InvariantCulture), reason = "depth" });
                    if (++visited > _config.Limits.MaxVisitedJsonTokens) return false;
                }
            }
            return true;
        }
        foreach (var child in container.Children())
        {
            var childName = child is JProperty property ? property.Name : ((JArray)container).IndexOf(child).ToString(CultureInfo.InvariantCulture);
            if (!ApplyDepth(child is JProperty propertyValue ? propertyValue.Value : child, depth - 1, pointer + "/" + childName, omissions, ref visited)) return false;
        }
        return true;
    }

    private static JToken ApplyFields(JToken token, string[]? fields) => fields is null ? token : FilterFields(token, fields);

    private static JsonElement ToJsonElement(JToken token) => JsonDocument.Parse(token.ToString(Formatting.None)).RootElement.Clone();

    private static JToken JsonElementToToken(JsonElement? element)
    {
        if (element is not { } value) return JValue.CreateNull();
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => JValue.CreateNull(),
            JsonValueKind.String => JValue.CreateString(value.GetString()),
            JsonValueKind.Number => value.TryGetInt64(out var integer) ? new JValue(integer) : value.TryGetUInt64(out var unsignedInteger) ? new JValue(unsignedInteger) : value.TryGetDecimal(out var decimalValue) ? new JValue(decimalValue) : JToken.Parse(value.GetRawText()),
            JsonValueKind.True => new JValue(true),
            JsonValueKind.False => new JValue(false),
            _ => JToken.Parse(value.GetRawText())
        };
    }

    private bool MatchesFilters(JToken row, FilterInput[]? filters, out string? error)
    {
        error = null;
        if (filters is null) return true;
        foreach (var filter in filters)
        {
            if (!TrySelectPointer(row, filter.Field, out var value))
            {
                if (filter.Op == "exists") value = null;
                else return false;
            }
            var exists = value is not null;
            if (filter.Op == "exists")
            {
                if (filter.Value is { } existsValue && existsValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    error = "exists 的 value 必须为布尔值";
                    return false;
                }
                if (exists != (filter.Value?.GetBoolean() ?? true)) return false;
                continue;
            }
            if (!exists) return false;
            var expected = JsonElementToToken(filter.Value);
            if (filter.Op == "contains")
            {
                if (value!.Type != JTokenType.String || expected.Type != JTokenType.String) { error = "contains 只支持字符串"; return false; }
                if (!value.Value<string>()!.Contains(expected.Value<string>() ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
                continue;
            }
            if (filter.Op is "eq" or "ne" && value!.Type != expected.Type)
            {
                error = "eq/ne 的 value 类型必须与字段类型一致";
                return false;
            }
            if (filter.Op is "gt" or "gte" or "lt" or "lte" && (value!.Type != expected.Type || value.Type is not (JTokenType.Integer or JTokenType.Float)))
            {
                error = "比较运算只支持同类型 JSON 数字";
                return false;
            }
            var equals = JToken.DeepEquals(value, expected);
            if (filter.Op == "eq" && !equals || filter.Op == "ne" && equals) return false;
            if (filter.Op is "gt" or "gte" or "lt" or "lte" && !Compare(value!, expected, filter.Op)) return false;
        }
        return true;
    }

    private static bool Compare(JToken left, JToken right, string op)
    {
        int comparison;
        if (left.Type == JTokenType.Integer)
        {
            var l = BigInteger.Parse(left.ToString(Formatting.None), CultureInfo.InvariantCulture);
            var r = BigInteger.Parse(right.ToString(Formatting.None), CultureInfo.InvariantCulture);
            comparison = l.CompareTo(r);
        }
        else
        {
            if (!decimal.TryParse(left.ToString(Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out var l) ||
                !decimal.TryParse(right.ToString(Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
                throw new ArgumentException("比较数字超出可精确比较的范围");
            comparison = l.CompareTo(r);
        }
        return op switch { "gt" => comparison > 0, "gte" => comparison >= 0, "lt" => comparison < 0, "lte" => comparison <= 0, _ => false };
    }

    private static bool HasUnknownFilterField(IEnumerable<(string Name, JToken Value)> rows, FilterInput[] filters)
    {
        var materialized = rows.ToArray();
        return filters.Any(filter => materialized.Length == 0 || materialized.All(row => !TrySelectPointer(row.Value, filter.Field, out _)));
    }

    private static bool HasUnknownField(IEnumerable<(string Name, JToken Value)> rows, string[] fields)
    {
        var materialized = rows.ToArray();
        return materialized.Length == 0 || fields.Any(field => materialized.All(row => !TrySelectPointer(row.Value, field, out _)));
    }

    private static object[] InferSchema(IEnumerable<(string Name, JToken Value)> rows)
    {
        var fields = new Dictionary<string, SchemaObservation>(StringComparer.Ordinal);
        foreach (var row in rows) ObserveSchema(row.Value, "");
        return fields.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (object)new
        {
            fieldPointer = pair.Key,
            type = string.Join("|", pair.Value.Types.OrderBy(type => type, StringComparer.Ordinal)),
            source = "observed",
            nullable = (bool?)pair.Value.Nullable,
            availability = pair.Value.Types.Count > 1 ? "ambiguous" : "observed"
        }).ToArray();

        void ObserveSchema(JToken value, string pointer)
        {
            if (!fields.TryGetValue(pointer, out var observation))
                fields[pointer] = observation = new SchemaObservation();
            if (value.Type == JTokenType.Null)
            {
                observation.Nullable = true;
                return;
            }
            observation.Types.Add(value.Type.ToString());
            switch (value)
            {
                case JObject obj when obj.Count == 0:
                    return;
                case JObject obj:
                    foreach (var property in obj.Properties())
                    {
                        var escaped = property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                        ObserveSchema(property.Value, pointer + "/" + escaped);
                    }
                    break;
                case JArray array:
                    foreach (var item in array) ObserveSchema(item, pointer + "/*");
                    break;
            }
        }
    }

    private static IReadOnlyList<object> GetParents(UClass cls, string packageDisplay)
    {
        var result = new List<object>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var parent = cls.SuperStruct;
        if (parent is null) return result;
        var parentPath = parent.ResolvedObject is { } parentObject ? ObjectDisplayPath(packageDisplay, parentObject) : parent.Name;
        UClass? current;
        try { current = parent.Load<UClass>(); }
        catch
        {
            result.Add(new { classPath = parentPath, source = "reflection", resolved = false, stopReason = "unresolved" });
            return result;
        }
        if (current is null)
        {
            result.Add(new { classPath = parentPath, source = "reflection", resolved = false, stopReason = "unresolved" });
            return result;
        }
        while (current is not null)
        {
            if (!visited.Add(parentPath))
            {
                result.Add(new { classPath = parentPath, source = "reflection", resolved = false, stopReason = "cycle" });
                break;
            }
            result.Add(new { classPath = parentPath, source = "reflection", resolved = true, stopReason = (string?)null });
            var super = current.SuperStruct;
            if (super is null) break;
            parentPath = super.ResolvedObject is { } superObject ? ObjectDisplayPath(packageDisplay, superObject) : super.Name;
            try { current = super.Load<UClass>(); }
            catch
            {
                result.Add(new { classPath = parentPath, source = "reflection", resolved = false, stopReason = "unresolved" });
                break;
            }
            if (current is null)
            {
                result.Add(new { classPath = parentPath, source = "reflection", resolved = false, stopReason = "unresolved" });
                break;
            }
        }
        return result;
    }

    private static IReadOnlyList<object> GetProperties(UClass cls, string classPath, string packageDisplay, bool inherited)
    {
        var result = new List<object>();
        var current = cls;
        var currentPath = classPath;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && visited.Add(currentPath))
        {
            CUE4Parse.UE4.Assets.Exports.UObject? cdo = null;
            try { cdo = current.ClassDefaultObject.Load(); } catch { }
            foreach (var field in current.ChildProperties ?? [])
            {
                if (field is not FProperty property || !emitted.Add(property.Name.Text)) continue;
                object? defaultValue = null;
                var defaultState = "notSerialized";
                var defaultSource = (string?)null;
                try
                {
                    var tag = cdo?.Properties.LastOrDefault(item => item.Name.Text == property.Name.Text && item.ArrayIndex == 0);
                    if (tag?.Tag is { } value)
                    {
                        defaultValue = JToken.Parse(JsonConvert.SerializeObject(value, Formatting.None));
                        defaultState = ReferenceEquals(current, cls) ? "serialized" : "inheritedSerialized";
                        defaultSource = current.ClassDefaultObject.ResolvedObject is { } cdoObject ? ObjectDisplayPath(packageDisplay, cdoObject) : cdo?.GetPathName();
                    }
                }
                catch { defaultState = "unavailable"; }
                result.Add(new { name = property.Name.Text, declaredType = property.GetType().Name[1..], arrayDim = property.ArrayDim, flags = property.PropertyFlags.ToString(), declaringClassPath = currentPath, defaultValue, defaultState, defaultSource, source = "reflection" });
            }
            if (!inherited) break;
            var super = current.SuperStruct;
            if (super is null) break;
            currentPath = super.ResolvedObject is { } superObject ? ObjectDisplayPath(packageDisplay, superObject) : super.Name;
            try { current = super.Load<UClass>(); }
            catch { break; }
        }
        return result;
    }

    private static IReadOnlyList<object> GetFunctions(UClass cls, string classPath, string packageDisplay, bool inherited)
    {
        var result = new List<object>();
        var current = cls;
        var currentPath = classPath;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && visited.Add(currentPath))
        {
            foreach (var pair in current.FuncMap ?? [])
            {
                if (!emitted.Add(pair.Key.Text)) continue;
                CUE4Parse.UE4.Objects.UObject.UFunction? function = null;
                try { function = pair.Value.Load<CUE4Parse.UE4.Objects.UObject.UFunction>(); } catch { }
                var objectPath = pair.Value.ResolvedObject is { } functionObject ? ObjectDisplayPath(packageDisplay, functionObject) : null;
                result.Add(new { name = pair.Key.Text, objectPath, flags = function?.FunctionFlags.ToString(), declaringClassPath = currentPath, availability = function is null ? "unresolved" : "available" });
            }
            if (!inherited) break;
            var super = current.SuperStruct;
            if (super is null) break;
            currentPath = super.ResolvedObject is { } superObject ? ObjectDisplayPath(packageDisplay, superObject) : super.Name;
            try { current = super.Load<UClass>(); }
            catch { break; }
        }
        return result;
    }

    private static IReadOnlyList<object> GetParameters(UFunction function)
        => (function.ChildProperties ?? []).OfType<FProperty>().Where(property => property.PropertyFlags.HasFlag(EPropertyFlags.Parm)).Select(property => new
        {
            name = property.Name.Text,
            type = property.GetType().Name[1..],
            flags = property.PropertyFlags.ToString(),
            direction = property.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm) ? "return" : property.PropertyFlags.HasFlag(EPropertyFlags.OutParm) && property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) ? "inout" : property.PropertyFlags.HasFlag(EPropertyFlags.OutParm) ? "out" : "in",
            arrayDim = property.ArrayDim
        }).ToArray();

    private static long? GetSerialSize(IPackage package, int index) => package is Package legacy && index < legacy.ExportMap.Length ? legacy.ExportMap[index].SerialSize : null;
    private static object GetExportFlags(IPackage package, int index) => package is Package legacy && index < legacy.ExportMap.Length ? legacy.ExportMap[index].ObjectFlags : 0;

    private bool SourceChanged()
    {
        if (string.IsNullOrWhiteSpace(_sourceFingerprint) || _provider is null || _catalog is null) return false;
        try
        {
            var observedFingerprint = ComputeSourceFingerprint(_config, _provider, _catalog);
            if (!string.Equals(_sourceFingerprint, observedFingerprint, StringComparison.Ordinal))
            {
                _indexStore?.SetSourceFingerprint(observedFingerprint);
                _faulted = true;
                _mountState = "failed";
                _warnings.Add(new WarningInfo("SOURCE_CHANGED", "已挂载容器发生变化", null));
                return true;
            }
        }
        catch
        {
            _faulted = true;
            _mountState = "failed";
            _warnings.Add(new WarningInfo("SOURCE_CHANGED", "无法重新核对已挂载容器", null));
            return true;
        }
        return false;
    }

    private string ArchiveId(IAesVfsReader reader)
        => Path.GetRelativePath(_config.GameRoot, reader.Path).Replace('\\', '/') + ":" + reader.ReadOrder.ToString(CultureInfo.InvariantCulture);

    private string ArchiveId(string path)
        => Path.GetRelativePath(_config.GameRoot, path).Replace('\\', '/');
    private static string HashSecret(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? HashFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static bool NativeAvailable()
        => CUE4ParseNatives.IsInitialized && CUE4ParseNatives.IsFeatureAvailable("Oodle\0"u8) && OodleHelper.Instance is not null;
    private static bool IsSharingViolation(IOException exception)
        => OperatingSystem.IsWindows() && (exception.HResult & 0xFFFF) is 32 or 33;
    private static IEnumerable<string> ArchiveInputFingerprintLines(GameConfig config)
    {
        var root = Path.GetFullPath(Path.Combine(config.GameRoot, config.ArchiveDir.Replace('/', Path.DirectorySeparatorChar)));
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            DirectoryInfo info;
            string? directoryError = null;
            try { info = new DirectoryInfo(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { info = new DirectoryInfo(directory); directoryError = ex.GetType().Name; }
            if (directoryError is not null) { yield return "archive-error|" + directory + "|" + directoryError; continue; }
            if (!info.Exists) { yield return "archive-missing|" + directory; continue; }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { yield return "archive-reparse-dir|" + Path.GetRelativePath(config.GameRoot, directory).Replace('\\', '/'); continue; }
            IEnumerable<FileInfo> files = [];
            IEnumerable<DirectoryInfo> children = [];
            string? enumerationError = null;
            try
            {
                files = info.EnumerateFiles("*", SearchOption.TopDirectoryOnly).ToArray();
                children = info.EnumerateDirectories("*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                enumerationError = ex.GetType().Name;
            }
            if (enumerationError is not null)
            {
                yield return "archive-error|" + Path.GetRelativePath(config.GameRoot, directory).Replace('\\', '/') + "|" + enumerationError;
                continue;
            }
            foreach (var file in files.Where(file => file.Extension.Equals(".pak", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".utoc", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".ucas", StringComparison.OrdinalIgnoreCase)).OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
                yield return string.Join('|', "archive", Path.GetRelativePath(config.GameRoot, file.FullName).Replace('\\', '/'), file.Length, file.LastWriteTimeUtc.Ticks, (file.Attributes & FileAttributes.ReparsePoint) != 0);
            foreach (var child in children.OrderBy(child => child.FullName, StringComparer.OrdinalIgnoreCase)) pending.Push(child.FullName);
        }
    }

    private static string ComputeSourceFingerprint(GameConfig config, PakOnlyFileProvider provider, Catalog catalog)
    {
        var lines = new[]
        {
            string.Join('|', Path.GetFullPath(config.GameRoot), config.ArchiveDir, config.UeVersion, config.Language, config.NativeLibraries.OodlePath ?? "none", "parser-baseline:CUE4Parse-source-snapshot", "index-schema:2", "projection:1", HashFile(config.Usmap) ?? "none", HashSecret(config.AesKey), string.Join(',', config.AesKeys.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + HashSecret(pair.Value))))
        }.Concat(ArchiveInputFingerprintLines(config))
        .Concat(provider.DiscoveredArchives.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            var info = new FileInfo(path);
            return string.Join('|', "discovered", Path.GetRelativePath(config.GameRoot, path).Replace('\\', '/'), info.Exists ? info.Length : -1, info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
        }))
        .Concat(provider.MountedVfs.OrderBy(reader => reader.Path, StringComparer.Ordinal).Select(reader => "mounted|" + Path.GetRelativePath(config.GameRoot, reader.Path).Replace('\\', '/') + "|" + reader.ReadOrder))
        .Concat(provider.UnloadedVfs.OrderBy(reader => reader.Path, StringComparer.Ordinal).Select(reader => "unmounted|" + Path.GetRelativePath(config.GameRoot, reader.Path).Replace('\\', '/') + "|" + reader.ReadOrder))
        .Concat(provider.UnsupportedArchives.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path => "unsupported|" + Path.GetRelativePath(config.GameRoot, path).Replace('\\', '/')))
        .Concat(provider.DiscoveryDiagnostics.OrderBy(value => value, StringComparer.Ordinal).Select(value => "discovery|" + value))
        .Concat(catalog.ConflictedPaths.OrderBy(value => value, StringComparer.Ordinal).Select(value => "conflict|" + value))
        .Concat(catalog.Files.OrderBy(file => file.ProviderPath, StringComparer.Ordinal).Select(file => file.ProviderPath + "|" + file.ReadOrder + "|" + file.Conflict));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
    }

    private static string MapExceptionCode(Exception ex) => ex switch
    {
        CursorException cursor => cursor.Code,
        BudgetException budget => budget.Code,
        OodleException => "DECOMPRESSION_FAILED",
        ScriptParseException => "SCRIPT_PARSE_FAILED",
        ExportDestinationException => "EXPORT_DESTINATION_EXISTS",
        PathNotAllowedException => "PATH_NOT_ALLOWED",
        PayloadVersionException => "PAYLOAD_VERSION_MISMATCH",
        SourceChangedException => "SOURCE_CHANGED",
        CUE4Parse.UE4.Exceptions.MappingException => "MAPPINGS_REQUIRED",
        ParserException => "PACKAGE_PARSE_FAILED",
        RegexMatchTimeoutException => "REGEX_TIMEOUT",
        ArgumentOutOfRangeException => "INVALID_ARGUMENT",
        ArgumentException => "INVALID_ARGUMENT",
        _ => "INTERNAL_ERROR"
    };

    public async ValueTask DisposeAsync()
    {
        _provider?.Dispose();
        _indexStore?.Dispose();
        _cacheLock?.Dispose();
        _parseGate.Dispose();
        await Task.CompletedTask;
    }

    private sealed record Paged<T>(IReadOnlyList<T> Items, PageInfo Page);
    private sealed class SchemaObservation
    {
        public HashSet<string> Types { get; } = new(StringComparer.Ordinal);
        public bool Nullable { get; set; }
    }
    private sealed record TaskView(string TaskId, string Kind, string State, string SessionId, bool Stale, string? Scope, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, string? Phase, string? CurrentSubject, int? TotalUnits, int ProcessedUnits, int SucceededUnits, int FailedUnits, int SkippedUnits, bool CancelRequested, bool CanInterruptCurrentUnit, string? SnapshotId, object? Result, int ErrorsVersion);
    private sealed record CursorData(int Version, string SessionId, string Operation, string FilterHash, int Offset, string? CatalogRevision, string? SnapshotId, string? LastKey = null);
}

internal sealed class BudgetWriteStream(Stream inner, long maximumBytes) : Stream
{
    private long _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureBudget(count);
        inner.Write(buffer, offset, count);
        _written += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureBudget(buffer.Length);
        inner.Write(buffer);
        _written += buffer.Length;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureBudget(buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _written += buffer.Length;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void EnsureBudget(int count)
    {
        if (count < 0 || checked(_written + count) > maximumBytes)
            throw new BudgetException("EXPORT_BUDGET_EXCEEDED");
    }
}

public sealed record ProjectionResult(bool Success, JToken? Token, IReadOnlyList<object> Omissions, string? Code, string? Message, string? SuggestedAction)
{
    public static ProjectionResult Succeeded(JToken token, IReadOnlyList<object> omissions) => new(true, token, omissions, null, null, null);
    public static ProjectionResult Failed(string code, string message, string suggestedAction) => new(false, null, [], code, message, suggestedAction);
}

public sealed class PakOnlyFileProvider : AbstractVfsFileProvider
{
    private readonly string _archiveDirectory;
    public List<string> DiscoveredArchives { get; } = [];
    public List<string> UnsupportedArchives { get; } = [];
    public List<string> DiscoveryDiagnostics { get; } = [];

    public PakOnlyFileProvider(string archiveDirectory, VersionContainer versions, StringComparer comparer) : base(versions, comparer)
        => _archiveDirectory = archiveDirectory;

    public override void Initialize()
    {
        if (!Directory.Exists(_archiveDirectory)) throw new DirectoryNotFoundException(_archiveDirectory);
        var pending = new Stack<string>([_archiveDirectory]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                DiscoveryDiagnostics.Add($"{directory}:REPARSE_POINT");
                continue;
            }
            try
            {
                foreach (var file in info.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    if (file.Extension.Equals(".pak", StringComparison.OrdinalIgnoreCase))
                    {
                        DiscoveredArchives.Add(file.FullName);
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            DiscoveryDiagnostics.Add($"{file.FullName}:REPARSE_POINT");
                            continue;
                        }
                        try { RegisterVfs(file); }
                        catch (Exception ex)
                        {
                            DiscoveryDiagnostics.Add($"{file.FullName}:{ex.GetType().Name}");
                        }
                    }
                    else if (file.Extension.Equals(".utoc", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".ucas", StringComparison.OrdinalIgnoreCase))
                    {
                        UnsupportedArchives.Add(file.FullName);
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                            DiscoveryDiagnostics.Add($"{file.FullName}:REPARSE_POINT");
                    }
                }
                foreach (var child in info.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        DiscoveryDiagnostics.Add($"{child.FullName}:REPARSE_POINT");
                        continue;
                    }
                    pending.Push(child.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiscoveryDiagnostics.Add($"{directory}:{ex.GetType().Name}");
            }
        }
    }
}

public sealed class Catalog
{
    private Catalog(List<CatalogFile> files, int physicalEntryCount, List<string> conflictedPaths, bool complete)
    {
        Files = files;
        PhysicalEntryCount = physicalEntryCount;
        ConflictedPaths = conflictedPaths;
        CatalogComplete = complete;
    }

    public IReadOnlyList<CatalogFile> Files { get; }
    public int PhysicalEntryCount { get; }
    public IReadOnlyList<string> ConflictedPaths { get; }
    public bool CatalogComplete { get; }

    public static Catalog Build(PakOnlyFileProvider provider, string? gameRoot = null)
    {
        var groups = provider.Files.GroupBy(pair => Normalize(pair.Key), StringComparer.OrdinalIgnoreCase);
        var files = new List<CatalogFile>();
        var conflicts = new List<string>();
        var physical = 0;
        foreach (var group in groups)
        {
            var entries = group.Select(pair => CreateFile(pair.Value)).ToList();
            physical += entries.Count;
            var max = entries.Max(entry => entry.ReadOrder);
            var winners = entries.Where(entry => entry.ReadOrder == max).ToList();
            var conflict = winners.Count > 1;
            if (conflict) conflicts.Add(group.Key);
            var winner = conflict ? winners[0] with { Conflict = true } : winners[0];
            files.Add(winner with { ShadowedCount = Math.Max(0, entries.Count - winners.Count) });
        }
        var complete = conflicts.Count == 0 && provider.UnloadedVfs.Count == 0 && provider.UnsupportedArchives.Count == 0 && provider.DiscoveryDiagnostics.Count == 0;
        return new Catalog(files.OrderBy(file => file.DisplayPath, StringComparer.Ordinal).ToList(), physical, conflicts, complete);

        CatalogFile CreateFile(GameFile file)
        {
            var providerPath = Normalize(file.Path);
            var display = ToDisplay(providerPath);
            var entry = file as VfsEntry;
            var archiveId = entry?.Vfs is { } reader
                ? (gameRoot is null ? reader.Name : Path.GetRelativePath(gameRoot, reader.Path).Replace('\\', '/') + ":" + reader.ReadOrder.ToString(CultureInfo.InvariantCulture))
                : "loose";
            return new CatalogFile(display, providerPath, PackageKey(display), NormalizeExtension(file.Extension), file.Size, archiveId, entry?.Vfs.ReadOrder ?? 0, file.IsUePackage, false, 0, entry is null ? null : entry.Vfs, file);
        }

        static string ToDisplay(string path)
        {
            var marker = "/Content/";
            var content = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (content >= 0) return "/Game/" + path[(content + marker.Length)..];
            return "/" + path.TrimStart('/');
        }
    }

    public CatalogFile? FindFile(string path)
    {
        var normalized = Normalize(path);
        var display = "/" + normalized;
        return Files.FirstOrDefault(file => file.DisplayPath.Equals(display, StringComparison.OrdinalIgnoreCase) || file.ProviderPath.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public CatalogFile? FindPackage(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith('/')) normalized = "/" + normalized;
        return Files.FirstOrDefault(file => file.IsPackage &&
            (PackageDisplayKey(file.DisplayPath).Equals(normalized.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || file.DisplayPath.Equals(normalized, StringComparison.OrdinalIgnoreCase) || file.ProviderPath.Equals(path, StringComparison.OrdinalIgnoreCase) || file.ProviderPath.Equals(normalized.TrimStart('/'), StringComparison.OrdinalIgnoreCase)));

        static string PackageDisplayKey(string value) => value.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) ? value[..value.LastIndexOf('.')].Replace("/Content/", "/", StringComparison.OrdinalIgnoreCase) : value;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
    private static string NormalizeExtension(string extension) => extension.TrimStart('.').ToLowerInvariant();
    private static string PackageKey(string display) => display.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || display.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) ? display[..display.LastIndexOf('.')].ToLowerInvariant() : display.ToLowerInvariant();
}

public sealed record CatalogFile(
    string DisplayPath,
    string ProviderPath,
    string PackageKey,
    string Extension,
    long Size,
    string ArchiveId,
    long ReadOrder,
    bool IsPackage,
    bool Conflict,
    int ShadowedCount,
    IVfsReader? Reader,
    GameFile? GameFile)
{
    public IReadOnlyList<string> AssetTypes { get; set; } = [];
}

public sealed class TaskRegistry
{
    private readonly ConcurrentDictionary<string, TaskInfo> _tasks = new(StringComparer.Ordinal);
    private IndexStore? _store;
    public string? ActiveTaskId => _tasks.Values.FirstOrDefault(task => task.State is "queued" or "running" or "cancelling")?.TaskId;

    public void Attach(IndexStore store)
    {
        _store = store;
        foreach (var task in store.LoadTasksAndMarkOrphans()) _tasks[task.TaskId] = task;
    }

    public bool TryStart(string kind, string sessionId, string scope, string? sourceFingerprint, out TaskInfo task)
    {
        if (ActiveTaskId is not null) { task = null!; return false; }
        task = new TaskInfo { TaskId = Guid.NewGuid().ToString("N"), Kind = kind, State = "queued", SessionId = sessionId, Scope = scope, SourceFingerprint = sourceFingerprint };
        _tasks[task.TaskId] = task;
        try
        {
            Persist(task);
            return true;
        }
        catch
        {
            _tasks.TryRemove(task.TaskId, out _);
            task.Cancellation.Dispose();
            throw;
        }
    }

    public bool TryGet(string id, out TaskInfo task) => _tasks.TryGetValue(id, out task!);

    public IReadOnlyList<TaskInfo> Snapshot() => _tasks.Values.ToArray();

    public bool TryCancel(string id, out string previous, out string current, out bool canInterrupt)
    {
        if (!_tasks.TryGetValue(id, out var task)) { previous = current = string.Empty; canInterrupt = false; return false; }
        lock (task)
        {
            previous = task.State;
            var previousCancelRequested = task.CancelRequested;
            if (task.State == "queued") task.State = "cancelled";
            else if (task.State is "running") task.State = "cancelling";
            current = task.State;
            task.CancelRequested = current is "cancelling" or "cancelled";
            canInterrupt = task.CanInterruptCurrentUnit;
            var finishedAt = task.FinishedAtUtc;
            if (task.State == "cancelled") task.FinishedAtUtc = DateTimeOffset.UtcNow;
            try
            {
                Persist(task);
            }
            catch
            {
                task.State = previous;
                task.CancelRequested = previousCancelRequested;
                task.FinishedAtUtc = finishedAt;
                throw;
            }
            if (task.CancelRequested) task.Cancellation.Cancel();
            return true;
        }
    }

    public void AddError(TaskInfo task, string subject, string stage, string code, string message)
    {
        lock (task)
        {
            AddErrorWithoutPersist(task, subject, stage, code, message);
            Persist(task);
        }
    }

    public void AddErrorWithoutPersist(TaskInfo task, string subject, string stage, string code, string message)
    {
        task.Errors.Add(new TaskErrorInfo(task.Errors.Count == 0 ? 0 : task.Errors.Max(error => error.Sequence) + 1, subject, stage, code, message));
        task.ErrorsVersion++;
    }

    public void Save(TaskInfo task)
    {
        lock (task) Persist(task);
    }

    private void Persist(TaskInfo task) => _store?.SaveTask(task);
}

public sealed record ListArgs(int? Limit = null, string? Cursor = null, string? Dir = null);
public sealed record ScopeArgs(string? Scope = null);
public sealed record AssetIndexArgs(string? Scope = null, bool IncludeSymbols = true);
public sealed record SearchFilesArgs(string Pattern = "", string Mode = "glob", string? Extension = null, string? AssetType = null, string? Scope = null, int? Limit = null, string? Cursor = null);
public sealed record DirectoryArgs(string Path = "/Game", int? Limit = null, string? Cursor = null);
public sealed record ObjectListArgs(string Path = "", string? Type = null, int? Limit = null, string? Cursor = null);
public sealed record ObjectArgs(string Path = "", int? ExportIndex = null, int Depth = 4, string[]? Fields = null);
public sealed record DataTableArgs(string Path = "", int? ExportIndex = null, string Mode = "rows", string? RowName = null, FilterInput[]? Where = null, string[]? Fields = null, int? Limit = null, string? Cursor = null);
public sealed record ClassInfoArgs(string Path = "", int? ExportIndex = null, string Section = "summary", bool IncludeInherited = false, int? Limit = null, string? Cursor = null);
public sealed record FunctionArgs(string Path = "", int? ExportIndex = null, string Section = "summary", int? Limit = null, string? Cursor = null);
public sealed record StringTableArgs(string Path = "", int? ExportIndex = null, string? Key = null, string? Namespace = null, int? Limit = null, string? Cursor = null);
public sealed record CurveArgs(string Path = "", int? ExportIndex = null, string? CurveName = null, string Mode = "keys", int? Limit = null, string? Cursor = null);
public sealed record SearchSymbolsArgs(string Pattern = "", string Kind = "any", string? Scope = null, int? Limit = null, string? Cursor = null);
public sealed record SearchTextArgs(string Keyword = "", string? Scope = null, string? SourceKind = null, int? Limit = null, string? Cursor = null);
public sealed record ReferenceArgs(string Path = "", string Direction = "outgoing", int? Limit = null, string? Cursor = null);
public sealed record ExportArgs(string Path = "", int? ExportIndex = null, string OutDir = "");
public sealed record TaskArgs(string TaskId = "", bool IncludeErrors = false, int? Limit = null, string? Cursor = null);
public sealed record FilterInput(string Field = "", string Op = "eq", JsonElement? Value = null);
public sealed record ResolvedPackage(bool Success, CatalogFile? File, string? DisplayPackage, string? PackageKey, ToolEnvelope? Error)
{
    public static ResolvedPackage Succeeded(CatalogFile file, string display, string key) => new(true, file, display, key, null);
    public static ResolvedPackage Failed(ToolEnvelope error) => new(false, null, null, null, error);
}
public sealed record ResolvedObject(bool Success, CatalogFile? File, IPackage? Package, int ExportIndex, string? ObjectPath, string? DisplayPackage, ToolEnvelope? Error)
{
    public static ResolvedObject Succeeded(CatalogFile file, IPackage package, int index, string objectPath, string display) => new(true, file, package, index, objectPath, display, null);
    public static ResolvedObject Failed(ToolEnvelope error) => new(false, null, null, -1, null, null, error);
}
public sealed class BudgetException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed class ExportDestinationException() : Exception("export destination already exists");
public sealed class ScriptParseException() : Exception("script bytecode is incomplete");
public sealed class PathNotAllowedException(string path) : Exception(path) { public string Path { get; } = path; }
public sealed class PayloadVersionException(string path) : Exception(path) { public string Path { get; } = path; }
public sealed class SourceChangedException() : Exception("source changed during operation");
public sealed class CursorException(string code) : Exception(code) { public string Code { get; } = code; }
public sealed class FieldSelectionException(string message) : Exception(message);
