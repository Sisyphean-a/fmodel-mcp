---
scope: workspace
status: design-baseline
---

# 架构索引

本仓库是一个尚未实施的 FModel MCP 设计基线：业务/构建内容只有 `docs/`、空的 `package.json` 和无依赖的 `package-lock.json`；`.codestable/` 是本次初始化的项目记忆，除此之外没有源码、测试项目或可运行入口。新工作先从本索引进入，再按包范围加载；不要把设计中的目标路径或验收命令当成现有代码。

## 作用域地图

- [package:fmodel-mcp](packages/fmodel-mcp.md)：唯一可识别的产品实现范围；当前尚无实现，职责和目标边界来自现有设计文档。
- `shared/`：当前没有多个实现包或跨包共享机制的证据，不创建共享架构事实。
- [workspace 领域上下文](../requirements/CONTEXT.md)：工作区唯一已确认的业务语言、稳定规则和不变量。

## 当前架构入口

- [产品架构设计](../../docs/architecture.md)：产品边界、Host/Worker 运行模型、配置、生命周期、安全和资源预算。
- [MCP 工具契约](../../docs/tool-contracts.md)：计划中的 24 个工具、结果包络、分页和错误码；明确声明尚未注册或运行。
- [解析、索引与导出设计](../../docs/parser-and-indexes.md)：CUE4Parse 适配、目录覆盖、文本身份、SQLite 快照和导出事务。
- [开发交接与验收](../../docs/development-plan.md)：P0–P5 顺序、验证门禁和未验证事项；本文档的测试结果不替代未来实现验证。

## 状态边界

`docs/` 是长期可读的设计/交接资料；`.codestable/` 只保存按范围加载所需的当前事实和入口，不复制四份文档的完整契约。设计资料没有迁移为历史：它们不是旧任务、审查、探索或运行产物，且仍是当前设计依据。
