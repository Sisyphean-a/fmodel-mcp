---
scope: workspace
status: implemented
---

# 架构索引

本仓库已实现 FModel MCP 的唯一产品包；`docs/` 是需求与验收权威，`.codestable/` 只保存当前态入口。新工作先从本索引进入，再按包范围加载；代码、测试、发布和真实内容证据仍需按对应验收范围单独核对。

## 作用域地图

- [package:fmodel-mcp](packages/fmodel-mcp.md)：唯一产品实现范围；当前实现、职责和目标边界由代码与现有设计文档共同约束。
- `shared/`：当前没有多个实现包或跨包共享机制的证据，不创建共享架构事实。
- [workspace 领域上下文](../requirements/CONTEXT.md)：工作区唯一已确认的业务语言、稳定规则和不变量。

## 当前架构入口

- [产品架构设计](../../docs/architecture.md)：产品边界、Host/Worker 运行模型、配置、生命周期、安全和资源预算。
- [MCP 工具契约](../../docs/tool-contracts.md)：计划中的 24 个工具、结果包络、分页和错误码；明确声明尚未注册或运行。
- [解析、索引与导出设计](../../docs/parser-and-indexes.md)：CUE4Parse 适配、目录覆盖、文本身份、SQLite 快照和导出事务。
- [开发交接与验收](../../docs/development-plan.md)：P0–P5 顺序、验证门禁和未验证事项；本文档的测试结果不替代未来实现验证。

## 状态边界

`docs/` 是长期可读的设计/交接资料；`.codestable/` 只保存按范围加载所需的当前事实和入口，不复制四份文档的完整契约。设计资料没有迁移为历史：它们不是旧任务、审查、探索或运行产物，且仍是当前设计依据。
