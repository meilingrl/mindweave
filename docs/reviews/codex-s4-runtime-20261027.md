# Task 4 independent review

## Spec Compliance

- ✅ **Spec compliant for Task 4.** No missing or misunderstood task-scoped behavior found. Fixed review range: `84052d98c1390028c6ee04e8d5350dc937cf1a1d..c5c3692d34f6d2ffc5c4c28780686c577bcc0f44`; package: `review-84052d9..c5c3692.diff`. This is an infrastructure/existing-Chat gate, not S4 acceptance.
- Read the task brief, binding review constraints, controller context, implementation report and API handoff. Reviewed the complete 1,398-line package once in four bounded sections. The package has all three specified Generation files, Chat integration, Desktop session/Main/Root wiring, both new test files and the existing fixture extension. CompositionRoot need not change: Root already passes `chat.Coordinator` and `chat.Configuration` at `src/Mindweave.Desktop/ViewModels/RootViewModel.cs:64`.
- ⚠️ **CannotVerify — later tasks:** production Summary/Naming acceptance, frozen scope, result-only-in-memory, complete-version commit/repair without resend, and their session-owned cleanup are not implemented by Task 4. Task 5/6 must use the same runtime and implement the contract in `src/Mindweave.Application/Generation/GenerationRuntimeContracts.cs:5–12`; synthetic priorities/participants do not establish that production integration. Scope/storage behavior and migrations remain earlier/later-task evidence, not new proof from this diff.
- ⚠️ **CannotVerify — acceptance layers:** no native app, real user database, credential vault, supplier, Windows or course/release acceptance was performed. No S1/S3 leftover supplier allowance is consumed or implied. S1 extreme revision overflow and S2 viewport scanning remain outside this review.

## Strengths and requirement checks

- **One physical limit and visible priorities:** `src/Mindweave.Application/Generation/AiRequestScheduler.cs:20–49` dispatches while `running < 2`, sorts ready phases by priority/sequence, and checks non-running higher-priority demand. `GenerationRuntimeContracts.cs:3` fixes Reply/Summary/Background at 0/1/2. Chat offers every ready job, without a separate two-job limiter (`src/Mindweave.Application/Chat/GenerationCoordinator.Scheduling.cs:24–34`).
- **Atomic transition and ownership:** scheduler completion restores pending demand and decrements physical occupancy under the same lock, before dispatching another phase (`AiRequestScheduler.cs:36–45`). Handle disposal removes future demand but does not decrement a running lease (`AiRequestScheduler.cs:85–95`). Simultaneous ready/running offers are rejected and canceled queued offers never execute (`AiRequestScheduler.cs:52–83`). The dependency-gap test checks zero physical occupancy while retaining a higher-priority barrier (`tests/Mindweave.Tests/AiRequestSchedulerTests.cs:96–110`).
- **Live enumeration includes disposal:** Chat awaits its entire physical callback before releasing the scheduler phase (`GenerationCoordinator.Scheduling.cs:54–65`); `await foreach` and post-enumeration validation stay in that callback. The S3 compression boundary is sound: `GenerationCoordinator.Context.cs:99–146` only enumerates/disposes and validates/updates memory; cache/result persistence is in `FlushCompressionAsync` at `:153–187`, called through the persistence path after the phase, or by independent in-stream checkpoints. Checkpoints do not release a live phase.
- **Preparation and durable tails consume no physical permit:** readiness flush and credentials precede `RunPhaseAsync` (`GenerationCoordinator.Scheduling.cs:38–54`). Cancellation/terminal/global-pause checks occur before transport. The approved Queued-before-offer/Running-at-grant ordering is implemented, with a behavioral integration test in `tests/Mindweave.Tests/GenerationRuntimeTests.cs:132–155`. This does not claim disk Running precedes HTTP.
- **Logical demand covers acceptance and transitions:** Reply acceptance obtains the global lease and temporary Reply demand before asynchronous preparation, checks admission again before store acceptance, then registers the durable job demand before releasing temporary demand (`GenerationCoordinator.cs:104–143`, `:276–277`). Intermediate compression retains the same demand. Final terminal/Stop paths dispose demand while keeping execution tails tracked (`GenerationCoordinator.Scheduling.cs:68–90`; `GenerationCoordinator.cs:148–160`, `:200–224`).
- **Stop, shutdown and cleanup remain distinct:** Stop terminates/cancels/flushes without awaiting an uncooperative enumerator. Shutdown snapshots and awaits tracked executions then performs its final flush; failed flush propagates while the instance remains retained (`GenerationCoordinator.cs:148–160`, `:200–224`). Dispose subsequently cancels and awaits periodic/growth checkpoint tasks, disposes job handles/CTS, unsubscribes Runtime.Changed and unregisters only this participant (`GenerationCoordinator.cs:238–252`). Default-constructor disposal does not incorrectly tear down shared resources.
- **Failure ownership and epoch protection:** runtime sources clear only matching epochs (`src/Mindweave.Application/Generation/AppGenerationRuntime.cs:108–131`); Chat's local and external sources are separate (`GenerationCoordinator.cs:35–43`, `:174–198`). Flush captures the local epoch under its persistence serialization and does not clear a newer report (`GenerationCoordinator.Persistence.cs:34–35`, `:105–108`). Repeated reports advance epochs without repeating the failed-state notification (`AppGenerationRuntime.cs:116–121`). Independent-source/concurrent-new-failure coverage is in `GenerationRuntimeTests.cs:9–17`, `:73–89`.
- **Acceptance/repair/close:** held and waiting acceptance are counted; admission is rechecked after taking the semaphore (`AppGenerationRuntime.cs:40–53`). Repair holds the global pause while external repair and participant repairs run (`:55–73`). Shutdown closes admission before draining acceptance and invokes every participant even if one throws synchronously (`:74–89`); Return rejects an active drain (`:90–96`). The two dedicated lifecycle tests cover these latter cases (`GenerationRuntimeTests.cs:158–181`). The API explicitly leaves external-callback failure-source reporting to that callback; Chat's wrapper performs it.
- **Desktop dependency order:** Root uses aggregate work for close confirmation and aggregate shutdown plus draft repair before disposal (`src/Mindweave.Desktop/ViewModels/RootViewModel.cs:83–90`). Session drains runtime, disposes the coordinator/runtime, and only then its HTTP owner (`src/Mindweave.Desktop/Services/DesktopChatSession.cs:25–31`); Root disposes the store afterward. Main Return reopens the runtime and draft save clears only its captured external source (`src/Mindweave.Desktop/ViewModels/MainViewModel.cs:179–201`).
- **Lock-order check:** aggregate participant properties are read after releasing the runtime lock (`AppGenerationRuntime.cs:16–24`); Resume callbacks and notifications run outside the owner lock (`:90–96`, `:104–107`, `:116–131`); unregistration only removes the list entry (`:34`). Scheduler callbacks execute via Task.Run, and phase completions use RunContinuationsAsynchronously (`AiRequestScheduler.cs:30–47`, `:103`). No new runtime/scheduler-to-Chat callback inversion was found against Chat's sync-to-runtime/scheduler access.
- **Scope and structure:** the three new runtime files separate arbitration, aggregate lifecycle and participant contracts; none references Avalonia/SQLite. The package changes no SQL/schema, S3 serialization/protocol, dependency or shared design files. Existing coordinator assertions are not weakened; its fixture only gains optional runtime injection (`tests/Mindweave.Tests/GenerationCoordinatorTests.cs:505–518`).

## Issues

### Critical — 0

None.

### Important — 0

None.

### Minor — 1

1. **Make the two-title test's peak counter atomic.** `tests/Mindweave.Tests/AiRequestSchedulerTests.cs:32` assigns `peak = Math.Max(peak, n)` from two concurrent physical callbacks. A callback with `n == 1` can read the old peak, pause, then overwrite the other callback's `peak == 2` with 1. The final assertion at `:45` can therefore fail despite correct shared capacity. Use the Interlocked.CompareExchange maximum already present in the later all-four-kinds test (`:72–73`), or protect the peak update with a lock. This is a nonblocking test-flakiness defect; the later test independently checks the same two-slot ceiling with an atomic peak. No stress loop or rerun is needed to establish this read/modify/write race.

## Checks and evidence boundary

- No test suite, focused test, build, native operation, network/supplier call, vault access, source edit, index change or branch mutation was performed. The controller has read the final exact-source raw logs: focused **68 passed**, Release **538 passed / 2 original explicit skips / 0 failed**, build **0 warnings / 0 errors**. Those are controller-verified run evidence, not a new reviewer run. The native vault skip is not a vault test pass.
- RED chronology in the implementation report correctly distinguishes missing-type compilation, executable pre-fix lifecycle/title failures, and later priority/local-cap mutations. The prospective local-cap mutation is not presented as an original Chat-only baseline defect.
- **Named focused context completions:** the diff cuts the S3 preparation/compression function and persistent-checkpoint implementation; read `GenerationCoordinator.Context.cs:8–189` and `GenerationCoordinator.Persistence.cs:10–118` to resolve the concrete risk of holding a physical lease during final cache persistence or losing checkpoint tails. Read the cut acceptance/resume bodies (`GenerationCoordinator.cs:104–143`, `:227–235`) and title acceptance body (`GenerationCoordinator.Scheduling.cs:147–194`) to check rechecks, handle transfer and reopening behavior. A targeted Root reference check established existing CompositionRoot injection. An initial guessed `Navigation/CompositionRoot.cs` path did not exist; no broader traversal followed. No other unchanged-code exploration was needed.
- Existing report destination was checked before writing. This report is the sole requested checkout write; task evidence remains in use for controller review/API handoff.

## Assessment

**Spec compliance: Compliant (task-scoped; CannotVerify boundaries above).**

**Task quality: Approved, with 1 nonblocking Minor.**

The shared scheduler preserves physical occupancy through live enumeration/disposal while keeping logical priority across preparation and persistence gaps. Existing Chat/desktop integration maintains retained-tail shutdown, source-owned failure repair and dependency lifetime; the sole finding is a racy counter in one new test, independently covered by the stronger atomic-counter test.


## 后续并发测试修正 · 1a7b5bc

原初审M1保留以上历史。Task5修复后完整回归另暴露同一测试把两槽并发回调执行顺序当作派发顺序的问题；仅改测试，两个问题均已关闭。最终源1a7b5bc完整560通过／2原有跳过、build零警告错误。限定复审原文：

# Task 4 supplemental fixture review

## Scope and verdict

**Approved for this test-only supplement. Addressed: 2; open: 0; new: 0 (0 Critical / 0 Important / 0 Minor).**

Fixed range: `622de681bdfae5a27f6d1116b364d1c02c5c1918..1a7b5bcdff21736199523dca7723219d8ce50524`. Read the complete immutable `review-622de68..1a7b5bc.diff` once (5,858 bytes): one file, `tests/Mindweave.Tests/AiRequestSchedulerTests.cs`, 43 insertions / 11 deletions. Also read the final supplemental section of `task-4-report.md`, original `task-4-review.md` M1, and the relevant failure excerpt in `task-5-fix1-release.log`. No unchanged scheduler source was needed.

## Finding disposition

1. **Original Task 4 M1 — ADDRESSED.** Every physical callback now increments the shared count through `Interlocked.Increment` and updates the peak through a `Volatile.Read` / `Interlocked.CompareExchange` maximum. A delayed lower value cannot overwrite a higher peak. The final assertion still requires exactly two, now read through `Volatile.Read`; both Summary blockers, Reply and both Titles use this same instrumentation. The callback decrement is protected by `finally`.

2. **Observed callback-order fixture failure — ADDRESSED.** The retained RED log reports 559 passed / 1 failed / 2 skipped and the old first-label assertion expected `reply` but observed `title1`. Releasing both Summary callbacks allowed two independent grants, so callback execution order was not a reliable witness for grant priority. The new fixture releases only A and keeps B physically occupied. Reply remains held until its start is observed, the order contains only `reply`, both Titles are unfinished, B is unfinished, and `ActivePhaseCount` is exactly two. Thus only one free slot participates in the ordering observation. After Reply completes and its logical demand is disposed, Title1 and Title2 use that same sole free slot sequentially; the exact asserted sequence `reply`, `title1`, `title2` checks both Reply overtaking the earlier queued background work and FIFO within the background class. This removes the invalid observation without serializing production callbacks or adding timing delays.

## Assertion strength and cleanup

The original Reply-first and peak-two assertions are retained and strengthened by exact full-order, blocked-Title, retained-B and active-phase assertions. Measuring all five callbacks also extends the peak check beyond the original pair of blockers. The guarded body releases/drains all created tasks in `finally`: it disposes A/B/Reply demand, opens every latch with `TrySetResult`, waits for both blockers, Reply and both Titles, and tolerates cancellation caused by cleanup of queued demand. Title handles remain covered by their existing `using` lifetime. No new sleeps, stress loops, API, product implementation, scheduler behavior, or weakened assertions are present in the package.

## Evidence boundary

No tests or build were rerun by this reviewer. The controller has fully read the exact final-source raw logs: `task-4-supplemental-fixture-focused.log` — **38 passed / 0 failed**; `task-4-supplemental-fixture-release.log` — **560 passed / 2 original explicit platform/native-vault skips / 0 failed**; `task-4-supplemental-fixture-build.log` — **0 warnings / 0 errors**. These are controller-verified results, not reviewer-executed validation; skipped vault cases are not passes.

This report is the sole requested write. Existing controller edits to `TRACK.md` and `docs/implementation/s4-verification.md` were preserved. No source, Git/index, native app, real database, credential vault, network/supplier operation or subagent was used. Approval is limited to the two fixture corrections and does not establish S4, native, provider, Windows, course or release acceptance. The original Task 4 review history remains unchanged.
