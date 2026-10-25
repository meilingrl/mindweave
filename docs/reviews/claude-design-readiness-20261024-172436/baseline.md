# 审查基线

**建立时间：** 2026-10-24 17:24:36 +08（Asia/Singapore 时区，与机器时钟一致）
**用途：** 记录本次审查读取的原始材料、版本与哈希，作为 [v1](v1/) 原样副本和最终完整性核对的依据。

## 1. 只读边界

- 本次审查只在本目录 `reviews/claude-design-readiness-20261024-172436/` 内写入。
- 项目根文档、既有复审目录 `reviews/review-20261023-023314-sgt/`、`prototypes/` 下全部代码、配置、结果与产物、用户数据均未修改。
- 未运行原型、构建、测试或任何付费 API；未读取密钥、Keychain 或应用数据目录。
- [v1](v1/) 是原样字节副本（`cp -p`），不作任何修改；其中的相对链接（如 `prototypes/...`、`../../../School/...`）按**原项目根目录**解释，在 v1 目录内点击不一定可达。

## 2. 审查对象（v1 原样复制）

| 原路径（相对项目根） | 文档版本 | 原文件修改时间 | 字节 | SHA-256 |
| --- | --- | --- | --- | --- |
| `initial-requirements.md` | v0.10 · 更新 2026-10-24 | 2026-10-24 14:50:21 | 31862 | `47b98d666f242a9729f1ff839d4bfce0b4b23f669333801f12679c6e3668cc6d` |
| `architecture-discussion.md` | 无版本号 · 更新 2026-10-24 | 2026-10-24 14:52:42 | 41727 | `b7dd701960ef09168010e3c07819af4bce483e26a9465df6ce71779dc8080616` |

v1 副本哈希与上表一致（复制后立即核对）。

## 3. 参照材料（只读，不复制）

| 路径 | 用途 | SHA-256 |
| --- | --- | --- |
| `README.md` | 项目入口、阅读顺序 | `f6b97763f7c3659ba994475b9efebda337896d229e1ca1cfccd22de49b110a8a` |
| `design-history.md` | 决策背景（不作为现行待办） | `846a3191dcb339e199b5f3e60786fabb528d9c3eb9f3d4e9666dc8edf785036e` |
| `reviews/review-20261023-023314-sgt/discussion-decisions.md` | D1–D8 用户确认原话与口径 | `04b0c3d30c33fea0c05ac41f8faacdc400a417c2388e5721333fc976d080787c` |
| `reviews/review-20261023-023314-sgt/README.md` | 冻结复审入口 | `82f21a227e889216441c2c1d780e5b9cd6b0ffa5bfcc85cd6297b6832f162f05` |
| `reviews/review-20261023-023314-sgt/ISSUES.md` | 既有 MW-01…MW-12，避免重复提出 | 见 `baseline-files.sha256` |
| `prototypes/avalonia-chat/README.md` | 聊天原型证据边界 | `7622ef6f7bcc977dacb1f2d20fc6ce737201ae96bb1e2fc0248c56275aca2350` |
| `prototypes/livemarkdown-chat/README.md` | 锚点实际含义与验证边界 | `b05f1395e1c4e0bb7eb3b0839a4da90fdd952b6b9936ee76c0a86991ac47a1ed` |
| `prototypes/network-layout/README.md` | 网络样例证据边界 | `9f0a86189b806d19c2f461de5be17b2c188047702d8904306ea3a6f943cf0208` |
| `prototypes/responses-compat/README.md` | 协议探针实测边界 | `da2ae1a9598161eda37e75fc08b61137c8c12902554afbeebc6a0661d6cce9a3` |

适用规则：已读 `/Users/meilingluo/AGENTS.md`（v0.6）。项目路径 `Developer/`、`Developer/Projects/`、`mindweave/` 各层未发现 `AGENTS.md`；`Developer/README.md`、`Developer/Projects/README.md` 已读作导航。

## 4. 全量完整性清单

[`baseline-files.sha256`](baseline-files.sha256) 记录建立基线时项目内 116 个文档、源码、配置和结果文件（排除 `bin/`、`obj/`、`artifacts/`、`__pycache__/` 构建或本机产物，以及本目录）的 SHA-256，供收尾核对“原始与冻结材料未变”。产物目录未纳入哈希，因为本次未读取也未触碰它们。
