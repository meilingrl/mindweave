# S4 同名组命名与生命周期审查 · 2026-10-27

初版e68b844发现I1；257e111保留被拒绝替换意图与前驱的条件清理归属，限定复审已关闭，无新问题。最终36聚焦／596完整通过、2原有跳过、build零警告错误。仅软件任务边界，名称语义与原生验收另行验证。

## 初审

# Task 6 independent spec and quality review

## Spec Compliance

- ❌ Issues found: **I1 — a typed rejection of a replacement intent loses ownership of the previous durable pending intent**, so successful shutdown can leave that intent behind. This misses the pending-intent reconciliation/cleanup contract. Evidence: `src/Mindweave.Application/Summaries/GroupNamingCoordinator.cs:79–84`; `src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryNaming.Prepared.cs:15`; `src/Mindweave.Application/Summaries/GroupNamingCoordinator.Persistence.cs:18–44,102–114`.
- Reviewed fixed BASE `d25567c77deeeecb36ebdf187837609d206abd11` → HEAD `e68b84426110fc9b63882b799279959ad6f922c3`, using the complete 94,220-byte, 14-file package `review-d25567c..e68b844.diff`, read once in bounded sections. Brief, binding review constraints, implementation report and API handoff were read. All listed source/test responsibilities have corresponding hunks; no unrelated product feature or schema change appears in the package.
- ⚠️ Task 7/8 durable adopt/restore/manual-version/fallback triggers remain future integration work as explicitly authorized. The present session wiring handles generated current commits and excludes candidates (`src/Mindweave.Desktop/Services/DesktopChatSession.cs:42–47`). Native interaction, real-provider grounding/suffix quality, Windows, course and release acceptance are not established by this review or the synthetic tests.

## Strengths

- Whole-group identity is checked against canonical snapshot JSON and every ordered immutable member row, including manual members. Preparation reconstructs current full content and actual display names, persists the request hash at the committed revision, and rejects stale material. Completion checks prepared metadata before applying any name; completed replay uses the original output hash/revision without trying to reconstruct pre-rename names (`src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryNaming.cs:13–35,64–79,103–145`; `src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryNaming.Prepared.cs:45–64`). The completion transaction updates display/name revisions only and retains small IDs/hashes, not summary bodies.
- Automatic targets are exactly the nonmanual subset while protected manual nodes remain part of the complete CAS. Strict parsing rejects duplicate/unknown fields, invalid identities and invalid names; equal names are permitted (`src/Mindweave.Application/Summaries/NodeNamingProtocol.cs:23–61`). The current helper preserves manual/distinguished display names and advances nameRevision only when the display text changes (`src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryVersions.cs:78–87`).
- Physical work uses the shared background scheduler phase through stream exhaustion, asynchronous enumerator disposal and strict parsing; prepared/terminal writes are outside that phase (`src/Mindweave.Application/Summaries/GroupNamingCoordinator.Execution.cs:12–49`). Exact completed-response repair, unknown acceptance and sticky canceled intents are explicitly retained (`src/Mindweave.Application/Summaries/GroupNamingCoordinator.Persistence.cs:21–70,102–114`). Session recovery and disposal use one app-owned participant (`src/Mindweave.Desktop/Services/DesktopChatSession.cs:20–25,34–40,50–58`).
- Added tests exercise real temporary SQLite transactions and controlled transport: atomic rollback, manual/member/current/deletion staleness, exact prepared metadata, before/after-write uncertainty, failed close/Return, delayed disposal and bounded follow-up (`tests/Mindweave.Tests/SummaryNamingStorageTests.cs:31–113`; `tests/Mindweave.Tests/SummaryNamingStorageTests.Edges.cs:11–75`; `tests/Mindweave.Tests/GroupNamingCoordinatorTests.cs:11–55`; `tests/Mindweave.Tests/GroupNamingCoordinatorTests.Lifecycle.cs:14–70`).

## Issues

### Critical (Must Fix)

- None.

### Important (Should Fix)

**I1 — retain and reconcile the replaced pending identity when the latest save is normally rejected.**

`src/Mindweave.Application/Summaries/GroupNamingCoordinator.cs:79–84` replaces `group.Pending` with a new object carrying `ReplacedOperation`, but any non-success result from `SaveNodeNamingIntentAsync` immediately sets `group.Pending = null`. The store's normal `Obsolete` branch at `src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryNaming.Prepared.cs:15` returns before changing the pending columns. Therefore the previous durable operation remains, while its only local cleanup identity is discarded.

A concrete allowed interleaving is: job J is active; pending P is durably saved; another durable current change starts notification Q; Q reads the current group and replaces the local pending object; a manual display-name edit occurs before Q's save and changes the group epoch. Q's save returns `Obsolete`. Manual display-name edits intentionally do not trigger another naming notification. P remains in SQLite, but the coordinator no longer tracks P or Q. When J finishes stale, `FinishGroupAsync` sees no pending object and removes the group (`src/Mindweave.Application/Summaries/GroupNamingCoordinator.Execution.cs:75–90`).

The observable impact is an orphan durable pending intent: after the tails settle, `HasActiveOrPendingWork` can be false (`src/Mindweave.Application/Summaries/GroupNamingCoordinator.cs:29`), explicit repair examines only groups with a local pending object (`src/Mindweave.Application/Summaries/GroupNamingCoordinator.Persistence.cs:18–44`), and shutdown marks/clears only those tracked objects (`src/Mindweave.Application/Summaries/GroupNamingCoordinator.Persistence.cs:102–114`). Shutdown can report success while `ListNodeNamingIntentsAsync` still contains P. This is a deterministic ownership/cleanup violation, not a claim that the stale intent automatically calls the provider: startup recovery does clear all pending columns (`src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryNaming.cs:147–154`).

Keep the rejected replacement and its predecessor identity until identity-conditional cleanup succeeds, or explicitly clear the predecessor before removing the tracked state. The typed stale outcome remains an ordinary business result. If cleanup itself throws, retain the identities for explicit repair and report that IO failure through Naming's failure source. The existing unsaved-pending reconciliation at `src/Mindweave.Application/Summaries/GroupNamingCoordinator.Persistence.cs:37–44` already demonstrates the required predecessor cleanup for thrown saves; the typed rejection path needs the same ownership guarantee.

Suggested focused regression: while J is running and P is saved, pause Q immediately before the real store save, perform a manual display-name edit, then let Q return the real typed `Obsolete`. Finish J and assert no durable pending remains after idle/shutdown. Add a conditional-clear IO-failure variant and verify that failed close → Return → repair clears P without any additional transport call. Existing lifecycle tests at `tests/Mindweave.Tests/GroupNamingCoordinatorTests.Lifecycle.cs:61–70` cover thrown latest saves, not this typed-rejection branch. No test rerun was needed to establish the direct state loss.

### Minor (Nice to Have)

- None.

## Named cross-file checks and validation boundary

- **Shared admission/lock ordering and failure epochs:** inspected unchanged `src/Mindweave.Application/Generation/AppGenerationRuntime.cs:40–94,108–130` and `src/Mindweave.Application/Generation/AiRequestScheduler.cs:21–48,73–96`. Runtime releases its acceptance semaphore before awaiting participant shutdown; failure clearing is epoch-scoped; scheduler ownership keeps an active phase occupied until its delegate/disposal returns. Naming's bounded first-plus-one-follow-up path is in `GroupNamingCoordinator.Execution.cs:69–90`; no rename-completion self-trigger appears in the coordinator/session changes.
- **Current/name/group epoch interaction and deletion/replay:** inspected unchanged `src/Mindweave.Infrastructure/Storage/SummarySchema.cs:201–252` and `src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryDeletion.cs:15–38,44–73`. Current/version, name revision/manual flag, membership insertion/deletion invalidate the group; naming records retain small identity rows after body deletion; active jobs referencing deleted bodies become terminal in the same transaction. This supports stale rejection and nonresurrecting completed replay. No changed-file body was reread; source-line references were indexed from the reviewed package.
- **Strict name validation dependency:** inspected unchanged `src/Mindweave.Application/Summaries/KnowledgeSummaryProtocol.cs:23–61,82–86,90–130` to confirm the reused title validator enforces Unicode, controls/line separators, 160-scalar bounds and the existing 262,144-byte transient buffer.
- **Verification evidence:** the controller states it fully read the final raw logs for this exact head: 34 focused passes; Release 594 passes/2 original explicit vault skips; build 0 warnings/0 errors. These figures agree with `task-6-report.md:34–38`; this reviewer did not rerun tests/builds or independently claim another execution. Existing declared skips are not new test-output defects. No native app, user database, vault, network or supplier work was performed.
- **Write boundary:** only this requested review report was written. Existing controller changes in `TRACK.md` and `docs/implementation/s4-verification.md` were observed and preserved. No Git state mutation, additional reviewer, broad source crawl or suite/stress loop was performed.

## Assessment

**Task quality: Needs fixes.**

**Counts: 0 Critical / 1 Important / 0 Minor.**

The main protocol, whole-group transaction and lifecycle implementation are well covered within the task boundary. I1 must preserve durable-intent ownership through normal stale rejection before this task passes its spec/quality gate; the bounded focused regression above is sufficient for this finding.


## I1限定复审

- **I1 — retain and reconcile the replaced pending identity when the latest save is normally rejected — ADDRESSED.** `src/Mindweave.Application/Summaries/GroupNamingCoordinator.cs:79–84` keeps Q with its predecessor P and routes typed save rejection through `ClearPendingAsync`. `src/Mindweave.Application/Summaries/GroupNamingCoordinator.Persistence.cs:79–85` first marks Q canceled, awaits conditional clearing of P and Q, and releases only the identical local pending object after both acknowledgements. Any thrown clear leaves both IDs owned; `GroupNamingCoordinator.cs:88` reports the actual persistence failure. The normal typed result itself does not report a failure.
- **I1 lifecycle check — ADDRESSED.** Repair processes retained pending objects at `src/Mindweave.Application/Summaries/GroupNamingCoordinator.Persistence.cs:18–50`; absent uncertain saves and cancellation use the same helper. Shutdown stamps cancellation before IO at lines 113–120, while Return at `GroupNamingCoordinator.cs:141` does not reset the retained cancellation. An IO failure therefore remains repairable and cannot turn into acceptance or a new provider call. The failure is cleared only after successful owned repair at `GroupNamingCoordinator.Persistence.cs:69`; runtime Return preserves its failure source (`src/Mindweave.Application/Generation/AppGenerationRuntime.cs:90–103,123–129`).
- **Related fix-path check — no new breakage found.** Rejected acceptance now uses the same conditional cleanup (`src/Mindweave.Application/Summaries/GroupNamingCoordinator.cs:108–110`); finishing/capped/canceled work does likewise (`GroupNamingCoordinator.Execution.cs:77–88`). Failure leaves the pending object available even after Active is released. Successful serialized saves replace the durable slot before normal acceptance (`src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryNaming.Prepared.cs:19–23`); each clear compares the operation ID at lines 25–32, so an old clear cannot erase a newer durable intent. The helper's object-identity check cannot release a newer local pending object. No dispatch, scheduler, protocol, schema or public API code was added by the fix.
- **Regression check — covers I1 and its IO variant.** `tests/Mindweave.Tests/GroupNamingCoordinatorTests.Lifecycle.cs:77–119` holds J active, saves P, pauses Q before the real store call, performs a durable manual rename, then observes actual Obsolete from the underlying store. The successful branch asserts no global persistence failure; the failing-clear branch asserts retained P/work, failed shutdown, Return and explicit repair. Both assert empty durable pending after idle/shutdown, protected manual name and exactly one transport start. The wrapper awaits the real save and records its typed result at lines 138–145; Obsolete is not fabricated.
- **Evidence check — no tests rerun.** The implementer's appended report names the covering commands/raw logs and exact fix SHA at `.superpowers/sdd/s4-summary-plan/task-6-report.md:48–74`: RED 2 failures; regression 2 passes; focused 36 passes; full Release 596 passes/2 original explicit vault skips; build 0 warnings/0 errors. The controller explicitly confirms fully reading those raw outputs on the exact final source. These are existing execution evidence, not a new reviewer execution or native/provider acceptance claim.
- **Scope check — fixed BASE `e68b84426110fc9b63882b799279959ad6f922c3` → HEAD `257e11196957ce475a29d30818a7d9a51772e5b0`.** Read the immutable 17,705-byte four-file fix package once; reviewed the task brief, original I1, fix-report append and semantic-handoff append. Only the finding and fix paths/dependencies were checked; no broad re-review, suite/stress run, source/index/Git mutation, subagent, native app, user DB, real vault or network operation.
- **New breakage in the fix diff:** None. Critical 0; Important 0; Minor 0.
- **Out-of-scope observations:** None.
- **Counts:** Addressed 1; open 0; new findings 0.
- **Fix round verdict:** All findings addressed, no new Critical/Important breakage. This verdict is limited to Task 6 fix round 1 and does not establish native, supplier, Windows, course or release acceptance.
