using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using JsonSerializer = System.Text.Json.JsonSerializer;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Internationalization;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Pak;
using FModelMcp.Contracts;
using FModelMcp.Worker;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModelMcp.Indexing;

public sealed class IndexStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, Head> _heads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<object>> _symbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string PackageKey, string Type)>> _assetTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _unknownAssetExports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _unknownAssetPackages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _symbolSnapshots = new(StringComparer.Ordinal);
    private readonly List<TextRow> _texts = [];
    private readonly List<ReferenceRow> _references = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private bool _ftsAvailable;
    private string? _currentFingerprint;

    public bool FtsAvailable => _ftsAvailable;
    public bool LastAssetMetadataComplete { get; private set; } = true;

    public IndexStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SQLitePCL.Batteries_V2.Init();
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString());
        _connection.Open();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
        }
        var databaseVersion = 0;
        using (var versionCommand = _connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            databaseVersion = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (databaseVersion != 0 && databaseVersion != 1)
                throw new InvalidDataException($"不支持的索引数据库版本: {databaseVersion}");
        }
        EnsureSchema();
        if (databaseVersion == 0)
        {
            using var setVersion = _connection.CreateCommand();
            setVersion.CommandText = "PRAGMA user_version=1;";
            setVersion.ExecuteNonQuery();
        }
        CleanupOrphanSnapshots();
        LoadPublishedData();
    }

    public IReadOnlyDictionary<string, object> GetHeads()
    {
        lock (_gate)
        {
            return _heads.ToDictionary(pair => pair.Key, pair => (object)new
            {
                kind = pair.Key,
                snapshotId = pair.Value.SnapshotId,
                scope = pair.Value.Scope,
                state = _currentFingerprint is null || string.Equals(pair.Value.SourceFingerprint, _currentFingerprint, StringComparison.Ordinal) ? "published" : "stale",
                coverage = new
                {
                    totalUnits = pair.Value.TotalUnits,
                    succeededUnits = pair.Value.SucceededUnits,
                    failedUnits = pair.Value.FailedUnits,
                    complete = pair.Value.Complete,
                    catalogComplete = pair.Value.CatalogComplete,
                    sourceFingerprint = pair.Value.SourceFingerprint
                },
                capabilityCoverage = pair.Key == "asset" ? new { symbols = _symbolSnapshots.Contains(pair.Value.SnapshotId) } : null,
                builtAtUtc = pair.Value.BuiltAtUtc
            }, StringComparer.Ordinal);
        }
    }

    public void SetSourceFingerprint(string fingerprint)
    {
        lock (_gate) _currentFingerprint = fingerprint;
    }

    public IReadOnlyList<TaskInfo> LoadTasksAndMarkOrphans()
    {
        lock (_gate)
        {
            var result = new List<TaskInfo>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT id, session_id, kind, state, snapshot_id, request_json, progress_json, result_json, started_utc, finished_utc FROM tasks ORDER BY started_utc, id";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var progress = JsonSerializer.Deserialize<StoredTaskProgress>(reader.GetString(6), JsonDefaults.Options) ?? new StoredTaskProgress();
                    var state = reader.GetString(3);
                    var orphaned = state is "queued" or "running" or "cancelling";
                    var task = new TaskInfo
                    {
                        TaskId = reader.GetString(0),
                        Kind = reader.GetString(2),
                        State = orphaned ? "interrupted" : state,
                        SessionId = reader.GetString(1),
                        Stale = progress.Stale || orphaned,
                        Scope = progress.Scope,
                        StartedAtUtc = ParseDate(reader.GetString(8)),
                        FinishedAtUtc = reader.IsDBNull(9) ? (orphaned ? DateTimeOffset.UtcNow : null) : ParseDate(reader.GetString(9)),
                        Phase = orphaned ? null : progress.Phase,
                        CurrentSubject = orphaned ? null : progress.CurrentSubject,
                        TotalUnits = progress.TotalUnits,
                        ProcessedUnits = progress.ProcessedUnits,
                        SucceededUnits = progress.SucceededUnits,
                        FailedUnits = progress.FailedUnits,
                        SkippedUnits = progress.SkippedUnits,
                        CancelRequested = progress.CancelRequested,
                        CanInterruptCurrentUnit = orphaned || progress.CanInterruptCurrentUnit,
                        ErrorsVersion = progress.ErrorsVersion,
                        SnapshotId = reader.IsDBNull(4) ? null : reader.GetString(4),
                        SourceFingerprint = progress.SourceFingerprint,
                        Result = DeserializeStoredJson(reader.IsDBNull(7) ? null : reader.GetString(7))
                    };
                    result.Add(task);
                }
            }
            using (var errors = _connection.CreateCommand())
            {
                errors.CommandText = "SELECT task_id, sequence, subject, stage, code, message FROM task_errors ORDER BY task_id, sequence";
                using var reader = errors.ExecuteReader();
                var byTask = result.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
                while (reader.Read())
                    if (byTask.TryGetValue(reader.GetString(0), out var task))
                        task.Errors.Add(new TaskErrorInfo(reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            }
            var orphanedTasks = result.Where(task => task.State == "interrupted" && task.Stale).ToArray();
            if (orphanedTasks.Length > 0)
            {
                _databaseGate.Wait();
                try
                {
                    using var transaction = _connection.BeginTransaction();
                    foreach (var task in orphanedTasks)
                    {
                        using var update = _connection.CreateCommand();
                        update.Transaction = transaction;
                        ConfigureTaskCommand(update, task);
                        update.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
                finally { _databaseGate.Release(); }
            }
            return result;
        }
    }

    public void SaveTask(TaskInfo task)
    {
        lock (_gate)
        {
            _databaseGate.Wait();
            try
            {
                using var transaction = _connection.BeginTransaction();
                SaveTaskInTransaction(task, transaction);
                transaction.Commit();
            }
            finally { _databaseGate.Release(); }
        }
    }

    private static void SaveTaskInTransaction(TaskInfo task, SqliteTransaction transaction)
    {
        using (var command = transaction.Connection!.CreateCommand())
        {
            command.Transaction = transaction;
            ConfigureTaskCommand(command, task);
            command.ExecuteNonQuery();
        }
        using (var delete = transaction.Connection!.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM task_errors WHERE task_id=$task";
            delete.Parameters.AddWithValue("$task", task.TaskId);
            delete.ExecuteNonQuery();
        }
        foreach (var error in task.Errors)
        {
            using var insert = transaction.Connection!.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO task_errors(task_id, sequence, subject, stage, code, message) VALUES ($task,$sequence,$subject,$stage,$code,$message)";
            insert.Parameters.AddWithValue("$task", task.TaskId);
            insert.Parameters.AddWithValue("$sequence", error.Sequence);
            insert.Parameters.AddWithValue("$subject", error.Subject);
            insert.Parameters.AddWithValue("$stage", error.Stage);
            insert.Parameters.AddWithValue("$code", error.Code);
            insert.Parameters.AddWithValue("$message", error.Message);
            insert.ExecuteNonQuery();
        }
    }

    private static void ConfigureTaskCommand(SqliteCommand command, TaskInfo task)
    {
        command.CommandText = "INSERT INTO tasks(id, session_id, kind, state, snapshot_id, request_json, progress_json, result_json, started_utc, finished_utc) VALUES ($id,$session,$kind,$state,$snapshot,$request,$progress,$result,$started,$finished) ON CONFLICT(id) DO UPDATE SET session_id=excluded.session_id, kind=excluded.kind, state=excluded.state, snapshot_id=excluded.snapshot_id, request_json=excluded.request_json, progress_json=excluded.progress_json, result_json=excluded.result_json, started_utc=excluded.started_utc, finished_utc=excluded.finished_utc";
        command.Parameters.AddWithValue("$id", task.TaskId);
        command.Parameters.AddWithValue("$session", task.SessionId);
        command.Parameters.AddWithValue("$kind", task.Kind);
        command.Parameters.AddWithValue("$state", task.State);
        AddNullable(command, "$snapshot", task.SnapshotId);
        command.Parameters.AddWithValue("$request", JsonSerializer.Serialize(new { scope = task.Scope }, JsonDefaults.Options));
        command.Parameters.AddWithValue("$progress", JsonSerializer.Serialize(new StoredTaskProgress(task), JsonDefaults.Options));
        AddNullable(command, "$result", task.Result is null ? null : JsonSerializer.Serialize(task.Result, task.Result.GetType(), JsonDefaults.Options));
        command.Parameters.AddWithValue("$started", task.StartedAtUtc.ToString("O"));
        AddNullable(command, "$finished", task.FinishedAtUtc?.ToString("O"));
    }

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private static object? DeserializeStoredJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<TaskErrorInfo> DeserializeStoredErrors(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : JsonSerializer.Deserialize<List<TaskErrorInfo>>(value, JsonDefaults.Options) ?? [];

    public bool HasPublished(string kind, string scope)
    {
        lock (_gate) return _heads.TryGetValue(kind, out var head) && IsCurrent(head) && InScope(scope, head.Scope);
    }

    public bool HasAnyPublished(string kind)
    {
        lock (_gate) return _heads.TryGetValue(kind, out var head) && IsCurrent(head);
    }

    public bool HasSymbolCoverage(string scope)
    {
        lock (_gate) return _heads.TryGetValue("asset", out var head) && IsCurrent(head) && _symbolSnapshots.Contains(head.SnapshotId) && InScope(scope, head.Scope);
    }

    public CoverageInfo? GetCoverage(string kind, string scope)
    {
        lock (_gate)
        {
            if (!_heads.TryGetValue(kind, out var head) || !IsCurrent(head) || !InScope(scope, head.Scope)) return null;
            return ToCoverage(head, scope);
        }
    }

    public CoverageInfo? GetHeadCoverage(string kind)
    {
        lock (_gate) return _heads.TryGetValue(kind, out var head) && IsCurrent(head) ? ToCoverage(head, head.Scope) : null;
    }

    private bool IsCurrent(Head head)
        => _currentFingerprint is null || string.Equals(head.SourceFingerprint, _currentFingerprint, StringComparison.Ordinal);

    private CoverageInfo ToCoverage(Head head, string scope)
        => new() { SnapshotId = head.SnapshotId, Scope = scope, TotalUnits = head.TotalUnits, SucceededUnits = head.SucceededUnits, FailedUnits = head.FailedUnits, SkippedUnits = 0, Complete = head.Complete, CatalogComplete = head.CatalogComplete, SourceFingerprint = head.SourceFingerprint, CapabilityCoverage = head.Kind == "asset" ? new { symbols = _symbolSnapshots.Contains(head.SnapshotId) } : null };

    public async Task<bool> IndexAssetAsync(string snapshotId, CatalogFile file, IFileProvider provider, bool includeSymbols, CancellationToken cancellationToken, TaskInfo? task = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var package = provider.LoadPackage(file.ProviderPath);
        var types = new List<string>();
        var assetRows = new List<AssetRow>();
        var symbols = new List<object>();
        var symbolsComplete = true;
        var unknownExportCount = 0;
        var unknownPackages = new List<string>();
        for (var i = 0; i < package.ExportsLazy.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CUE4Parse.UE4.Assets.ResolvedObject? metadata = null;
            try { metadata = package.ResolvePackageIndex(new FPackageIndex(package, i + 1)); }
            catch { }
            string? className = null;
            try { className = metadata?.Class?.Name.Text; }
            catch { }
            var objectPath = $"{PackageDisplayPath(file.DisplayPath)}:export_{i}";
            var objectName = $"export_{i}";
            string? classPath = null;
            string? outerPath = null;
            try
            {
                if (metadata is not null)
                {
                    objectPath = ObjectDisplayPath(file.DisplayPath, metadata);
                    objectName = metadata.Name.Text;
                    classPath = metadata.Class is { } type ? ObjectDisplayPath(file.DisplayPath, type) : null;
                    outerPath = metadata.Outer is { } outer ? ObjectDisplayPath(file.DisplayPath, outer) : null;
                }
            }
            catch
            {
                metadata = null;
                className = null;
            }
            var flags = package is Package pak && i < pak.ExportMap.Length
                ? pak.ExportMap[i].ObjectFlags.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            var metadataState = string.IsNullOrWhiteSpace(className) ? "unresolved" : "resolved";
            assetRows.Add(new AssetRow(file.PackageKey, i, objectPath, objectName, className, classPath, outerPath, file.ArchiveId, flags, metadataState));
            if (metadataState == "resolved")
            {
                types.Add(className!);
                if (includeSymbols)
                {
                    UObject? obj;
                    try { obj = package.GetExport(i); }
                    catch { symbolsComplete = false; continue; }
                    if (obj is null)
                    {
                        symbolsComplete = false;
                        continue;
                    }
                    try
                    {
                        if (obj is UClass cls)
                        {
                            var classObjectPath = objectPath;
                            var superPath = cls.SuperStruct?.ResolvedObject is { } super ? ObjectDisplayPath(file.DisplayPath, super) : null;
                            symbols.Add(new { symbolId = StableSymbolId(snapshotId, file.ProviderPath, i, "class", classObjectPath), kind = "class", name = obj.Name, ownerObjectPath = classObjectPath, objectPath = classObjectPath, propertyPointer = (string?)null, declaredType = "UClass", flags = cls.ClassFlags.ToString(), declaringClassPath = superPath, availability = "available" });
                            foreach (var child in cls.ChildProperties?.OfType<FProperty>() ?? [])
                                symbols.Add(new { symbolId = StableSymbolId(snapshotId, file.ProviderPath, i, "property", child.Name.Text), kind = "property", name = child.Name.Text, ownerObjectPath = classObjectPath, objectPath = (string?)null, propertyPointer = "/" + child.Name.Text, declaredType = child.GetType().Name[1..], flags = child.PropertyFlags.ToString(), declaringClassPath = classObjectPath, availability = "available" });
                            foreach (var function in cls.FuncMap ?? [])
                            {
                                var functionPath = function.Value.ResolvedObject is { } functionObject ? ObjectDisplayPath(file.DisplayPath, functionObject) : null;
                                symbols.Add(new { symbolId = StableSymbolId(snapshotId, file.ProviderPath, i, "function", function.Key.Text), kind = "function", name = function.Key.Text, ownerObjectPath = classObjectPath, objectPath = functionPath, propertyPointer = (string?)null, declaredType = (string?)null, flags = (string?)null, declaringClassPath = classObjectPath, availability = "available" });
                            }
                        }
                        else if (obj is UFunction function)
                        {
                            var functionObjectPath = objectPath;
                            var declaringClassPath = obj.Super is { } super ? ObjectDisplayPath(file.DisplayPath, super) : null;
                            symbols.Add(new { symbolId = StableSymbolId(snapshotId, file.ProviderPath, i, "function", functionObjectPath), kind = "function", name = obj.Name, ownerObjectPath = functionObjectPath, objectPath = functionObjectPath, propertyPointer = (string?)null, declaredType = "UFunction", flags = function.FunctionFlags.ToString(), declaringClassPath, availability = "available" });
                        }
                    }
                    catch { symbolsComplete = false; }
                }
            }
            else
            {
                unknownExportCount++;
                unknownPackages.Add(file.PackageKey);
            }
        }
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (var transaction = _connection.BeginTransaction())
            {
                try
                {
                    await InsertAssetRowsAsync(snapshotId, assetRows, cancellationToken, transaction).ConfigureAwait(false);
                    await InsertSymbolsAsync(snapshotId, file.PackageKey, symbols, cancellationToken, transaction).ConfigureAwait(false);
                    await InsertScanUnitAsync(snapshotId, file.DisplayPath, "metadata", "succeeded", null, null, cancellationToken, transaction).ConfigureAwait(false);
                    if (includeSymbols)
                        await InsertScanUnitAsync(snapshotId, file.DisplayPath, "symbols", symbolsComplete ? "succeeded" : "failed", symbolsComplete ? null : "PACKAGE_PARSE_FAILED", symbolsComplete ? null : "符号提取不完整", cancellationToken, transaction).ConfigureAwait(false);
                    if (task is not null) SaveTaskInTransaction(task, transaction);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }
        finally { _databaseGate.Release(); }
        lock (_gate)
        {
            if (!_symbols.TryGetValue(snapshotId, out var snapshotSymbols))
                _symbols[snapshotId] = snapshotSymbols = [];
            snapshotSymbols.AddRange(symbols);
            if (!_assetTypes.TryGetValue(snapshotId, out var snapshotTypes))
                _assetTypes[snapshotId] = snapshotTypes = [];
            foreach (var type in types) snapshotTypes.Add((file.PackageKey, type));
            _unknownAssetExports[snapshotId] = _unknownAssetExports.GetValueOrDefault(snapshotId) + unknownExportCount;
            if (unknownPackages.Count > 0)
            {
                if (!_unknownAssetPackages.TryGetValue(snapshotId, out var snapshotUnknownPackages))
                    _unknownAssetPackages[snapshotId] = snapshotUnknownPackages = [];
                snapshotUnknownPackages.AddRange(unknownPackages);
            }
            LastAssetMetadataComplete = unknownExportCount == 0;
        }
        return symbolsComplete;
    }

    public async Task IndexTextAsync(string snapshotId, CatalogFile file, IFileProvider provider, string language, CancellationToken cancellationToken, TaskInfo? task = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var records = new List<TextRow>();
        if (file.DisplayPath.EndsWith(".locres", StringComparison.OrdinalIgnoreCase) && file.GameFile is not null && IsRequestedLocalization(file.DisplayPath, language))
        {
            using var reader = file.GameFile.CreateReader();
            var resource = new CUE4Parse.UE4.Localization.FTextLocalizationResource(reader);
            foreach (var namespaceEntry in resource.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in namespaceEntry.Value)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    records.Add(new TextRow(snapshotId, file.DisplayPath, file.DisplayPath, "locres", null, null, null, namespaceEntry.Key.Str, entry.Key.Str, null, entry.Value.LocalizedString, null, language, "resolvedLocalized"));
                }
            }
        }
        else if (file.IsPackage)
        {
            var package = provider.LoadPackage(file.ProviderPath);
            for (var i = 0; i < package.ExportsLazy.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var export = package.GetExport(i);
                CUE4Parse.UE4.Assets.ResolvedObject? exportMetadata = null;
                try { exportMetadata = package.ResolvePackageIndex(new FPackageIndex(package, i + 1)); } catch { }
                var objectPath = exportMetadata is not null ? ObjectDisplayPath(file.DisplayPath, exportMetadata) : null;
                if (export is UStringTable table)
                {
                    foreach (var pair in table.StringTable.KeysToEntries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var localized = provider.Internationalization.TryGetValue(table.StringTable.TableNamespace, out var localizedEntries) && localizedEntries.TryGetValue(pair.Key, out var localizedValue)
                            ? localizedValue
                            : null;
                        records.Add(new TextRow(snapshotId, file.DisplayPath, file.DisplayPath, "stringTable", objectPath, null, null, table.StringTable.TableNamespace, pair.Key, objectPath, localized ?? pair.Value, pair.Value, language, localized is null ? "sourceOnly" : "resolvedLocalized"));
                    }
                }
                else if (export is UDataTable tableData)
                {
                    foreach (var row in tableData.RowMap ?? [])
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var rowJson = JToken.Parse(JsonConvert.SerializeObject(row.Value, Formatting.None));
                        foreach (var leaf in StringLeaves(rowJson, ""))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            records.Add(new TextRow(snapshotId, file.DisplayPath, file.DisplayPath, "datatable", objectPath, row.Key.Text, leaf.Pointer, leaf.Namespace, leaf.Key, leaf.TableId, leaf.Text, leaf.SourceText, language, leaf.Resolution));
                        }
                    }
                }
            }
        }
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (var transaction = _connection.BeginTransaction())
            {
                try
                {
                    foreach (var record in records) await InsertTextRowAsync(record, cancellationToken, transaction).ConfigureAwait(false);
                    if (records.Any(record => record.SourceKind == "locres"))
                    {
                        await using var ambiguity = _connection.CreateCommand();
                        ambiguity.Transaction = transaction;
                        ambiguity.CommandText = "UPDATE texts SET resolution='ambiguous' WHERE snapshot_id=$snapshot AND source_kind='locres' AND EXISTS (SELECT 1 FROM texts other WHERE other.snapshot_id=texts.snapshot_id AND other.source_kind='locres' AND other.namespace=texts.namespace AND other.text_key=texts.text_key AND other.text<>texts.text)";
                        ambiguity.Parameters.AddWithValue("$snapshot", snapshotId);
                        await ambiguity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    await InsertScanUnitAsync(snapshotId, file.DisplayPath, "text", "succeeded", null, null, cancellationToken, transaction).ConfigureAwait(false);
                    if (task is not null) SaveTaskInTransaction(task, transaction);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }
        finally { _databaseGate.Release(); }
        lock (_gate)
        {
            var ambiguousKeys = _texts.Where(row => row.SnapshotId == snapshotId && row.SourceKind == "locres")
                .Concat(records.Where(row => row.SourceKind == "locres"))
                .GroupBy(row => (row.Namespace, row.Key))
                .Where(group => group.Select(row => row.Text).Distinct(StringComparer.Ordinal).Count() > 1)
                .Select(group => group.Key)
                .ToHashSet();
            for (var index = 0; index < _texts.Count; index++)
                if (_texts[index].SnapshotId == snapshotId && _texts[index].SourceKind == "locres" && ambiguousKeys.Contains((_texts[index].Namespace, _texts[index].Key)))
                    _texts[index] = _texts[index] with { Resolution = "ambiguous" };
            _texts.AddRange(records.Select(record => ambiguousKeys.Contains((record.Namespace, record.Key)) ? record with { Resolution = "ambiguous" } : record));
        }
    }

    public async Task IndexReferenceAsync(string snapshotId, CatalogFile file, IFileProvider provider, CancellationToken cancellationToken, TaskInfo? task = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var package = provider.LoadPackage(file.ProviderPath);
        if (package is not Package legacy)
        {
            await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var transaction = _connection.BeginTransaction();
                try
                {
                    await InsertScanUnitAsync(snapshotId, file.DisplayPath, "references", "succeeded", null, null, cancellationToken, transaction).ConfigureAwait(false);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
            finally { _databaseGate.Release(); }
            return;
        }
        var rows = new List<ReferenceRow>();
        var sourcePackage = NormalizePackageName(package.Name);
        for (var index = 0; index < legacy.ImportMap.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var import = legacy.ImportMap[index];
            CUE4Parse.UE4.Assets.ResolvedObject? resolved = null;
            try { resolved = package.ResolvePackageIndex(new FPackageIndex(package, -index - 1)); }
            catch { }
            var targetPackage = resolved?.Package?.Name is { Length: > 0 } name ? NormalizePackageName(name) : NormalizePackageName(import.PackageName.Text);
            if (string.IsNullOrEmpty(targetPackage))
            {
                try { targetPackage = NormalizePackageName(import.OuterIndex?.ResolvedObject?.Package.Name ?? string.Empty); }
                catch { targetPackage = string.Empty; }
            }
            if (string.Equals(targetPackage, sourcePackage, StringComparison.OrdinalIgnoreCase)) continue;
            var targetRawName = resolved?.GetPathName() ?? import.ObjectName.Text;
            rows.Add(new ReferenceRow(snapshotId, sourcePackage, targetPackage, targetRawName, "packageImport", resolved is null ? "unresolved" : "resolved", 1, index));
        }
        rows = rows.GroupBy(row => (row.SourcePackage, row.TargetPackage, UnknownKey: string.IsNullOrEmpty(row.TargetPackage) ? row.TargetRawName : string.Empty, row.EdgeKind), ReferenceTupleComparer.Instance)
            .Select(group => group.OrderBy(row => row.ExampleImportIndex).First() with
            {
                EvidenceCount = group.Sum(row => row.EvidenceCount),
                Resolution = group.All(row => row.Resolution == "resolved") ? "resolved" : "unresolved"
            }).ToList();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (var transaction = _connection.BeginTransaction())
            {
                try
                {
                    foreach (var row in rows) await InsertReferenceRowAsync(row, cancellationToken, transaction).ConfigureAwait(false);
                    await InsertScanUnitAsync(snapshotId, file.DisplayPath, "references", "succeeded", null, null, cancellationToken, transaction).ConfigureAwait(false);
                    if (task is not null) SaveTaskInTransaction(task, transaction);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }
        finally { _databaseGate.Release(); }
        lock (_gate) _references.AddRange(rows);
    }

    public IReadOnlyList<object> SearchSymbols(string scope, Regex regex, string kind)
    {
        lock (_gate)
        {
            if (!_heads.TryGetValue("asset", out var head) || !IsCurrent(head) || !_symbols.TryGetValue(head.SnapshotId, out var symbols)) return [];
            return symbols.Where(item =>
            {
                var name = SymbolValue(item, "name");
                var itemKind = SymbolValue(item, "kind");
                var owner = SymbolValue(item, "ownerObjectPath") ?? SymbolValue(item, "objectPath") ?? string.Empty;
                return InScope(owner, scope) && regex.IsMatch(name ?? string.Empty) && (kind.Equals("any", StringComparison.OrdinalIgnoreCase) || kind.Equals(itemKind, StringComparison.OrdinalIgnoreCase));
            }).OrderBy(item => SymbolValue(item, "name"), StringComparer.Ordinal)
              .ThenBy(item => SymbolValue(item, "ownerObjectPath"), StringComparer.Ordinal)
              .ToArray();
        }
    }

    private static string? SymbolValue(object item, string property)
    {
        if (item is JsonElement element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return item.GetType().GetProperty(property)?.GetValue(item)?.ToString();
    }

    public int GetUnknownAssetExportCount(string scope)
    {
        lock (_gate)
        {
            if (!_heads.TryGetValue("asset", out var head) || !IsCurrent(head) || !InScope(scope, head.Scope)) return 0;
            if (_unknownAssetPackages.TryGetValue(head.SnapshotId, out var packages)) return packages.Count(package => InScope(package, scope));
            return _unknownAssetExports.GetValueOrDefault(head.SnapshotId);
        }
    }

    public IReadOnlyList<string> GetAssetTypes(string packageKey)
    {
        lock (_gate)
        {
            if (!_heads.TryGetValue("asset", out var head) || !IsCurrent(head) || !_assetTypes.TryGetValue(head.SnapshotId, out var types)) return [];
            return types.Where(item => string.Equals(item.PackageKey, packageKey, StringComparison.OrdinalIgnoreCase) && item.Type != "\u0000unknown")
                .Select(item => item.Type)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(type => type, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<object> ListAssetTypes(string scope)
    {
        lock (_gate)
        {
            if (!_heads.TryGetValue("asset", out var head) || !IsCurrent(head) || !_assetTypes.TryGetValue(head.SnapshotId, out var types)) return [];
            return types.Where(item => InScope(item.PackageKey, scope))
                .GroupBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
                .Select(group => (object)new { assetType = group.Key, exportCount = group.Count(), packageCount = group.Select(item => item.PackageKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() })
                .OrderByDescending(item => (int)item.GetType().GetProperty("exportCount")!.GetValue(item)!)
                .ThenBy(item => item.GetType().GetProperty("assetType")!.GetValue(item)!.ToString(), StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<object> SearchText(string keyword, string scope, string? sourceKind)
    {
        var normalized = keyword.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        lock (_gate)
        {
            _databaseGate.Wait();
            try
            {
            if (!_heads.TryGetValue("text", out var head) || !IsCurrent(head)) return [];
            HashSet<string>? ftsMatches = null;
            if (normalized.EnumerateRunes().Count() >= 3)
            {
                if (!_ftsAvailable) throw new InvalidOperationException("SQLite FTS5 trigram 不可用");
                ftsMatches = QueryFtsIds(head.SnapshotId, normalized);
            }
            return _texts.Where(row => row.SnapshotId == head.SnapshotId && (ftsMatches is null || ftsMatches.Contains(row.TextId)) && (sourceKind is null || row.SourceKind.Equals(sourceKind, StringComparison.OrdinalIgnoreCase)) && InScope(row.SourcePath, scope) && row.NormalizedText.Contains(normalized, StringComparison.Ordinal))
                .OrderBy(row => row.SourcePath, StringComparer.Ordinal).ThenBy(row => row.ObjectPath, StringComparer.Ordinal).ThenBy(row => row.RowName, StringComparer.Ordinal).ThenBy(row => row.FieldPointer, StringComparer.Ordinal).ThenBy(row => row.TextId, StringComparer.Ordinal).Select(row => new
                {
                    textId = row.TextId,
                    sourceKind = row.SourceKind,
                    sourcePath = row.SourcePath,
                    objectPath = row.ObjectPath,
                    rowName = row.RowName,
                    fieldPointer = row.FieldPointer,
                    @namespace = row.Namespace,
                    key = row.Key,
                    tableId = row.TableId,
                    text = row.Text,
                    sourceText = row.SourceText,
                    language = row.Language,
                    resolution = row.Resolution,
                    identityLinks = row.Key is null && row.Namespace is null
                        ? Array.Empty<object>()
                        : new object[] { new { source = row.SourcePath, @namespace = row.Namespace, key = row.Key, tableId = row.TableId } }
                }).Cast<object>().ToArray();
            }
            finally { _databaseGate.Release(); }
        }
    }

    public IReadOnlyList<object> FindReferences(string packageKey, string direction)
    {
        lock (_gate)
        {
            if (!_heads.TryGetValue("reference", out var head) || !IsCurrent(head)) return [];
            if (!direction.Equals("incoming", StringComparison.OrdinalIgnoreCase) && !direction.Equals("outgoing", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("direction 必须为 incoming 或 outgoing");
            var query = _references.Where(row => row.SnapshotId == head.SnapshotId && (direction.Equals("incoming", StringComparison.OrdinalIgnoreCase)
                ? string.Equals(row.TargetPackage, packageKey, StringComparison.OrdinalIgnoreCase)
                : string.Equals(row.SourcePackage, packageKey, StringComparison.OrdinalIgnoreCase)));
            return query.OrderBy(row => row.SourcePackage, StringComparer.Ordinal).ThenBy(row => row.TargetPackage, StringComparer.Ordinal).Select(row => new
            {
                sourcePackage = row.SourcePackage,
                targetPackage = string.IsNullOrEmpty(row.TargetPackage) ? null : row.TargetPackage,
                targetRawName = row.TargetRawName,
                edgeKind = row.EdgeKind,
                evidenceCount = row.EvidenceCount,
                resolution = row.Resolution,
                exampleImportIndex = row.ExampleImportIndex
            }).Cast<object>().ToArray();
        }
    }

    public void DiscardSnapshot(string snapshotId)
    {
        lock (_gate)
        {
            _databaseGate.Wait();
            try
            {
            using var transaction = _connection.BeginTransaction();
            try
            {
                foreach (var table in new[] { "texts", "symbols", "assets", "reference_edges", "scan_units", "snapshots" })
                {
                    using var command = _connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"DELETE FROM {table} WHERE {(table == "snapshots" ? "id" : "snapshot_id")}=$snapshot";
                    command.Parameters.AddWithValue("$snapshot", snapshotId);
                    command.ExecuteNonQuery();
                }
                if (_ftsAvailable)
                {
                    using var fts = _connection.CreateCommand();
                    fts.Transaction = transaction;
                    fts.CommandText = "DELETE FROM texts_fts WHERE snapshot_id=$snapshot";
                    fts.Parameters.AddWithValue("$snapshot", snapshotId);
                    fts.ExecuteNonQuery();
                }
                transaction.Commit();
                foreach (var key in _heads.Where(pair => pair.Value.SnapshotId == snapshotId).Select(pair => pair.Key).ToArray()) _heads.Remove(key);
                _symbols.Remove(snapshotId);
                _assetTypes.Remove(snapshotId);
                _unknownAssetExports.Remove(snapshotId);
                _unknownAssetPackages.Remove(snapshotId);
                _symbolSnapshots.Remove(snapshotId);
                _texts.RemoveAll(row => row.SnapshotId == snapshotId);
                _references.RemoveAll(row => row.SnapshotId == snapshotId);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            }
            finally { _databaseGate.Release(); }
        }
    }

    public void CleanupOldSnapshots(string kind, string keepSnapshotId)
    {
        lock (_gate)
        {
            _databaseGate.Wait();
            try
            {
            using var transaction = _connection.BeginTransaction();
            var oldIds = new List<string>();
            using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT id FROM snapshots WHERE kind=$kind AND id<>$keep AND id NOT IN (SELECT snapshot_id FROM index_heads)";
                query.Parameters.AddWithValue("$kind", kind);
                query.Parameters.AddWithValue("$keep", keepSnapshotId);
                using var reader = query.ExecuteReader();
                while (reader.Read()) oldIds.Add(reader.GetString(0));
            }
            try
            {
                foreach (var id in oldIds)
                {
                    foreach (var table in new[] { "texts", "symbols", "assets", "reference_edges", "scan_units", "snapshots" })
                    {
                        using var command = _connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = $"DELETE FROM {table} WHERE {(table == "snapshots" ? "id" : "snapshot_id")}=$snapshot";
                        command.Parameters.AddWithValue("$snapshot", id);
                        command.ExecuteNonQuery();
                    }
                    if (_ftsAvailable)
                    {
                        using var fts = _connection.CreateCommand();
                        fts.Transaction = transaction;
                        fts.CommandText = "DELETE FROM texts_fts WHERE snapshot_id=$snapshot";
                        fts.Parameters.AddWithValue("$snapshot", id);
                        fts.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
                foreach (var id in oldIds)
                {
                    _symbols.Remove(id);
                    _assetTypes.Remove(id);
                    _unknownAssetExports.Remove(id);
                    _unknownAssetPackages.Remove(id);
                    _symbolSnapshots.Remove(id);
                    _texts.RemoveAll(row => row.SnapshotId == id);
                    _references.RemoveAll(row => row.SnapshotId == id);
                }
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            }
            finally { _databaseGate.Release(); }
        }
    }

    public void RecordScanUnit(string snapshotId, string unitKey, string phase, string state, string? errorCode = null, string? errorMessage = null)
    {
        _databaseGate.Wait();
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO scan_units(snapshot_id, unit_key, phase, state, error_code, error_message) VALUES ($snapshot,$unit,$phase,$state,$code,$message) ON CONFLICT(snapshot_id, unit_key, phase) DO UPDATE SET state=excluded.state, error_code=excluded.error_code, error_message=excluded.error_message";
            command.Parameters.AddWithValue("$snapshot", snapshotId);
            command.Parameters.AddWithValue("$unit", unitKey);
            command.Parameters.AddWithValue("$phase", phase);
            command.Parameters.AddWithValue("$state", state);
            AddNullable(command, "$code", errorCode);
            AddNullable(command, "$message", errorMessage);
            command.ExecuteNonQuery();
        }
        finally { _databaseGate.Release(); }
    }

    public void RecordScanUnitAndSaveTask(TaskInfo task, string snapshotId, string unitKey, string phase, string state, string? errorCode = null, string? errorMessage = null)
    {
        lock (_gate)
        {
            _databaseGate.Wait();
            try
            {
                using var transaction = _connection.BeginTransaction();
                using (var command = _connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO scan_units(snapshot_id, unit_key, phase, state, error_code, error_message) VALUES ($snapshot,$unit,$phase,$state,$code,$message) ON CONFLICT(snapshot_id, unit_key, phase) DO UPDATE SET state=excluded.state, error_code=excluded.error_code, error_message=excluded.error_message";
                    command.Parameters.AddWithValue("$snapshot", snapshotId);
                    command.Parameters.AddWithValue("$unit", unitKey);
                    command.Parameters.AddWithValue("$phase", phase);
                    command.Parameters.AddWithValue("$state", state);
                    AddNullable(command, "$code", errorCode);
                    AddNullable(command, "$message", errorMessage);
                    command.ExecuteNonQuery();
                }
                SaveTaskInTransaction(task, transaction);
                transaction.Commit();
            }
            finally { _databaseGate.Release(); }
        }
    }

    public void BeginSnapshot(string kind, string snapshotId, string scope, string fingerprint, string? language = null, bool includeSymbols = false)
    {
        _databaseGate.Wait();
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO snapshots(id, kind, scope_key, source_fingerprint, language, include_symbols, complete, coverage_json, created_utc, state) VALUES ($id,$kind,$scope,$fingerprint,$language,$includeSymbols,0,$coverage,$created,'building')";
            command.Parameters.AddWithValue("$id", snapshotId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            AddNullable(command, "$language", language);
            command.Parameters.AddWithValue("$includeSymbols", includeSymbols ? 1 : 0);
            command.Parameters.AddWithValue("$coverage", JsonSerializer.Serialize(new { succeededUnits = 0, failedUnits = 0, totalUnits = 0, catalogComplete = false, complete = false, symbolsAvailable = false }, JsonDefaults.Options));
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally { _databaseGate.Release(); }
    }

    public void Publish(string kind, string snapshotId, string scope, string fingerprint, bool complete, int succeeded, int failed, int total, bool catalogComplete = true, bool symbolsAvailable = false, string? language = null, bool includeSymbols = false, TaskInfo? terminalTask = null)
    {
        lock (_gate)
        {
        _databaseGate.Wait();
        try
        {
        var builtAt = DateTimeOffset.UtcNow;
        var coverageJson = JsonSerializer.Serialize(new { succeededUnits = succeeded, failedUnits = failed, totalUnits = total, catalogComplete, complete, symbolsAvailable }, JsonDefaults.Options);
        using var transaction = _connection.BeginTransaction();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO snapshots(id, kind, scope_key, source_fingerprint, complete, created_utc, state, language, include_symbols, coverage_json, published_utc) VALUES ($id,$kind,$scope,$fingerprint,$complete,$created,'published',$language,$includeSymbols,$coverage,$published) ON CONFLICT(id) DO UPDATE SET kind=excluded.kind, scope_key=excluded.scope_key, source_fingerprint=excluded.source_fingerprint, complete=excluded.complete, state='published', language=excluded.language, include_symbols=excluded.include_symbols, coverage_json=excluded.coverage_json, published_utc=excluded.published_utc";
            command.Parameters.AddWithValue("$id", snapshotId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$complete", complete ? 1 : 0);
            command.Parameters.AddWithValue("$created", builtAt.ToString("O"));
            AddNullable(command, "$language", language);
            command.Parameters.AddWithValue("$includeSymbols", includeSymbols ? 1 : 0);
            command.Parameters.AddWithValue("$coverage", coverageJson);
            command.Parameters.AddWithValue("$published", builtAt.ToString("O"));
            command.ExecuteNonQuery();
        }
        using (var headCommand = _connection.CreateCommand())
        {
            headCommand.Transaction = transaction;
            headCommand.CommandText = "INSERT INTO index_heads(kind, snapshot_id) VALUES ($kind,$snapshot) ON CONFLICT(kind) DO UPDATE SET snapshot_id=excluded.snapshot_id";
            headCommand.Parameters.AddWithValue("$kind", kind);
            headCommand.Parameters.AddWithValue("$snapshot", snapshotId);
            headCommand.ExecuteNonQuery();
        }
        if (terminalTask is not null)
        {
            using var taskCommand = _connection.CreateCommand();
            taskCommand.Transaction = transaction;
            ConfigureTaskCommand(taskCommand, terminalTask);
            taskCommand.ExecuteNonQuery();
        }
        transaction.Commit();
        _heads[kind] = new Head(kind, snapshotId, scope, fingerprint, complete, succeeded, failed, total, catalogComplete, builtAt);
        if (kind == "asset" && symbolsAvailable) _symbolSnapshots.Add(snapshotId);
        }
        finally { _databaseGate.Release(); }
        }
    }

    private void LoadPublishedData()
    {
        var published = new List<(string Kind, string SnapshotId, string Scope, string Fingerprint, bool Complete, int Succeeded, int Failed, int Total, bool CatalogComplete, bool SymbolsAvailable, DateTimeOffset BuiltAt)>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT s.kind, s.id, s.scope_key, s.source_fingerprint, s.complete, s.created_utc, s.coverage_json FROM index_heads h JOIN snapshots s ON s.id=h.snapshot_id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var created = DateTimeOffset.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTimeOffset.MinValue;
                var coverage = JsonSerializer.Deserialize<StoredCoverage>(reader.GetString(6), JsonDefaults.Options) ?? new StoredCoverage();
                published.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) != 0,
                    coverage.SucceededUnits, coverage.FailedUnits, coverage.TotalUnits, coverage.CatalogComplete, coverage.SymbolsAvailable, created));
            }
        }
        foreach (var item in published)
        {
            _heads[item.Kind] = new Head(item.Kind, item.SnapshotId, item.Scope, item.Fingerprint, item.Complete, item.Succeeded, item.Failed, item.Total, item.CatalogComplete, item.BuiltAt);
            if (item.Kind == "asset" && item.SymbolsAvailable) _symbolSnapshots.Add(item.SnapshotId);
            switch (item.Kind)
            {
                case "asset": LoadAssets(item.SnapshotId); LoadSymbols(item.SnapshotId); break;
                case "text": LoadTexts(item.SnapshotId); break;
                case "reference": LoadReferences(item.SnapshotId); break;
            }
        }
    }

    private void LoadAssets(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT package_key, class_name, metadata_state FROM assets WHERE snapshot_id=$snapshot";
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        var types = new List<(string PackageKey, string Type)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var package = reader.GetString(0);
            var type = GetNullable(reader, 1);
            if (reader.GetString(2).Equals("unresolved", StringComparison.OrdinalIgnoreCase))
            {
                _unknownAssetExports[snapshotId] = _unknownAssetExports.GetValueOrDefault(snapshotId) + 1;
                if (!_unknownAssetPackages.TryGetValue(snapshotId, out var unknownPackages)) _unknownAssetPackages[snapshotId] = unknownPackages = [];
                unknownPackages.Add(package);
            }
            else if (!string.IsNullOrWhiteSpace(type)) types.Add((package, type));
        }
        _assetTypes[snapshotId] = types;
    }

    private void LoadSymbols(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT metadata_json FROM symbols WHERE snapshot_id=$snapshot ORDER BY id";
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        using var reader = command.ExecuteReader();
        var symbols = new List<object>();
        while (reader.Read())
        {
            using var document = JsonDocument.Parse(reader.GetString(0));
            symbols.Add(document.RootElement.Clone());
        }
        _symbols[snapshotId] = symbols;
    }

    private void LoadTexts(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT source_key, source_path, source_file_key, source_kind, object_path, row_name, field_pointer, namespace, text_key, table_id, text, source_text, language, resolution FROM texts WHERE snapshot_id=$snapshot ORDER BY id";
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            _texts.Add(new TextRow(snapshotId, reader.GetString(1), reader.GetString(2), reader.GetString(3), GetNullable(reader, 4), GetNullable(reader, 5), GetNullable(reader, 6), GetNullable(reader, 7), GetNullable(reader, 8), GetNullable(reader, 9), reader.GetString(10), GetNullable(reader, 11), reader.GetString(12), reader.GetString(13), reader.GetString(0)));
        }
    }

    private void LoadReferences(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT source_package, target_package, target_raw, edge_kind, resolution, evidence_count, example_import_index FROM reference_edges WHERE snapshot_id=$snapshot";
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        using var reader = command.ExecuteReader();
        var references = new List<ReferenceRow>();
        while (reader.Read()) references.Add(new ReferenceRow(snapshotId, reader.GetString(0), GetNullable(reader, 1) ?? string.Empty, GetNullable(reader, 2) ?? string.Empty, reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6)));
        _references.AddRange(references);
    }

    private static string? GetNullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private HashSet<string> QueryFtsIds(string snapshotId, string normalized)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT text_id FROM texts_fts WHERE texts_fts MATCH $query AND snapshot_id = $snapshot";
        command.Parameters.AddWithValue("$query", "\"" + normalized.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        using var reader = command.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private async Task InsertAssetRowsAsync(string snapshotId, IReadOnlyList<AssetRow> rows, CancellationToken cancellationToken, SqliteTransaction transaction)
    {
        foreach (var row in rows)
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO assets(snapshot_id, package_key, export_index, object_path, object_path_key, object_name, class_name, class_path, outer_path, archive_id, flags, metadata_state) VALUES ($snapshot,$package,$index,$path,$pathKey,$name,$class,$classPath,$outer,$archive,$flags,$state)";
            command.Parameters.AddWithValue("$snapshot", snapshotId);
            command.Parameters.AddWithValue("$package", row.PackageKey);
            command.Parameters.AddWithValue("$index", row.ExportIndex);
            command.Parameters.AddWithValue("$path", row.ObjectPath);
            command.Parameters.AddWithValue("$pathKey", row.ObjectPath.ToUpperInvariant());
            command.Parameters.AddWithValue("$name", row.ObjectName);
            AddNullable(command, "$class", row.ClassName);
            AddNullable(command, "$classPath", row.ClassPath);
            AddNullable(command, "$outer", row.OuterPath);
            command.Parameters.AddWithValue("$archive", row.ArchiveId);
            command.Parameters.AddWithValue("$flags", row.Flags);
            command.Parameters.AddWithValue("$state", row.MetadataState);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InsertTextRowAsync(TextRow row, CancellationToken cancellationToken, SqliteTransaction transaction)
    {
        var identity = JsonSerializer.Serialize(new { source = row.SourcePath, sourceKind = row.SourceKind, @namespace = row.Namespace, key = row.Key, tableId = row.TableId, language = row.Language }, JsonDefaults.Options);
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO texts(snapshot_id, source_key, source_file_key, source_path, object_path, row_name, field_pointer, source_kind, namespace, text_key, table_id, text, normalized_text, source_text, language, resolution, identity_json) VALUES ($snapshot,$sourceKey,$sourceFile,$path,$object,$row,$field,$kind,$namespace,$key,$table,$text,$normalized,$sourceText,$language,$resolution,$identity)";
        command.Parameters.AddWithValue("$snapshot", row.SnapshotId);
        command.Parameters.AddWithValue("$sourceKey", row.TextId);
        command.Parameters.AddWithValue("$sourceFile", row.SourceFilePath);
        command.Parameters.AddWithValue("$path", row.SourcePath);
        AddNullable(command, "$object", row.ObjectPath);
        AddNullable(command, "$row", row.RowName);
        AddNullable(command, "$field", row.FieldPointer);
        command.Parameters.AddWithValue("$kind", row.SourceKind);
        AddNullable(command, "$namespace", row.Namespace);
        AddNullable(command, "$key", row.Key);
        AddNullable(command, "$table", row.TableId);
        command.Parameters.AddWithValue("$text", row.Text);
        command.Parameters.AddWithValue("$normalized", row.NormalizedText);
        AddNullable(command, "$sourceText", row.SourceText);
        command.Parameters.AddWithValue("$language", row.Language);
        command.Parameters.AddWithValue("$resolution", row.Resolution);
        command.Parameters.AddWithValue("$identity", identity);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted == 0) return;
        if (_ftsAvailable)
        {
            await using var fts = _connection.CreateCommand();
            fts.Transaction = transaction;
            fts.CommandText = "INSERT INTO texts_fts(normalized_text, snapshot_id, text_id) VALUES ($normalized,$snapshot,$id)";
            fts.Parameters.AddWithValue("$normalized", row.NormalizedText);
            fts.Parameters.AddWithValue("$snapshot", row.SnapshotId);
            fts.Parameters.AddWithValue("$id", row.TextId);
            await fts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InsertSymbolsAsync(string snapshotId, string packageKey, IReadOnlyList<object> symbols, CancellationToken cancellationToken, SqliteTransaction transaction)
    {
        foreach (var symbol in symbols)
        {
            var json = JsonSerializer.SerializeToElement(symbol, symbol.GetType(), JsonDefaults.Options);
            var ownerPath = GetJsonString(json, "ownerObjectPath") ?? GetJsonString(json, "objectPath") ?? string.Empty;
            var objectPath = GetJsonString(json, "objectPath");
            var kind = GetJsonString(json, "kind") ?? "class";
            var name = GetJsonString(json, "name") ?? string.Empty;
            var propertyPointer = GetJsonString(json, "propertyPointer");
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO symbols(snapshot_id, package_key, owner_path, object_path, kind, name, name_key, property_pointer, metadata_json) VALUES ($snapshot,$package,$owner,$object,$kind,$name,$nameKey,$property,$metadata)";
            command.Parameters.AddWithValue("$snapshot", snapshotId);
            command.Parameters.AddWithValue("$package", packageKey);
            command.Parameters.AddWithValue("$owner", ownerPath);
            AddNullable(command, "$object", objectPath);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$nameKey", name.ToUpperInvariant());
            AddNullable(command, "$property", propertyPointer);
            command.Parameters.AddWithValue("$metadata", json.GetRawText());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? GetJsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void AddNullable(SqliteCommand command, string name, string? value)
        => command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private async Task InsertScanUnitAsync(string snapshotId, string unitKey, string phase, string state, string? errorCode, string? errorMessage, CancellationToken cancellationToken, SqliteTransaction transaction)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO scan_units(snapshot_id, unit_key, phase, state, error_code, error_message) VALUES ($snapshot,$unit,$phase,$state,$code,$message) ON CONFLICT(snapshot_id, unit_key, phase) DO UPDATE SET state=excluded.state, error_code=excluded.error_code, error_message=excluded.error_message";
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        command.Parameters.AddWithValue("$unit", unitKey);
        command.Parameters.AddWithValue("$phase", phase);
        command.Parameters.AddWithValue("$state", state);
        AddNullable(command, "$code", errorCode);
        AddNullable(command, "$message", errorMessage);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertReferenceRowAsync(ReferenceRow row, CancellationToken cancellationToken, SqliteTransaction transaction)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO reference_edges(snapshot_id, source_package, target_package, target_key, target_raw, edge_kind, resolution, evidence_count, example_import_index) VALUES ($snapshot,$source,$target,$targetKey,$raw,$kind,$resolution,$count,$index)";
        command.Parameters.AddWithValue("$snapshot", row.SnapshotId);
        command.Parameters.AddWithValue("$source", row.SourcePackage);
        AddNullable(command, "$target", string.IsNullOrEmpty(row.TargetPackage) ? null : row.TargetPackage);
        command.Parameters.AddWithValue("$targetKey", string.IsNullOrEmpty(row.TargetPackage) ? "unresolved:" + row.TargetRawName : row.TargetPackage);
        AddNullable(command, "$raw", row.TargetRawName);
        command.Parameters.AddWithValue("$kind", row.EdgeKind);
        command.Parameters.AddWithValue("$resolution", row.Resolution);
        command.Parameters.AddWithValue("$count", row.EvidenceCount);
        command.Parameters.AddWithValue("$index", row.ExampleImportIndex);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void CleanupOrphanSnapshots()
    {
        using var query = _connection.CreateCommand();
        query.CommandText = "SELECT id FROM snapshots WHERE id NOT IN (SELECT snapshot_id FROM index_heads)";
        var ids = new List<string>();
        using (var reader = query.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetString(0));
        if (ids.Count == 0) return;
        using var transaction = _connection.BeginTransaction();
        try
        {
            foreach (var id in ids)
            {
                foreach (var table in new[] { "texts", "symbols", "assets", "reference_edges", "scan_units", "snapshots" })
                {
                    using var command = _connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"DELETE FROM {table} WHERE {(table == "snapshots" ? "id" : "snapshot_id")}=$snapshot";
                    command.Parameters.AddWithValue("$snapshot", id);
                    command.ExecuteNonQuery();
                }
                if (_ftsAvailable)
                {
                    using var fts = _connection.CreateCommand();
                    fts.Transaction = transaction;
                    fts.CommandText = "DELETE FROM texts_fts WHERE snapshot_id=$snapshot";
                    fts.Parameters.AddWithValue("$snapshot", id);
                    fts.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void EnsureSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS snapshots (
              id TEXT PRIMARY KEY,
              kind TEXT NOT NULL CHECK(kind IN ('asset','text','reference')),
              source_fingerprint TEXT NOT NULL,
              scope_key TEXT NOT NULL,
              language TEXT,
              include_symbols INTEGER NOT NULL DEFAULT 0,
              state TEXT NOT NULL CHECK(state IN ('building','published','discarded')),
              complete INTEGER NOT NULL DEFAULT 0,
              coverage_json TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              published_utc TEXT
            );
            CREATE TABLE IF NOT EXISTS index_heads (
              kind TEXT PRIMARY KEY CHECK(kind IN ('asset','text','reference')),
              snapshot_id TEXT NOT NULL REFERENCES snapshots(id)
            );
            CREATE TABLE IF NOT EXISTS scan_units (
              snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
              unit_key TEXT NOT NULL,
              phase TEXT NOT NULL,
              state TEXT NOT NULL CHECK(state IN ('succeeded','failed','skipped')),
              error_code TEXT,
              error_message TEXT,
              PRIMARY KEY(snapshot_id, unit_key, phase)
            );
            CREATE TABLE IF NOT EXISTS assets (
              snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
              package_key TEXT NOT NULL,
              export_index INTEGER NOT NULL,
              object_path TEXT NOT NULL,
              object_path_key TEXT NOT NULL,
              object_name TEXT NOT NULL,
              class_name TEXT,
              class_path TEXT,
              outer_path TEXT,
              archive_id TEXT NOT NULL,
              flags TEXT NOT NULL,
              metadata_state TEXT NOT NULL,
              PRIMARY KEY(snapshot_id, package_key, export_index)
            );
            CREATE INDEX IF NOT EXISTS ix_assets_type ON assets(snapshot_id, class_name, package_key);
            CREATE INDEX IF NOT EXISTS ix_assets_path ON assets(snapshot_id, object_path_key, export_index);
            CREATE TABLE IF NOT EXISTS symbols (
              id INTEGER PRIMARY KEY,
              snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
              package_key TEXT NOT NULL,
              owner_path TEXT NOT NULL,
              object_path TEXT,
              kind TEXT NOT NULL CHECK(kind IN ('class','function','property')),
              name TEXT NOT NULL,
              name_key TEXT NOT NULL,
              property_pointer TEXT,
              metadata_json TEXT NOT NULL,
              UNIQUE(snapshot_id, package_key, owner_path, kind, name, property_pointer)
            );
            CREATE INDEX IF NOT EXISTS ix_symbols_query ON symbols(snapshot_id, kind, name_key, id);
            CREATE TABLE IF NOT EXISTS texts (
              id INTEGER PRIMARY KEY,
              snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
              source_key TEXT NOT NULL,
              source_file_key TEXT NOT NULL,
              source_path TEXT NOT NULL,
              object_path TEXT,
              row_name TEXT,
              field_pointer TEXT,
              source_kind TEXT NOT NULL,
              namespace TEXT,
              text_key TEXT,
              table_id TEXT,
              text TEXT NOT NULL,
              normalized_text TEXT NOT NULL,
              source_text TEXT,
              language TEXT NOT NULL,
              resolution TEXT NOT NULL,
              identity_json TEXT NOT NULL,
              UNIQUE(snapshot_id, source_key)
            );
            CREATE INDEX IF NOT EXISTS ix_texts_scope ON texts(snapshot_id, source_file_key, id);
            CREATE INDEX IF NOT EXISTS ix_texts_identity ON texts(snapshot_id, namespace, text_key);
            CREATE VIRTUAL TABLE IF NOT EXISTS texts_fts USING fts5(
              normalized_text,
              snapshot_id UNINDEXED,
              text_id UNINDEXED,
              tokenize='trigram case_sensitive 1'
            );
            CREATE TABLE IF NOT EXISTS reference_edges (
              id INTEGER PRIMARY KEY,
              snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
              source_package TEXT NOT NULL,
              target_package TEXT,
              target_key TEXT NOT NULL,
              target_raw TEXT,
              edge_kind TEXT NOT NULL CHECK(edge_kind='packageImport'),
              resolution TEXT NOT NULL,
              evidence_count INTEGER NOT NULL,
              example_import_index INTEGER NOT NULL,
              UNIQUE(snapshot_id, source_package, target_key, edge_kind)
            );
            CREATE INDEX IF NOT EXISTS ix_refs_out ON reference_edges(snapshot_id, source_package, target_key);
            CREATE INDEX IF NOT EXISTS ix_refs_in ON reference_edges(snapshot_id, target_key, source_package);
            CREATE TABLE IF NOT EXISTS tasks (
              id TEXT PRIMARY KEY,
              session_id TEXT NOT NULL,
              kind TEXT NOT NULL,
              state TEXT NOT NULL,
              snapshot_id TEXT REFERENCES snapshots(id) ON DELETE SET NULL,
              request_json TEXT NOT NULL,
              progress_json TEXT NOT NULL,
              result_json TEXT,
              started_utc TEXT NOT NULL,
              finished_utc TEXT
            );
            CREATE TABLE IF NOT EXISTS task_errors (
              task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
              sequence INTEGER NOT NULL,
              subject TEXT NOT NULL,
              stage TEXT NOT NULL,
              code TEXT NOT NULL,
              message TEXT NOT NULL,
              PRIMARY KEY(task_id, sequence)
            );
            """;
        try { command.ExecuteNonQuery(); }
        catch (SqliteException ex) { throw new NotSupportedException("SQLite FTS5 trigram tokenizer 不可用", ex); }
        _ftsAvailable = true;
    }

    private static bool IsRequestedLocalization(string path, string language)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var culture = segments.FirstOrDefault(IsCultureSegment);
        if (culture is null) return true;
        return language.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)
            ? culture.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) || culture.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) || culture.Equals("zh-SG", StringComparison.OrdinalIgnoreCase)
            : language.Equals("en", StringComparison.OrdinalIgnoreCase) && culture.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        static bool IsCultureSegment(string segment)
        {
            var parts = segment.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsLetter)) return false;
            return parts.Skip(1).All(part => part.Length is >= 2 and <= 8 && part.All(char.IsLetterOrDigit));
        }
    }

    private static string PackageDisplayPath(string path)
        => path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)
            ? path[..path.LastIndexOf('.')].Replace("/Content/", "/", StringComparison.OrdinalIgnoreCase)
            : path;

    private static string ObjectDisplayPath(string packagePath, CUE4Parse.UE4.Assets.ResolvedObject metadata)
    {
        var displayPackage = PackageDisplayPath(packagePath);
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

    private static string NormalizePackageName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/')) normalized = "/" + normalized;
        if (normalized.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || normalized.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) normalized = normalized[..normalized.LastIndexOf('.')];
        var contentMarker = normalized.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (contentMarker >= 0) normalized = "/Game/" + normalized[(contentMarker + "/Content/".Length)..];
        return normalized;
    }

    private static IEnumerable<StringLeaf> StringLeaves(JToken token, string pointer)
    {
        if (token.Type == JTokenType.String)
        {
            yield return new StringLeaf(pointer, token.Value<string>() ?? string.Empty, null, null, null, null, "sourceOnly");
            yield break;
        }
        if (token is JObject obj)
        {
            var @namespace = obj["Namespace"]?.Value<string>();
            var key = obj["Key"]?.Value<string>();
            var sourceText = obj["SourceString"]?.Value<string>();
            var localizedText = obj["LocalizedString"]?.Value<string>();
            var tableId = obj["TableId"]?.Value<string>();
            if (sourceText is not null || localizedText is not null || @namespace is not null || tableId is not null)
            {
                var resolvedText = !string.IsNullOrEmpty(localizedText) ? localizedText! : sourceText ?? string.Empty;
                var resolution = !string.IsNullOrEmpty(localizedText) ? "resolvedLocalized" : sourceText is not null ? "sourceOnly" : "unresolved";
                yield return new StringLeaf(pointer, resolvedText, @namespace, key, tableId, sourceText, resolution);
                yield break;
            }
            foreach (var property in obj.Properties())
            {
                var escaped = property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                foreach (var leaf in StringLeaves(property.Value, pointer + "/" + escaped)) yield return leaf;
            }
            yield break;
        }
        if (token is JArray array)
        {
            for (var index = 0; index < array.Count; index++)
                foreach (var leaf in StringLeaves(array[index], pointer + "/" + index.ToString(CultureInfo.InvariantCulture))) yield return leaf;
            yield break;
        }
        foreach (var child in token.Children())
            foreach (var leaf in StringLeaves(child, pointer)) yield return leaf;
    }

    private static string StableSymbolId(string snapshotId, string path, int exportIndex, string kind, string name)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { path, exportIndex.ToString(CultureInfo.InvariantCulture), kind, name }, JsonDefaults.Options))).ToLowerInvariant();

    private static string SymbolKey(string snapshotId, object symbol) => snapshotId + ":" + (symbol.GetType().GetProperty("symbolId")?.GetValue(symbol)?.ToString() ?? StableSymbolId(snapshotId, "unknown", 0, "unknown", symbol.ToString() ?? string.Empty));
    private static bool InScope(string path, string scope) => path.StartsWith(scope.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) || path.Equals(scope, StringComparison.OrdinalIgnoreCase);
    public void Dispose()
    {
        _databaseGate.Dispose();
        _connection.Dispose();
    }

    private sealed record Head(string Kind, string SnapshotId, string Scope, string SourceFingerprint, bool Complete, int SucceededUnits, int FailedUnits, int TotalUnits, bool CatalogComplete, DateTimeOffset BuiltAtUtc);
    private sealed class StoredCoverage
    {
        public int SucceededUnits { get; init; }
        public int FailedUnits { get; init; }
        public int TotalUnits { get; init; }
        public bool CatalogComplete { get; init; }
        public bool Complete { get; init; }
        public bool SymbolsAvailable { get; init; }
    }
    private sealed class StoredTaskProgress
    {
        public string? Scope { get; init; }
        public bool Stale { get; init; }
        public string? Phase { get; init; }
        public string? CurrentSubject { get; init; }
        public int? TotalUnits { get; init; }
        public int ProcessedUnits { get; init; }
        public int SucceededUnits { get; init; }
        public int FailedUnits { get; init; }
        public int SkippedUnits { get; init; }
        public bool CancelRequested { get; init; }
        public bool CanInterruptCurrentUnit { get; init; }
        public int ErrorsVersion { get; init; }
        public string? SourceFingerprint { get; init; }

        public StoredTaskProgress() { }
        public StoredTaskProgress(TaskInfo task)
        {
            Scope = task.Scope;
            Stale = task.Stale;
            Phase = task.Phase;
            CurrentSubject = task.CurrentSubject;
            TotalUnits = task.TotalUnits;
            ProcessedUnits = task.ProcessedUnits;
            SucceededUnits = task.SucceededUnits;
            FailedUnits = task.FailedUnits;
            SkippedUnits = task.SkippedUnits;
            CancelRequested = task.CancelRequested;
            CanInterruptCurrentUnit = task.CanInterruptCurrentUnit;
            ErrorsVersion = task.ErrorsVersion;
            SourceFingerprint = task.SourceFingerprint;
        }
    }
    private sealed record AssetRow(string PackageKey, int ExportIndex, string ObjectPath, string ObjectName, string? ClassName, string? ClassPath, string? OuterPath, string ArchiveId, string Flags, string MetadataState);
    private sealed record StringLeaf(string Pointer, string Text, string? Namespace, string? Key, string? TableId, string? SourceText, string Resolution);
    private sealed record TextRow(string SnapshotId, string SourcePath, string SourceFilePath, string SourceKind, string? ObjectPath, string? RowName, string? FieldPointer, string? Namespace, string? Key, string? TableId, string Text, string? SourceText, string Language, string Resolution, string? PersistedTextId = null)
    {
        public string TextId { get; } = PersistedTextId ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { SourceKind, SourcePath, ObjectPath, RowName, FieldPointer, Namespace, Key, TableId, Language }, JsonDefaults.Options))).ToLowerInvariant();
        public string NormalizedText { get; } = Text.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    }
    private sealed record ReferenceRow(string SnapshotId, string SourcePackage, string TargetPackage, string TargetRawName, string EdgeKind, string Resolution, int EvidenceCount, int ExampleImportIndex);

    private sealed class ReferenceTupleComparer : IEqualityComparer<(string SourcePackage, string TargetPackage, string UnknownKey, string EdgeKind)>
    {
        public static ReferenceTupleComparer Instance { get; } = new();
        public bool Equals((string SourcePackage, string TargetPackage, string UnknownKey, string EdgeKind) x, (string SourcePackage, string TargetPackage, string UnknownKey, string EdgeKind) y)
            => string.Equals(x.SourcePackage, y.SourcePackage, StringComparison.OrdinalIgnoreCase) && string.Equals(x.TargetPackage, y.TargetPackage, StringComparison.OrdinalIgnoreCase) && string.Equals(x.UnknownKey, y.UnknownKey, StringComparison.Ordinal) && string.Equals(x.EdgeKind, y.EdgeKind, StringComparison.Ordinal);
        public int GetHashCode((string SourcePackage, string TargetPackage, string UnknownKey, string EdgeKind) value)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.SourcePackage), StringComparer.OrdinalIgnoreCase.GetHashCode(value.TargetPackage), StringComparer.Ordinal.GetHashCode(value.UnknownKey), StringComparer.Ordinal.GetHashCode(value.EdgeKind));
    }
}
