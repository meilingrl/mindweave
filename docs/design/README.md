# 激进轨详细设计

本目录只属于 `track/claude-auto`，记录交接文档允许实现者自行细化的选择。产品行为以 `main` 上的[需求](../requirements.md)与[架构](../architecture.md)为准；这里与它们冲突时以它们为准，并在 [TRACK.md](../../TRACK.md) “待决定”中记录。

| 文档 | 内容 |
| --- | --- |
| [数据关系梳理](data-model.md) | 稳定身份、来源依赖、祖先冻结范围、版本归属、事务与删除边界；各阶段按迁移逐步落表 |
| [S0 骨架与存储](s0-skeleton-storage.md) | 工程与依赖、数据目录与轨道隔离、单实例、SQLite 连接／迁移／备份／恢复、三层导航与 ViewLocator |
| [决策记录](decisions.md) | 交接 §4 授权实现者决定的参数与库选择，一条一段 |
