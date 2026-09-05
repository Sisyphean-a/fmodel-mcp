---
scope: package:fmodel-mcp
status: design-baseline
---

# fmodel-mcp 产品包

一个面向 Windows x64 本机的黑神话悟空逻辑 MOD 静态研究工具。该页描述设计中的产品范围；当前仓库尚未创建对应的可运行包。

## 当前实现状态

- `package.json` 当前为空对象，`package-lock.json` 没有包依赖；仓库没有 `src/`、`tests/`、`.csproj` 或可运行入口。
- `docs/architecture.md` 的“建议源码布局”是未来目标，不是已创建目录：目标为一个 C#/.NET 10 产品项目和一个测试项目。
- 当前没有编译、MCP 握手、挂载、解密、解析或导出证据；`docs/development-plan.md` 将依赖和真实样本探针列为 P0 阻塞项。

## 设计职责与边界

- 默认进程是 MCP Host，使用官方 MCP SDK 的 stdio transport；同一可执行文件的 `--worker` 模式负责单游戏解析。
- Host 负责 MCP schema、配置校验/持久化、结果映射和 Worker 监督；Worker 负责挂载、有效文件目录、对象读取、索引和导出。
- Host 与 Worker 通过匿名管道传递有界 DTO/诊断，不传原始包或完整导出 JSON；Worker 日志走 stderr，协议 stdout 不混入日志。
- 一个实例同一时刻只加载一款游戏，最多一个解析 Worker。更换配置或 remount 通过替换 Worker 建立新会话，不隐式恢复旧会话。
- 游戏目录只读；导出只能提交到批准的 outputRoot。v1 面向 pak，IoStore 只做明确的不支持诊断，不做运行时注入、Hook、游戏内存访问、MOD 安装或自动联网修复。

## 计划公开边界

设计契约规划 24 个 snake_case 工具，分为：配置/诊断、文件/目录/资产定位、对象/表格/类/函数/文本/曲线读取、文本与包依赖索引、原始/JSON 导出和任务控制。完整输入、输出、分页和错误码只在 [MCP 工具契约](../../../docs/tool-contracts.md) 维护；这些工具当前尚未注册。

## 必须保持的不变量

- 文件、包和对象使用 Provider 虚拟路径及明确身份；物理条目与有效文件分开记录。有效文件按实际 `ReadOrder` 选择，同级最高优先级冲突不得猜赢家。
- 前台读取与后台索引共享单解析执行器；控制面仍可读取状态和任务快照。同步解析无法被假装为即时取消，超时/失控由 Host 回收 Worker。
- 查询只读已发布的不可变索引快照；快照覆盖率、失败清单和领域完整性独立于外层 `ok`，不能用空结果或截断伪装完整。
- 原始包和必要伴随文件必须保持来源一致；导出使用同卷临时目录和不覆盖的原子提交。

## 目标代码锚点（未创建）

`docs/architecture.md` 的目标布局为 `src/FModelMcp/Program.cs`、`Host/`、`Contracts/`、`Worker/`、`Parsing/`、`Indexing/`、`Exporting/`、`third_party/CUE4Parse/`、`config/` 和 `tests/FModelMcp.Tests/`。这些路径只用于后续实现定位，当前不存在，不能作为已验证代码锚点。

## 依据

- [产品架构设计](../../../docs/architecture.md)
- [解析、索引与导出设计](../../../docs/parser-and-indexes.md)
- [开发交接与验收](../../../docs/development-plan.md)
