using System.Text.Json;
using FModelMcp.Config;
using FModelMcp.Contracts;
using FModelMcp.Indexing;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using Xunit;

namespace FModelMcp.Tests;

public sealed class ContractTests
{
    [Fact]
    public void ToolResults_duplicate_structured_content_as_text_and_map_success()
    {
        var result = ToolResults.Success("session-1", new { value = "ok" });

        Assert.False(result.IsError);
        Assert.NotNull(result.StructuredContent);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal(result.StructuredContent!.Value.GetRawText(), text);
    }

    [Fact]
    public void ToolResults_map_failure_to_is_error_with_same_envelope()
    {
        var result = ToolResults.Failure("session-1", "INVALID_ARGUMENT", "bad input");

        Assert.True(result.IsError);
        Assert.Contains("INVALID_ARGUMENT", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(result.StructuredContent!.Value.GetRawText(), Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void ToolResults_replace_oversized_success_with_bounded_failure_envelope()
    {
        var result = ToolResults.Success("session-1", new { value = new string('x', 4_000) }, maxResultBytes: 4_096);

        Assert.True(result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.Equal("OUTPUT_BUDGET_EXCEEDED", structured.GetProperty("error").GetProperty("code").GetString());
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal(structured.GetRawText(), text);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(text), 1, 4_096);
    }

    [Fact]
    public void Config_patch_rejects_fractional_integer_limit()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ConfigPatch>(
            "{\"limits\":{\"maxPageSize\":1.5}}", JsonDefaults.Options));
    }

    [Fact]
    public void Sqlite_supports_required_fts5_trigram_tokenizer()
    {
        using var temp = new TempDirectory();
        using var store = new IndexStore(Path.Combine(temp.Path, "index.sqlite"));

        Assert.True(store.FtsAvailable);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(temp.Path, "probe.sqlite") }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE VIRTUAL TABLE text_probe USING fts5(value, tokenize='trigram case_sensitive 1');";
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Index_heads_and_coverage_survive_store_reopen()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "index.sqlite");
        using (var store = new IndexStore(path))
            store.Publish("asset", "snapshot-1", "/Game", "fingerprint", true, 3, 0, 3, true, symbolsAvailable: true);

        using var reopened = new IndexStore(path);
        Assert.True(reopened.HasPublished("asset", "/Game/Foo"));
        Assert.True(reopened.HasSymbolCoverage("/Game/Foo"));
        Assert.Equal("snapshot-1", Assert.IsType<string>(reopened.GetHeads()["asset"].GetType().GetProperty("snapshotId")!.GetValue(reopened.GetHeads()["asset"])));
        Assert.Equal(3, reopened.GetHeadCoverage("asset")!.SucceededUnits);
    }

    [Fact]
    public void Index_store_uses_normalized_v1_schema_without_legacy_tables()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "schema.sqlite");
        using (var store = new IndexStore(path)) { }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, version.ExecuteScalar());
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = tables.ExecuteReader())
            while (reader.Read()) names.Add(reader.GetString(0));
        Assert.Contains("assets", names);
        Assert.Contains("symbols", names);
        Assert.Contains("texts", names);
        Assert.Contains("reference_edges", names);
        Assert.Contains("tasks", names);
        Assert.Contains("task_errors", names);
        Assert.Contains("snapshots", names);
        Assert.Contains("scan_units", names);
        Assert.DoesNotContain("asset_exports", names);
        Assert.DoesNotContain("text_metadata", names);
        Assert.DoesNotContain("payload", names);
        Assert.DoesNotContain("errors", names);

        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(texts)";
        var textColumns = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = columns.ExecuteReader())
            while (reader.Read()) textColumns.Add(reader.GetString(1));
        Assert.Contains("source_key", textColumns);
        Assert.Contains("source_file_key", textColumns);
        Assert.Contains("identity_json", textColumns);
    }

    [Fact]
    public void Task_records_restore_as_stale_and_interrupt_active_work()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "tasks.sqlite");
        using (var store = new IndexStore(path))
        {
            var task = new TaskInfo { TaskId = "task-1", Kind = "asset", State = "running", SessionId = "old-session", Scope = "/Game", TotalUnits = 2 };
            task.Errors.Add(new TaskErrorInfo(0, "/Game/A.uasset", "asset", "PACKAGE_PARSE_FAILED", "failed"));
            task.ErrorsVersion = 1;
            store.SaveTask(task);
        }

        using var reopened = new IndexStore(path);
        var restored = Assert.Single(reopened.LoadTasksAndMarkOrphans());
        Assert.Equal("interrupted", restored.State);
        Assert.True(restored.Stale);
        Assert.Single(restored.Errors);
        Assert.Equal(1, restored.ErrorsVersion);
    }

    [Fact]
    public void Config_validation_rejects_unknown_engine_and_overlapping_roots()
    {
        using var temp = new TempDirectory();
        var game = Directory.CreateDirectory(Path.Combine(temp.Path, "game"));
        Directory.CreateDirectory(Path.Combine(game.FullName, "b1", "Content", "Paks"));
        var config = new GameConfig
        {
            GameRoot = game.FullName,
            UeVersion = "UE5_3",
            OutputRoot = Path.Combine(temp.Path, "out"),
            CacheRoot = Path.Combine(temp.Path, "cache")
        };

        var result = ConfigService.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains("GAME_BlackMythWukong", result.Message);
    }

    [Fact]
    public void Config_patch_rejects_unknown_fields()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ConfigPatch>("{\"unknown\":1}", JsonDefaults.Options));
    }

    [Fact]
    public void Config_patch_distinguishes_omitted_and_explicit_null()
    {
        using var temp = new TempDirectory();
        var game = Directory.CreateDirectory(Path.Combine(temp.Path, "game"));
        Directory.CreateDirectory(Path.Combine(game.FullName, "b1", "Content", "Paks"));
        var service = new ConfigService(null);
        service.SetCurrent(new GameConfig
        {
            GameRoot = game.FullName,
            UeVersion = "GAME_BlackMythWukong",
            AesKey = new string('A', 64),
            OutputRoot = Path.Combine(temp.Path, "out"),
            CacheRoot = Path.Combine(temp.Path, "cache")
        });

        var patch = JsonSerializer.Deserialize<ConfigPatch>("{\"aesKey\":null}", JsonDefaults.Options)!;
        var candidate = service.BuildCandidate(patch);

        Assert.True(candidate.IsValid, candidate.Message);
        Assert.Null(candidate.Config!.AesKey);
    }

    [Fact]
    public void Config_validation_accepts_empty_key_but_requires_existing_archive_directory()
    {
        using var temp = new TempDirectory();
        var game = Directory.CreateDirectory(Path.Combine(temp.Path, "game"));
        Directory.CreateDirectory(Path.Combine(game.FullName, "b1", "Content", "Paks"));
        var config = new GameConfig
        {
            GameRoot = game.FullName,
            UeVersion = "GAME_BlackMythWukong",
            OutputRoot = Path.Combine(temp.Path, "out"),
            CacheRoot = Path.Combine(temp.Path, "cache")
        };

        var result = ConfigService.Validate(config);

        Assert.True(result.IsValid, result.Message);
        Assert.Null(result.Config!.AesKey);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fmodel-mcp-tests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
