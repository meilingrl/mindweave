# 结构审查：过长文件与职责混杂（2026-11-06）

起因：你 2026-11-06 看代码后认为部分文件过长、职责过杂，要求先查 `GenerationCoordinator.cs`，再查其他文件与测试代码。三路只读分析（英文原文），结论经抽查核实：

- [coordinator.md](coordinator.md)：生成协调器的职责图与拆分步骤（调度核心约 420 行保留，其余移出）。
- [product-files.md](product-files.md)：其他大文件的排序与拆分方案；哪些可以保持原样。
- [tests.md](tests.md)：测试代码：主要问题是约 85 处重复辅助方法（部分已漂移）、测试类被当作工具库、`ChatHarness` 重复一份服务装配、`FakeChatClient` 开关不一致。

执行顺序与结果记在 TRACK.md 与阶段报告。所有步骤只改结构、不改行为，每步全量测试通过且测试数不变。
