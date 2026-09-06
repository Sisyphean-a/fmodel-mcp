using System.ComponentModel;
using FModelMcp.Contracts;
using FModelMcp.Worker;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FModelMcp.Host;

[McpServerToolType]
public sealed class FModelTools
{
    private readonly AppRuntime _runtime;

    public FModelTools(AppRuntime runtime) => _runtime = runtime;

    [McpServerTool(Name = "get_status", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("低成本只读状态查询；无前置条件，不触发挂载或扫描，返回当前诊断与最近错误，不分页截断状态字段。")]
    public Task<CallToolResult> GetStatus(CancellationToken cancellationToken = default)
        => _runtime.GetStatusAsync(cancellationToken);

    [McpServerTool(Name = "set_config", ReadOnly = false, OpenWorld = false, Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("配置校验成本低；需要绝对本地路径，可能持久化配置并替换 Worker 会话；结果只返回脱敏摘要，长诊断通过错误字段表达。")]
    public Task<CallToolResult> SetConfig(ConfigPatch patch, bool persist = false, CancellationToken cancellationToken = default)
        => _runtime.SetConfigAsync(patch, persist, cancellationToken);

    [McpServerTool(Name = "remount", ReadOnly = false, OpenWorld = false, Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("中等启动与目录扫描成本；需要已配置会话，会重启 Worker、重扫 pak 并使旧游标失效；列表结果按 limit 分页。")]
    public Task<CallToolResult> Remount(CancellationToken cancellationToken = default)
        => _runtime.RemountAsync(cancellationToken);

    [McpServerTool(Name = "list_archives", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("目录快照查询成本与容器数成正比；需要已配置会话，不修改文件；按 limit 分页，未挂载和 IoStore 诊断不隐藏。")]
    public Task<CallToolResult> ListArchives(int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("list_archives", new { limit, cursor }, cancellationToken);

    [McpServerTool(Name = "search_files", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("按有效目录线性扫描，regex 受超时预算；需要已挂载目录，仅读取元数据；按 limit 分页，不把冲突路径当作可读赢家。")]
    public Task<CallToolResult> SearchFiles([Description("Provider 虚拟路径 glob 或 .NET regex。") ] string pattern, string mode = "glob", string? extension = null, string? assetType = null, string? scope = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("search_files", new { pattern, mode, extension, assetType, scope, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "list_directory", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("低成本目录元数据查询；需要已挂载目录，不加载 UObject；只返回直接子项并按 limit 分页。")]
    public Task<CallToolResult> ListDirectory(string path = "/Game", int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("list_directory", new { path, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "list_objects", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("按包大小读取头部/元数据，可能触发包解压但不应加载全部 export；需要有效包，按 exportIndex 分页，未知元数据保留为 unresolved。")]
    public Task<CallToolResult> ListObjects(string path, string? type = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("list_objects", new { path, type, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "build_asset_index", ReadOnly = false, OpenWorld = false, Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("高成本后台逐包扫描；需要有效挂载，会创建并发布新的 asset 快照但不改游戏文件；get_task 查看失败和覆盖率，结果不塞入本调用。")]
    public Task<CallToolResult> BuildAssetIndex(string? scope = null, bool includeSymbols = true, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("build_asset_index", new { scope, includeSymbols }, cancellationToken);

    [McpServerTool(Name = "list_asset_types", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("已发布快照上的内存/SQLite 查询；需要 asset 索引覆盖目录；按 limit 分页，未知 export 单独计数。")]
    public Task<CallToolResult> ListAssetTypes(string? dir = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("list_asset_types", new { dir, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "search_symbols", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("已发布 symbols 快照上的查询；需要 includeSymbols=true 的 asset 索引，按 limit 分页；不声明运行时 Hook 能力。")]
    public Task<CallToolResult> SearchSymbols(string pattern, string kind = "any", string? scope = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("search_symbols", new { pattern, kind, scope, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "get_object", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("可能触发完整包解析的高成本前台读取；需要有效对象；fields/depth/maxProjectionBytes 控制输出，超预算失败而不返回半个 JSON。")]
    public Task<CallToolResult> GetObject(string path, int? exportIndex = null, int depth = 4, string[]? fields = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("get_object", new { path, exportIndex, depth, fields }, cancellationToken);

    [McpServerTool(Name = "get_datatable", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("可能加载整张表的前台读取；需要 UDataTable，where/fields 仅支持受限类型比较；按行分页，字段不存在或预算超限明确失败。")]
    public Task<CallToolResult> GetDataTable(string path, int? exportIndex = null, string mode = "rows", string? rowName = null, FilterInput[]? where = null, string[]? fields = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("get_datatable", new { path, exportIndex, mode, rowName, where, fields, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "get_class_info", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("前台反射读取，继承成员可能触发额外包解析；需要 UClass；列表 section 按 limit 分页，默认值缺失不填零。")]
    public Task<CallToolResult> GetClassInfo(string path, int? exportIndex = null, string section = "summary", bool includeInherited = false, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("get_class_info", new { path, exportIndex, section, includeInherited, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "get_function", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("前台函数解析成本随脚本大小增长；需要 UFunction；参数/表达式按页返回，partial/failed 脚本以 SCRIPT_PARSE_FAILED 和前缀表达，不伪装完整。")]
    public Task<CallToolResult> GetFunction(string path, int? exportIndex = null, string section = "summary", int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("get_function", new { path, exportIndex, section, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "get_string_table", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("前台文本资源读取；需要 StringTable 对象或 locres 文件；精确 Ordinal key/namespace 查询按页返回，不把同文案合并。")]
    public Task<CallToolResult> GetStringTable(string path, int? exportIndex = null, string? key = null, string? @namespace = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("get_string_table", new { path, exportIndex, key, @namespace, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "get_curve", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("前台曲线解析；需要 UCurveTable/UCurveFloat；names/keys 按页返回，保留原始 keyIndex、重复时间和未存储字段状态。")]
    public Task<CallToolResult> GetCurve(string path, int? exportIndex = null, string? curveName = null, string mode = "keys", int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("get_curve", new { path, exportIndex, curveName, mode, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "build_text_index", ReadOnly = false, OpenWorld = false, Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("高成本后台逐包/逐 locres 扫描；使用当前 language 创建新 text 快照，不修改游戏文件；get_task 查看覆盖率，搜索需等待发布。")]
    public Task<CallToolResult> BuildTextIndex(string? scope = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("build_text_index", new { scope }, cancellationToken);

    [McpServerTool(Name = "search_text", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("已发布 text 快照查询；三字以上使用 FTS5 trigram、短词扫描，按 limit 分页；同显示文本不同来源保留身份，不执行用户关键词。")]
    public Task<CallToolResult> SearchText(string keyword, string? scope = null, string? sourceKind = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("search_text", new { keyword, scope, sourceKind, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "build_reference_index", ReadOnly = false, OpenWorld = false, Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("高成本后台逐包读取 import/export 元数据并发布 reference 快照；不改游戏文件；失败边和覆盖率通过 get_task/结果报告，不冒充对象调用图。")]
    public Task<CallToolResult> BuildReferenceIndex(string? scope = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("build_reference_index", new { scope }, cancellationToken);

    [McpServerTool(Name = "find_references", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("已发布 reference 快照上的包级查询；需要对应快照，按 limit 分页；零结果不等于运行时绝无引用，coverage 标明 indexedScope。")]
    public Task<CallToolResult> FindReferences(string path, string direction, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("find_references", new { path, direction, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "export_raw", ReadOnly = false, OpenWorld = false, Idempotent = false, Destructive = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("高成本后台读取并解密/解压有效包及同来源伴随文件；需要有效包和 outputRoot 下相对 outDir，会写入 manifest 后原子提交且不覆盖已有目录；结果只返回清单路径，按预算失败并清理未提交目录。")]
    public Task<CallToolResult> ExportRaw(string path, string outDir, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("export_raw", new { path, outDir }, cancellationToken);

    [McpServerTool(Name = "export_json", ReadOnly = false, OpenWorld = false, Idempotent = false, Destructive = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("高成本后台逐 export 流式写 CUE4Parse JSON；需要有效包和 outputRoot 下相对 outDir，会原子提交且不覆盖已有目录；不截断 JSON，选定对象/脚本失败即整次失败并清理未提交目录。")]
    public Task<CallToolResult> ExportJson(string path, string outDir, int? exportIndex = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("export_json", new { path, exportIndex, outDir }, cancellationToken);

    [McpServerTool(Name = "get_task", ReadOnly = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("低成本控制面查询；需要 taskId，解析繁忙时仍可应答；任务错误列表用 includeErrors/limit/cursor 分页，游标绑定错误版本。")]
    public Task<CallToolResult> GetTask(string taskId, bool includeErrors = false, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("get_task", new { taskId, includeErrors, limit, cursor }, cancellationToken);

    [McpServerTool(Name = "cancel_task", ReadOnly = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope)), Description("低成本控制面操作；需要 taskId，仅请求后台任务在包/写入安全点取消，不撤销已提交成果；返回当前状态，不能把 cancelling 冒充 completed。")]
    public Task<CallToolResult> CancelTask(string taskId, CancellationToken cancellationToken = default)
        => _runtime.CallAsync("cancel_task", new { taskId }, cancellationToken);
}
