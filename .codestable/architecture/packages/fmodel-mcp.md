---
scope: package:fmodel-mcp
status: implemented
---

# fmodel-mcp 产品包

面向 Windows x64 本机的黑神话悟空逻辑 MOD 静态研究工具。实现基于 .NET 10、官方 ModelContextProtocol SDK 和本地 CUE4Parse 源码快照。

## 当前实现状态

- `src/FModelMcp/FModelMcp.csproj` 是 net10.0 可执行项目；默认进程是 MCP stdio Host，同一可执行文件的 `--worker` 模式运行解析 Worker。
- `tests/FModelMcp.Tests` 提供契约、配置、SQLite FTS5、快照/任务恢复和官方 MCP 子进程握手测试；`config/blackmyth.example.json` 不含密钥。
- `third_party/CUE4Parse` 为锁定的本地依赖，包含 Windows x64 `CUE4Parse-Natives.dll` 构建；Oodle 能力仍按运行时初始化和 `IsFeatureAvailable` 判定，不以 DLL 存在冒充可用。
- 发布命令为 `dotnet publish src/FModelMcp/FModelMcp.csproj -c Release -r win-x64 --self-contained false -o .tmp/publish`；发布目录包含 `FModelMcp.exe` 与 native sidecar。
- 真实 Black Myth: Wukong AES/usmap 探针已证明 pak 挂载、映射、DataTable、UCurveFloat、UClass/UFunction、locres、资产/文本/引用索引和 raw/JSON 导出；当前 CUE4Parse-Natives 未启用可验证的 Oodle feature，即使显式本地 runtime DLL 也必须保持 `partial` 并报告 `NATIVE_DEPENDENCY_MISSING`。

## 设计职责与边界

- Host 负责 MCP schema、配置校验/持久化、结果映射和 Worker 监督；Worker 负责挂载、有效文件目录、对象读取、索引和导出。
- Host 与 Worker 通过有界 JSONL 匿名管道传递 DTO/诊断，不传原始包或完整导出 JSON；Worker 日志走 stderr，协议 stdout 不混入日志。
- 一个实例同一时刻只加载一款游戏，最多一个解析 Worker。更换配置或 remount 停止旧 Worker 后以新会话替换，不隐式恢复旧会话。
- 游戏目录只读；导出只能提交到批准的 outputRoot。v1 面向 pak，IoStore 只做明确的不支持诊断，不做运行时注入、Hook、游戏内存访问、MOD 安装或自动联网修复。

## 公开边界与实现锚点

- `src/FModelMcp/Host/FModelTools.cs` 暴露严格 24 个 snake_case 工具；`Contracts/Models.cs` 定义包络、配置、分页、coverage、诊断和 IPC DTO。
- `src/FModelMcp/Host/AppRuntime.cs` 与 `Worker/WorkerProtocol.cs` 实现 Host/Worker 生命周期、超时、取消、Job Object、内存/行预算和会话切换。
- `src/FModelMcp/Worker/WorkerEngine.cs` 实现 pak 目录、对象/表格/类/函数/文本/曲线读取、任务、分页、导出和 source fingerprint；`src/FModelMcp/Indexing/IndexStore.cs` 实现规范化 SQLite 快照、FTS5、资产/符号/文本/引用及持久任务。
- `third_party/CUE4Parse` 的上游补丁实现挂载失败诊断、usmap/Kismet 脚本完整性和严格 Oodle 解压尺寸校验。

## 必须保持的不变量

- 文件、包和对象使用 Provider 虚拟路径及明确身份；物理条目与有效文件分开记录。有效文件按实际 `ReadOrder` 选择，同级最高优先级冲突不得猜赢家。
- 前台读取与后台索引共享单解析执行器；控制面仍可读取状态和任务快照。同步解析无法被假装为即时取消，超时/失控由 Host 回收 Worker。
- 查询只读已发布的不可变索引快照；快照覆盖率、失败清单和领域完整性独立于外层 `ok`，不能用空结果或截断伪装完整。
- 原始包和必要伴随文件必须保持来源一致；导出使用同卷临时目录、manifest 和不覆盖的原子提交。
- AES 明文、真实本地配置、游戏原始资源和导出物只允许位于被忽略的 `.tmp/` 或用户本地路径，不得进入源码、测试、示例或项目记忆。

## 依据

- [产品架构设计](../../../docs/architecture.md)
- [MCP 工具契约](../../../docs/tool-contracts.md)
- [解析、索引与导出设计](../../../docs/parser-and-indexes.md)
- [开发交接与验收](../../../docs/development-plan.md)
