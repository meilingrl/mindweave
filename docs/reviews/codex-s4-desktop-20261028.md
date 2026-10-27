# S4 桌面总结、编辑与阅读审查 · 2026-10-28

初版`a56af23`独立审查发现2项Important：保存前节点读取期间的新输入归属、在途阅读写入期间的可见性中断。修复`504598b`已由限定复审关闭2/2问题，无新问题；最终26聚焦／622完整通过、2原有跳过、构建零警告错误。初版20聚焦／616完整通过未覆盖这两个窗口，原始初审与失败证据保留。原生、真实服务、Windows与阶段整体结论仍独立。

## 初审

# Task 7 independent spec and quality review

## Spec compliance

- ❌ Issues found at fixed HEAD `a56af234e46e193a8adb5c6fc944ba90000e2157`, BASE `f80655e93d70dea95add67dbaf77b1682eabca38`: queued input preservation and continuous active-visible reading each have an uncovered asynchronous window (Important 1–2 below).
- ✅ The changed-file coverage matches this task: production session injection and navigation, list/detail/editor, actual rendered reading adapter, settings wording and focused tests. Task 8 mutation/confirmation controls are correctly left outside this task. Frozen scope plus honestly labeled version save time follows the controller ruling (`SummaryViewModel.cs:127`).
- ⚠️ Native visibility/platform behavior, Windows and course/release acceptance cannot be established from the diff/headless evidence. The newly added failed-close Return path (`MainViewModel.cs:216`, `SummaryViewModel.cs:167`) is visible in code but its resumed-reading behavior is not exercised by the two new close-drain tests; the controller should preserve this validation boundary. Tasks 1–6 backend/scheduler guarantees are not re-reviewed here; the controller separately supplied migration-copy evidence.

## Strengths

- `src/Mindweave.Desktop/ViewModels/MainViewModel.Summaries.cs:49` and `src/Mindweave.Desktop/ViewModels/SummaryViewModel.cs:86`: route and body-load ownership are both checked; body clearing and load stamps prevent stale asynchronous content from being presented as the selected new version.
- `src/Mindweave.Desktop/Markdown/SummaryReadingAdapter.cs:21` and `:40`: NFC scalar identity is mapped to original grapheme geometry and actual viewport intersections, including formatted text, rather than using Markdown length or scroll percentage.
- `src/Mindweave.Desktop/ViewModels/MainViewModel.cs:191`, `:374` and `src/Mindweave.Desktop/ViewModels/SummaryViewModel.cs:157`: tracked refreshes/version loads are drained during close. The held-read regressions and retained executable RED logs specifically support these lifecycle fixes instead of masking the earlier teardown failure with retries.
- `src/Mindweave.Desktop/ViewModels/SummaryDraftSession.cs:58`: draft write repair compares the exact attempted token/content before advancing to newer input; the lost-ack and post-submission queued-input tests exercise real temporary SQLite behavior.

## Issues

### Critical

- None identified.

### Important

1. **Queued input during the pre-commit node read is discarded.** `src/Mindweave.Desktop/ViewModels/SummaryDraftSession.cs:86–92`, `:133`. `CommitAsync` flushes the old text, then awaits `LoadSummaryNodeAsync`. A queued text change arriving during that await increments the local revision and changes `Title`/`Markdown`, but its debounce cannot acquire `writes`. The code then builds `pendingCommit` from this newer local text/revision while passing the older persisted `saved.Token` to `CommitSummaryDraftAsync`. The store commits the old draft; `FinishCommitAsync` sees the newer revision equal to `pending.LocalRevision` and clears `saved` plus the dirty revision. The new text is therefore neither in the committed version nor retained as a dirty draft, and the next Begin projects the old committed content over it. Lost acknowledgements in the same window also make reconciliation compare against text that was never submitted. Freeze the content and local revision associated with the actual persisted draft before any subsequent await, or flush/reconcile again under the same serialized operation before freezing the commit intent. Add a deterministic barrier specifically on the node load after Flush; change text there and assert either that exact text was committed or it remains a dirty draft against the new current. `tests/Mindweave.Tests/SummaryDesktopTests.cs:363` holds the later `CommitSummaryDraftAsync` call after `pendingCommit` already exists and does not cover this window.

2. **Visibility/activation interruptions are dropped while an observation is in flight.** `src/Mindweave.Desktop/Views/SummaryView.axaml.cs:35`, `:44`, `:71`. Window activation/deactivation and local visibility transitions all call `Tick -> ObserveNowAsync`; the first `observing` guard silently drops them while a reading store operation is awaited. For example, observe at t=0, hold the t=2 write, hide/deactivate then restore the page at t=20 before releasing that write. Both interruption edges are lost. The next active observation accumulates the entire t=2..20 gap and incorrectly marks a short document read, despite no continuous three-second visible interval. Record interruption independently of the coalesced observation lock and apply an inactive observation/reset before subsequent active evidence; also invalidate active snapshots sampled before such an interruption. Add a held-reading-write test with hide/show or deactivate/activate during the hold. The existing hidden-view test (`tests/Mindweave.Tests/SummaryDesktopTests.cs:326`) awaits each observation and does not cover an in-flight transition.

### Minor

- None required for this task gate.

## Evidence and bounded checks

- Reviewed the supplied 133180-byte package in ordered segments. One initially tool-truncated `MainViewModel.Summaries` section was re-read from the same package; no changed source file was separately re-read. Diff line-number anchors were calculated from hunk metadata.
- Named outside-diff risk: whether dropping visibility edges really carries elapsed dwell into the next sample. Checked only `src/Mindweave.Application/Summaries/SummaryReadPolicy.cs:47–54`; it adds elapsed time between qualifying observations and requires an interruption observation to reset continuity. This confirms Important 2 rather than speculating about policy behavior.
- Read the task brief, controller constraints, implementation report/API handoff, final focused/full/build results, retained disposal failure and both lifecycle RED logs. Final reported 20 focused passes, 616 full passes with two original credential skips, and clean build are consistent with those visible logs; they do not exercise Important 1–2. No suite/compiler/probe rerun was performed. Both proposed focused regressions were sent to the controller for the single-writer fix dispatch.

## Assessment

**Task quality: Needs fixes.**

The implementation covers the intended Task 7 surface and improves real close/drain behavior, but the two asynchronous windows break explicit user-input preservation and reading-state guarantees. Fix and prove those windows before accepting the task.


## I1／I2限定复审

- **I1 — Queued input during the pre-commit node read is discarded.** — **ADDRESSED**。`src/Mindweave.Desktop/ViewModels/SummaryDraftSession.cs:91` 在 node read 的 await 前，将 durable draft 的内容、token 和 persisted revision 固定为同一提交意图；`:96` 使用该意图，`:131` 检测后续输入并在新 current 上续租，未把后续文本误认成已提交。`tests/Mindweave.Tests/SummaryDesktopTests.cs:482` 的 barrier 位于 Flush 后的 node read，覆盖正常／丢失 acknowledgement；`:499`–`:503` 断言提交原 payload、保留新文本、两版本且一次通知。相邻 notification await 收尾也已检查：`SummaryDraftSession.cs:130` 在最终 dirty/rebase 决策之前等待，`:508` 的 held notification 测试断言后续输入持久化到新 current 的草稿。
- **I2 — Visibility/activation interruptions are dropped while an observation is in flight.** — **ADDRESSED**。`src/Mindweave.Desktop/Views/SummaryView.axaml.cs:47`、`:50`、`:51` 在 observation coalescing guard 前同步记录 hide/deactivation；`:83` 也记录 inactive sample。`src/Mindweave.Desktop/ViewModels/SummaryViewModel.Reading.cs:14` 不等待 I/O 即递增 interruption revision；`:45`–`:55` 先写 inactive empty reset，仅当 revision 未再变化才清除 pending reset，并将跨 interruption 的旧 active evidence 置为 inactive/empty。`SummaryViewModel.cs:166` 的 quiesce 使用同一路径。`tests/Mindweave.Tests/SummaryDesktopTests.cs:526` 在 t=2 store await 内执行 hide/show 至 t=20，断言恢复后 dwell=0、t=22 unread、t=23 read。Begin await 路径也已检查：`SummaryViewModel.Reading.cs:28` 保留中途 interruption；`SummaryView.axaml.cs:94`–`:98` 保留成功打开的同 version/session epoch 并丢弃旧 geometry；`:582` 的 held Begin 测试断言 Seen 为空、epoch 不重开、manual-unread suppression 保留。

### New Breakage in the Fix Diff

- **None** — 未发现 fix 引入的 Critical、Important 或 Minor 问题。
- **Failed-close Return 补证已满足本次检查项** — `tests/Mindweave.Tests/SummaryDesktopTests.cs:553` 使用实际编译 SummaryView、Root/Main 和临时 SQLite trigger；`:569`–`:575` 断言 close 失败后 quiesce、Return 恢复同一 active page、共享失败仍阻断、显式 repair 后 fresh dwell 为零且再过三秒才已读。报告如实注明该测试在 I2 修复前已通过，作为补证而非 RED。它不证明原生窗口事件交付。

### Out-of-Scope Observations

- **None** — 未新增 diff 外问题；原生／平台／真实服务与费用验收仍由控制器另行管理。

### Checks

- **固定审查范围已核对** — BASE `a56af234e46e193a8adb5c6fc944ba90000e2157`，HEAD `504598b49c924c906e75343a89a26e484cbb6120`；唯一 package `review-a56af23..504598b.diff` 分三段完整读一次，35599 bytes、5 files、196 insertions / 21 deletions；没有重新生成 git diff 或重做完整任务审查。
- **证据核对完成** — 已读 brief、constraints、fix1 context、report 的 fix1 追加和更新 API handoff；report 的 raw RED/GREEN 与新增 barrier 测试和修复代码一致。现有最终日志：26 focused passed；622 full passed、2 original credential skips、0 failed；build 0 warnings / 0 errors。上述运行结果来自现有报告及控制器的同源确认，本复审没有重跑 suite、stress loop 或额外测试。
- **写入边界** — 唯一写入是本复审报告；无 production/test/index/HEAD 修改，无 subagent、用户库/vault、HTTP/native、push 或 merge 操作。

### Verdict

- **Fix round: All findings addressed, no new Critical/Important breakage** — 2/2 findings ADDRESSED；0 open findings；0 new breakage；0 out-of-scope observations。
