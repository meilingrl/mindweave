# S4 知识总结协调器审查 · 2026-10-27

初版b530d22发现I1；622de68保留未知接受的退出停止意图，限定复审已关闭，无新问题。独立调度测试修正1a7b5bc后最终完整560通过／2原有跳过、build零警告错误。以下分别保留初审与限定复审，非原生／服务验收。

## 初审

# Task 5 independent review

## Spec Compliance

- ❌ Issues found: unknown accepted operations lose shutdown cancellation intent after failed reconciliation and Return; the next explicit persistence repair can automatically start an old task. `src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.Persistence.cs:31,143` and `src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.cs:139`.
- Reviewed fixed BASE `4f44c283277c69fcc578758bffceccaa8d0c79b2` → HEAD `b530d2204b7dfce9674a06791bc87d885d553ea1`, seven owned files, using the supplied diff package in bounded consecutive sections. All task-owned file categories have corresponding changes; the session owns the new coordinator without requiring a separate CompositionRoot change (`src/Mindweave.Desktop/Services/DesktopChatSession.cs:19`).
- ⚠️ Naming/current-membership revalidation, Summary UI, native Avalonia, Windows, supplier behavior and product/course acceptance are later boundaries; this task does not claim them. The committed DTO exposes IsCandidate through Result, and session initialization currently covers Chat and KnowledgeSummary only (`src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.cs:11`; `src/Mindweave.Desktop/Services/DesktopChatSession.cs:29`).

## Strengths

- Complete output requires normal enumeration and disposal, a stop marker, bounded strict parsing and model validation before a completion identity is retained; failure paths produce terminal metadata, not partial versions (`src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.Execution.cs:57-83`). Tests exercise truncation, empty/invalid/no-stop, post-stop faults and disposal failure (`tests/Mindweave.Tests/KnowledgeSummaryCoordinatorTests.cs:48`).
- Completion repair retains the exact result request and delegates authoritative replay to storage; successful completion clears retained content and publishes an identity event. Stop preserves a completion already handed off rather than falsely reporting no committed output (`src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.Persistence.cs:54-68`; `src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.cs:159`; `tests/Mindweave.Tests/KnowledgeSummaryCoordinatorTests.cs:93,138,175`).
- Shared acceptance is acquired before Chat flush and input/configuration acceptance; frozen prepared requests use frozen credential references. Later Reply priority and fixed source/configuration are covered with real storage and controlled transport (`src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.cs:84-110`; `src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.Execution.cs:38`; `tests/Mindweave.Tests/KnowledgeSummaryCoordinatorTests.cs:60,78`).
- Failure repair captures and clears only the participant epoch, verifies retained configuration/preparation read failures, and does not clone another participant's failure into its own source (`src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.Persistence.cs:13,36-47,74`; `tests/Mindweave.Tests/KnowledgeSummaryCoordinatorTests.cs:149,261`).
- Execution tasks remain tracked beyond visible terminal status; immediate Stop cancels while actual stream/disposal tails remain awaited. Session initialization recovers both implemented job families before workbench exposure, and successful aggregate shutdown precedes shared dependency disposal (`src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.cs:47`; `src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.Persistence.cs:149-152`; `src/Mindweave.Desktop/Services/DesktopChatSession.cs:29-42`; `tests/Mindweave.Tests/KnowledgeSummaryCoordinatorTests.cs:123,192,224`).

## Issues

### Critical (Must Fix)

- None.

### Important (Should Fix)

- **I1 — Preserve shutdown cancellation on unresolved acceptance independently of current admission state.** `src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.Persistence.cs:31,143-150`; `src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.cs:110,139,173`.
  - Trigger: acceptance commits but its acknowledgement is lost, leaving an operation in `unknownAcceptances`. Confirmed shutdown sets `accepting=false` and stops only the jobs already in `jobs`. If `FindKnowledgeSummaryOperationAsync` throws during that shutdown flush, the unknown operation remains unresolved and the close fails.
  - Returning to the workbench invokes runtime ResumeAccepting, which sets the coordinator's `accepting=true`. Later successful save repair finds the durable Queued operation; reconciliation calls AddJob and uses only the *current* `!accepting` value to decide whether to Stop/Cancel it. It therefore adopts the old operation as active, removes the intent, clears the failure epoch and dispatches an AI request automatically.
  - This violates confirmed shutdown cancellation and the distinction between repairing a durable outcome and resuming a stopped task. It is a real retained-state path, not native UI uncertainty: `src/Mindweave.Desktop/ViewModels/MainViewModel.cs:194` calls runtime ResumeAccepting; `src/Mindweave.Application/Generation/AppGenerationRuntime.cs:90-95` resumes every participant; `src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.Execution.cs:13-18` then dispatches the recovered active job.
  - Fix: record a sticky shutdown/stop intent on each unresolved acceptance (or an equivalent acceptance-generation cancellation boundary) while shutdown holds acceptance ownership. Reconciliation must honor that intent even after Return reopens admission for future work; only a newly explicit accepted operation should run.
  - Add one deterministic regression: commit-then-lose acceptance acknowledgement; fail operation lookup during shutdown; Return/ResumeAccepting; restore lookup and retry persistence; assert the discovered operation is terminal, no AI call starts, no summary version is created and a later explicit new operation can be accepted. Existing lost-acceptance and failed-close tests cover these pieces separately but not their combination (`tests/Mindweave.Tests/KnowledgeSummaryCoordinatorTests.cs:109,224`).

### Minor (Nice to Have)

- None added by this task. The controller's deferred Task 4 scheduler test counter issue is unchanged and excluded from this task's counts.

## Focused checks outside the diff

- **Authoritative CAS/replay, including deleted output:** inspected `SqliteWorkspaceStore.SummaryJobs.cs:16-19,53-76` and `SqliteWorkspaceStore.SummaryCompletion.cs:23-38,72-76`. Completion replay checks exact prior revision/content/model and returns original IDs without looking up or recreating deleted bodies. Terminal metadata rejects completed rows, and coordinator conflict reconciliation reads the durable winner.
- **Global acceptance/repair ownership and failure epochs:** inspected `AppGenerationRuntime.cs:40-95,108-130`, plus `GenerationCoordinator.cs:50-93,146-199` and `GenerationCoordinator.Persistence.cs:32-116`. Admission ownership is shared; repair pauses dispatch and clears only matching source epochs. The Chat pending flush used by acceptance is durable, and session startup does not recursively acquire the runtime admission lease.
- **Physical-slot and disposal ownership:** inspected `AiRequestScheduler.cs:21-48,73-96`. Running phases keep their physical slot when their logical work is disposed, until the phase callback including enumerator disposal finishes; completion persistence runs afterward in the coordinator.
- **Committed-event exceptions must not reclassify durable results:** inspected `SqliteWorkspaceStore.cs:169-183` for commit-before-ack ordering; the supplied diff shows coordinator state updated before queued notifications and per-observer exception isolation (`KnowledgeSummaryCoordinator.Persistence.cs:60-68,79-84`; `KnowledgeSummaryCoordinator.cs:165-171`). No store Saved-event callback exists in that transaction path.
- **Failed-close Return versus unresolved acceptance ownership:** inspected focused unchanged Return/close lifecycle code in Root and Main, required to resolve the lifecycle edge above. Root retains session/store after close failure (`RootViewModel.cs:87-95`), while Main Return reopens participant acceptance (`MainViewModel.cs:194`), establishing I1's production path. The supplied Root diff is cut off in its initialization cleanup; no unrelated changed implementation file was reread.

## Verification evidence

- No tests, build, package suite, stress loop, native app, network, supplier call, vault operation or real database operation was run by this reviewer. The identified lifecycle defect follows directly from the retained-state transitions and does not need a new run to establish it.
- Controller supplied exact-final raw-log verification: focused 20 passed; Release 558 passed, 0 failed, 2 original explicit credential-vault skips; build 0 warnings/errors. These agree with `task-5-report.md`; they are controller-verified automated evidence, not an independent rerun or native acceptance.
- Reviewed task brief, verbatim global constraints/controller rulings, implementer report and API handoff. Wrote only this report; source/index/branch state was not changed.

## Assessment

**Task quality: Needs fixes.**

**Counts: 0 Critical / 1 Important / 0 Minor.**

The separation between streaming, durable completion, repair and physical tails is sound in the covered paths. Unknown-acceptance cancellation must survive failed shutdown and Return before this task can be trusted as the app-owned lifecycle boundary.


## I1限定复审

**I1 — Preserve shutdown cancellation on unresolved acceptance independently of current admission state — ADDRESSED.** `src/Mindweave.Application/Summaries/KnowledgeSummaryCoordinator.Persistence.cs:133-140` captures unresolved operation IDs while holding acceptance ownership and the coordinator lock, before reconciliation can fail. `KnowledgeSummaryCoordinator.cs:29,141-144` keeps that stop set independent of reopened admission. Central adoption at `KnowledgeSummaryCoordinator.cs:152-159` applies Stop/Cancel before registering demand, so later repair cannot dispatch the old Queued operation. Reconciliation at `KnowledgeSummaryCoordinator.Persistence.cs:23-29,45-70` adopts the stopped job, flushes terminal metadata, then clears the captured failure epoch.

### Checks of the fix

- Missing-operation replay remains stopped: `KnowledgeSummaryCoordinator.Persistence.cs:29` clears resolved uncertainty without deleting its stop tombstone; `KnowledgeSummaryCoordinator.cs:96-100` returns Obsolete / SummaryOperationStopped before store acceptance when no known durable job exists. Known stopped jobs retain NoChange replay with their original record.
- A fresh operation ID is unaffected: only IDs captured from unknown acceptances during shutdown are tombstoned. `tests/Mindweave.Tests/KnowledgeSummaryCoordinatorTests.cs:125-150,157-173` covers lookup failure, Return, repair, zero old-operation requests/versions, both same-ID replay forms, and successful fresh requests.
- Valid retained completion remains repairable without resend: existing Stop retains `Completion` at `KnowledgeSummaryCoordinator.cs:164-169`; `KnowledgeSummaryCoordinator.Persistence.cs:49-65` retries that exact completion. The amended two-case regression at `tests/Mindweave.Tests/KnowledgeSummaryCoordinatorTests.cs:93-106` now inserts Return between failed shutdown and repair and still checks one request, one version and one distinct completion identity.
- Read the immutable supplied fix package once: BASE `b530d2204b7dfce9674a06791bc87d885d553ea1` to HEAD `622de681bdfae5a27f6d1116b364d1c02c5c1918`; three owned files, 73 insertions / 13 deletions. Checked the task brief, prior I1, report fix appendix, API semantic appendix and focused source context. No broad re-review.
- Inspected existing raw logs without rerunning tests: `task-5-fix1-red.log` shows the two regressions failing before the fix; `task-5-fix1-focused.log` shows 22 passed / 0 failed / 0 skipped; `task-5-fix1-build.log` shows 0 warnings / 0 errors.
- `task-5-fix1-release.log` shows 559 passed / 1 failed / 2 skipped. Its failure is the unchanged scheduler fixture's callback-order assertion, `AiRequestSchedulerTests.Two_queued_titles_cannot_hide_new_reply_and_capacity_is_shared:45` (expected reply, actual title1). That file is outside this immutable fix package and remains controller-owned. This review does not declare the full-suite gate passing.
- No tests/build, native app, user database, vault or network calls were run; no source, index or Git state was changed. Only this report was written.

### New Breakage in the Fix Diff

None. Counts: 0 Critical / 0 Important / 0 Minor.

### Out-of-Scope Observations

The existing full-suite scheduler fixture failure above remains a separate gate blocker; no additional out-of-scope review findings were sought or raised.

### Verdict

**Fix round: All findings addressed, no new Critical/Important breakage.** Addressed: I1. Open prior findings: none. Addressed/open counts: 1/0; new findings: 0. Whole-suite completion remains unproven by the recorded failing run.
