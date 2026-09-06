# FModel MCP

FModel MCP 是面向 Black Myth: Wukong 的 Windows x64 MCP stdio 服务。它使用官方 `ModelContextProtocol` SDK，在 Host 中监督隔离的 Worker；Worker 只读游戏目录，通过受限 JSONL 管道提供 pak 浏览、对象解析、索引、搜索和导出能力。

## 快速开始

前置条件：Windows x64、.NET SDK 10.0.400、游戏目录中的 pak 文件和与游戏版本匹配的 `Mappings.usmap`。v1 只支持 pak；`.utoc/.ucas` 会被识别并报告为不支持，不会冒充已挂载。

1. 复制 `config/blackmyth.example.json` 为本地配置，例如 `config/blackmyth.local.json`。
2. 填写绝对路径，并把 AES key 写入 `aesKey` 或 `aesKeys`。不要把真实 key 写入 Git 跟踪文件、命令行、日志或测试源码。
3. 按需设置 `nativeLibraries.oodlePath` 为已经存在且获准使用的本地 Oodle DLL。程序不会联网下载 Native/Oodle。
4. 使用 stdio MCP 客户端启动发布程序：

```json
{
  "mcpServers": {
    "fmodel": {
      "command": "E:\\mod\\fmodel-mcp\\.tmp\\publish\\FModelMcp.exe",
      "args": ["--config", "E:\\mod\\fmodel-mcp\\config\\blackmyth.local.json"]
    }
  }
}
```

发布程序也可直接启动：

```powershell
dotnet publish src/FModelMcp/FModelMcp.csproj -c Release -r win-x64 --self-contained false -o .tmp/publish
.tmp/publish/FModelMcp.exe --config C:\path\to\blackmyth.local.json
```

日志写入 stderr，stdout 仅用于 MCP 协议。`--worker` 是 Host 内部监督模式，不应直接配置给 MCP 客户端。

## 配置与安全边界

配置契约是 `schemaVersion: 1`，`ueVersion` 必须为 `GAME_BlackMythWukong`。`gameRoot`、`outputRoot`、`cacheRoot` 和 `usmap` 使用绝对路径；配置目录、缓存目录和导出目录会执行可读性、重解析点、包含关系和路径安全校验。游戏输入只读，导出只能落在 `outputRoot` 内。

同一 `gameRoot` 的会话独占 `cacheRoot` 锁；第二个会话返回 `CACHE_LOCKED`，不会删除或覆盖第一个会话的数据库。配置、AES、usmap 或 pak 内容改变后，旧 Worker/索引不会被静默复用，需要重新挂载。

## 构建、测试与发布

仓库用 `global.json` 固定 .NET SDK，并为应用和测试项目提交 NuGet lock file。推荐使用文档中的锁定流程：

```powershell
dotnet restore src/FModelMcp/FModelMcp.csproj --locked-mode
dotnet restore tests/FModelMcp.Tests/FModelMcp.Tests.csproj --locked-mode
dotnet test tests/FModelMcp.Tests/FModelMcp.Tests.csproj -c Release --filter "Category!=Integration"
dotnet test tests/FModelMcp.Tests/FModelMcp.Tests.csproj -c Release --filter "Category=Integration"
dotnet publish src/FModelMcp/FModelMcp.csproj -c Release -r win-x64 --self-contained false -o .tmp/publish
```

真实游戏 Integration 测试需要设置被忽略的 `FMODEL_TEST_CONFIG`；没有本地配置时只运行不依赖游戏的契约测试。发布目录应放在 `.tmp/` 或其他本地忽略目录，游戏原始资源和导出结果不进入测试目录。

## 当前限制

- 当前实现按 v1 契约只处理 pak。IoStore 容器会保留 `UNSUPPORTED_CONTAINER`/unsupported 统计。
- Oodle 初始化是离线、显式、本地路径模式。发布物携带 `CUE4Parse-Natives.dll`，但 Native DLL 存在不等于 Oodle 可用；当前环境没有可用于构建/验证 CUE4Parse-Natives Oodle feature 的 SDK/core，显式本地 Oodle DLL 探针仍报告 `NATIVE_DEPENDENCY_MISSING`，不会伪造压缩内容成功。
- 解析、索引和导出都有大小、深度、读取、超时和取消预算；部分索引会发布带覆盖率和错误清单的 partial snapshot，而取消或源变更会丢弃未发布快照。
- 导出使用 exportId 目录、manifest、源 archive provenance、字节数和 SHA-256，并通过同卷原子提交；未提交的失败/取消目录会清理，已提交成果不会被失败清理删除。

完整工具契约、解析边界、SQLite schema 和验收矩阵见：

- [`docs/architecture.md`](docs/architecture.md)
- [`docs/tool-contracts.md`](docs/tool-contracts.md)
- [`docs/parser-and-indexes.md`](docs/parser-and-indexes.md)
- [`docs/development-plan.md`](docs/development-plan.md)
