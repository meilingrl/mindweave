# Mindweave 仓库规则

项目是什么、先读哪些文档，见 [README](README.md)。本文件只说明分支和双轨开发的约定。

## 分支

| 分支 | 用途 | 本机工作目录 |
| --- | --- | --- |
| `main` | 共享基线：现行需求、架构、审查与原型。只放两条轨道都要遵守的内容，不放产品代码 | `Projects/mindweave/` |
| `track/codex-steady` | 稳健轨：用户与 Codex 协作推进，是课程提交版本 | `Projects/mindweave.worktrees/codex-steady/` |
| `track/claude-auto` | 激进轨：Claude 自主开发，用于提前暴露问题、积累经验 | `Projects/mindweave.worktrees/claude-auto/` |

标签 `baseline-v0.11` 标出两条轨道共同的起点（需求 v0.11，已采纳详细设计就绪审查 R4）。

开始工作前先运行 `git branch --show-current` 确认所在分支，开发分支再读根目录的 `TRACK.md`；`main` 的路线入口见 [开发路线](docs/development-routes.md)。

## 合并方向

- 只能 `main` → 轨道分支。需求或架构的更正先提交到 `main`，各轨道按需合并。
- 轨道之间不合并，轨道也不合并回 `main`。
- 需要参考另一条轨道时，只读，例如 `git show track/claude-auto:TRACK.md`，可按相关任务需要查看发现、证据或代码并独立验证；只读检查不等于借用代码。借用另一条轨道的代码须经用户决定。

## 改动需求或架构

两条轨道都以 `main` 上的 `docs/requirements.md` 和 `docs/architecture.md` 为准。开发中发现文档有错或缺失时，记入本轨道的 `TRACK.md`，由用户决定是否更正 `main`；不要只在轨道分支上悄悄改这两份文档。

## 文件归属

现行设计与路线在 `docs/`，历史在 `docs/history/`，审查批次在 `docs/reviews/`，独立原型在 `prototypes/`。正式代码、测试与脚本在开发分支按需放入 `src/`、`tests/`、`scripts/`，不预建空目录。各轨道以 `TRACK.md` 记录状态、下一步和重要发现。运行数据、单实例标识与凭据命名空间按轨道隔离，实施前不得声称已完成隔离。
