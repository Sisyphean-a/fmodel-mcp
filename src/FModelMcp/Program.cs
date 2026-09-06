using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FModelMcp.Contracts;
using FModelMcp.Host;
using FModelMcp.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

if (args.Contains("--worker", StringComparer.Ordinal))
{
    await WorkerMode.RunAsync(args);
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
var configPath = GetOption(args, "--config");
var runtime = new AppRuntime(configPath);
builder.Services.AddSingleton(runtime);
var schemaOptions = new AIJsonSchemaCreateOptions
{
    TransformOptions = new AIJsonSchemaTransformOptions { DisallowAdditionalProperties = true }
};
var toolOptions = new McpServerToolCreateOptions
{
    SerializerOptions = JsonDefaults.Options,
    SchemaCreateOptions = schemaOptions
};
var toolHandler = new FModelTools(runtime);
var tools = typeof(FModelTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
    .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
    .Select(method => McpServerTool.Create(method, toolHandler, toolOptions))
    .ToArray();
var patchSchema = AIJsonUtilities.CreateJsonSchema(typeof(ConfigPatchSchema), null, false, null, JsonDefaults.Options, schemaOptions);
var setConfigTool = tools.Single(tool => tool.ProtocolTool.Name == "set_config");
var setConfigSchema = JsonNode.Parse(setConfigTool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
setConfigSchema["properties"]!["patch"] = JsonNode.Parse(patchSchema.GetRawText());
setConfigTool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(setConfigSchema, JsonDefaults.Options);
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools(tools);

using var host = builder.Build();
await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
try
{
    await host.RunAsync().ConfigureAwait(false);
}
finally
{
    await runtime.DisposeAsync().ConfigureAwait(false);
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
    return null;
}
