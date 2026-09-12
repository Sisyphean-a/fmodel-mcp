using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Sdk;

namespace FModelMcp.Tests;

public sealed class McpIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Official_client_completes_handshake_lists_tools_and_calls_status()
    {
        var config = Environment.GetEnvironmentVariable("FMODEL_TEST_CONFIG");
        if (string.IsNullOrWhiteSpace(config) || !File.Exists(config))
            throw SkipException.ForSkip("FMODEL_TEST_CONFIG 未提供；真实游戏集成探针跳过");

        var command = Environment.GetEnvironmentVariable("FMODEL_TEST_COMMAND") ?? Path.Combine(AppContext.BaseDirectory, "FModelMcp.exe");
        Assert.True(File.Exists(command), $"发布/构建 apphost 不存在: {command}");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = command,
            Arguments = ["--config", Path.GetFullPath(config)],
            Name = "fmodel-mcp-test",
            WorkingDirectory = AppContext.BaseDirectory
        });
        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions(), null, CancellationToken.None);

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var expected = new[] { "get_status", "set_config", "remount", "list_archives", "search_files", "list_directory", "list_objects", "build_asset_index", "list_asset_types", "search_symbols", "get_object", "get_datatable", "get_class_info", "get_function", "get_string_table", "get_curve", "build_text_index", "search_text", "build_reference_index", "find_references", "export_raw", "export_json", "get_task", "cancel_task" };
        Assert.Equal(expected.OrderBy(name => name), tools.Select(tool => tool.Name).OrderBy(name => name));
        foreach (var tool in tools)
        {
            var schema = tool.ProtocolTool.InputSchema;
            Assert.True(schema.TryGetProperty("additionalProperties", out var closed) && closed.ValueKind == JsonValueKind.False, tool.Name);
            AssertToolEnvelopeOutputSchema(tool);
        }
        var setConfig = tools.Single(tool => tool.Name == "set_config").ProtocolTool.InputSchema;
        var patch = setConfig.GetProperty("properties").GetProperty("patch");
        Assert.True(patch.GetProperty("additionalProperties").ValueKind == JsonValueKind.False);
        Assert.DoesNotContain("schemaVersion", patch.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        foreach (var nested in new[] { "nativeLibraries", "limits" })
        {
            var nestedSchema = patch.GetProperty("properties").GetProperty(nested);
            Assert.True(nestedSchema.GetProperty("additionalProperties").ValueKind == JsonValueKind.False, nested);
        }
        var status = await client.CallToolAsync("get_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        Assert.True(status.IsError is false);
        Assert.NotNull(status.StructuredContent);
        var statusJson = status.StructuredContent!.Value.GetRawText();
        Assert.DoesNotContain("aesKey", statusJson, StringComparison.OrdinalIgnoreCase);
        var filesPage = await client.CallToolAsync("search_files", new Dictionary<string, object?>
        {
            ["pattern"] = "**",
            ["mode"] = "glob",
            ["extension"] = "uasset",
            ["limit"] = 1
        }, cancellationToken: CancellationToken.None);
        Assert.False(filesPage.IsError);
        var filesEnvelope = RequireEnvelope(filesPage);
        var fileItems = filesEnvelope.GetProperty("data").GetProperty("items");
        Assert.NotEmpty(fileItems.EnumerateArray());
        var packagePath = fileItems[0].GetProperty("filePath").GetString();
        Assert.False(string.IsNullOrWhiteSpace(packagePath));
        var fileCursor = filesEnvelope.GetProperty("page").GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fileCursor));

        var mismatchedCursor = await client.CallToolAsync("search_files", new Dictionary<string, object?>
        {
            ["pattern"] = "**",
            ["mode"] = "glob",
            ["extension"] = "uasset",
            ["limit"] = 2,
            ["cursor"] = fileCursor
        }, cancellationToken: CancellationToken.None);
        Assert.True(mismatchedCursor.IsError);
        Assert.Equal("CURSOR_QUERY_MISMATCH", ErrorCode(mismatchedCursor));

        var invalidCursor = await client.CallToolAsync("search_files", new Dictionary<string, object?>
        {
            ["pattern"] = "**",
            ["mode"] = "glob",
            ["extension"] = "uasset",
            ["limit"] = 1,
            ["cursor"] = "not-a-cursor"
        }, cancellationToken: CancellationToken.None);
        Assert.True(invalidCursor.IsError);
        Assert.Equal("INVALID_CURSOR", ErrorCode(invalidCursor));

        var traversalExport = await client.CallToolAsync("export_raw", new Dictionary<string, object?>
        {
            ["path"] = packagePath,
            ["outDir"] = "../outside"
        }, cancellationToken: CancellationToken.None);
        Assert.False(traversalExport.IsError);
        var exportData = RequireEnvelope(traversalExport).GetProperty("data");
        var exportTaskId = exportData.GetProperty("taskId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(exportTaskId));
        var failedExport = await WaitForTerminalTaskAsync(client, exportTaskId!);
        Assert.Equal("failed", failedExport.GetProperty("state").GetString());
        Assert.Contains(failedExport.GetProperty("errors").EnumerateArray(), error =>
            error.GetProperty("code").GetString() == "PATH_NOT_ALLOWED");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Second_client_reports_cache_lock_without_replacing_first_worker()
    {
        var config = Environment.GetEnvironmentVariable("FMODEL_TEST_CONFIG");
        if (string.IsNullOrWhiteSpace(config) || !File.Exists(config))
            throw SkipException.ForSkip("FMODEL_TEST_CONFIG 未提供；真实游戏集成探针跳过");

        var command = Environment.GetEnvironmentVariable("FMODEL_TEST_COMMAND") ?? Path.Combine(AppContext.BaseDirectory, "FModelMcp.exe");
        Assert.True(File.Exists(command), $"发布/构建 apphost 不存在: {command}");
        static StdioClientTransport CreateTransport(string command, string config, string name)
            => new(new StdioClientTransportOptions
            {
                Command = command,
                Arguments = ["--config", Path.GetFullPath(config)],
                Name = name,
                WorkingDirectory = AppContext.BaseDirectory
            });

        await using var first = await McpClient.CreateAsync(CreateTransport(command, config, "fmodel-mcp-lock-first"), new McpClientOptions(), null, CancellationToken.None);
        await using var second = await McpClient.CreateAsync(CreateTransport(command, config, "fmodel-mcp-lock-second"), new McpClientOptions(), null, CancellationToken.None);
        var firstStatus = await first.CallToolAsync("get_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        var secondStatus = await second.CallToolAsync("get_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        Assert.False(firstStatus.IsError);
        Assert.True(secondStatus.IsError);
        Assert.Equal("CACHE_LOCKED", secondStatus.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
    }

    private static JsonElement RequireEnvelope(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        return result.StructuredContent!.Value;
    }

    private static string? ErrorCode(CallToolResult result)
    {
        var envelope = RequireEnvelope(result);
        return envelope.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    private static async Task<JsonElement> WaitForTerminalTaskAsync(McpClient client, string taskId)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var result = await client.CallToolAsync("get_task", new Dictionary<string, object?>
            {
                ["taskId"] = taskId,
                ["includeErrors"] = true,
                ["limit"] = 10
            }, cancellationToken: CancellationToken.None);
            var data = RequireEnvelope(result).GetProperty("data");
            var state = data.GetProperty("state").GetString();
            if (state is "completed" or "completedWithErrors" or "failed" or "cancelled" or "interrupted") return data;
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"任务 {taskId} 未在测试预算内结束");
    }

    private static void AssertToolEnvelopeOutputSchema(McpClientTool tool)
    {
        var property = tool.ProtocolTool.GetType().GetProperty("OutputSchema");
        Assert.NotNull(property);
        var value = property!.GetValue(tool.ProtocolTool);
        Assert.NotNull(value);
        var schema = value switch
        {
            JsonElement element => element,
            JsonDocument document => document.RootElement,
            _ => JsonSerializer.SerializeToElement(value)
        };
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        var properties = schema.GetProperty("properties");
        foreach (var name in new[] { "ok", "sessionId", "data", "page", "coverage", "warnings", "error" })
            Assert.True(properties.TryGetProperty(name, out _), $"{tool.Name} outputSchema 缺少 {name}");
    }
}
