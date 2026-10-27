# S4 领域、输入协议与纯规则审查

最终代码`a436efe01b53fdff6a021eae6312dfec866c3ce8`；初版10505e6的I1在第1轮修复并限定复审关闭，开放Critical／Important／Minor均0。最终Release445通过／2原有凭据跳过／0失败，build零警告错误。批准仅覆盖Task1纯合同／规则，存储、调度、桌面及真实服务另验。

## 初审（61dad7e至10505e6）

### Spec Compliance

- ❌ **Issues found / SpecCompliant: false.** Short-body dwell is cumulative across interrupted visibility segments, contrary to the continuous 3-second rule in `docs/implementation/s4-summary-plan.md:21`. See I1 below.
- Review scope: fixed package `review-61dad7e..10505e6.diff`, BASE `61dad7efc9fcc894b23b92c7e4471c9fde8e595b`, HEAD `10505e6d14d8e3c6d12ffe723365c861b656e604`. The 15 Task 1 source/test additions were reviewed once in bounded diff sections. Controller documentation commits are distinguished from Task 1 implementation. No changed source file was reread; no hunk was cut off mid-function.
- ⚠️ **Cannot verify from this diff:** actual transactional membership/BranchSource authority, idempotency, schema migration, deletion of stored bodies/read state, atomic group CAS, stream completion/disposal, scheduler priority/capacity and native readable-range mapping. These are explicitly later-task responsibilities, not missing Task 1 implementations (`IKnowledgeSummaryStore.cs:10-26`, `ISummaryVersionStore.cs:14-33`, `INodeNamingStore.cs:10-20`, `KnowledgeSummaryProtocol.cs:89`). Native/platform/supplier/course/release acceptance remains unverified.

### Strengths

- `src/Mindweave.Application/Summaries/KnowledgeSummaryInputPlanner.cs:20-44,47-56,71-79`: all-own-row highwater is separate from effective own attempts; frozen ancestor identities stay ordered and unchanged; direct quote binds original BranchSource plus source revision/body and quote/binding hashes. Complete system instruction and quoted JSON envelope go through the existing budget estimator before acceptance; Standard purpose remains explicit even with Disabled configuration.
- `src/Mindweave.Application/Summaries/KnowledgeSummaryInputPlanner.cs:47-68`: reconstruction validates independent input/prepared/protocol/estimator versions, full reference metadata, frozen configuration and canonical request hash. It does not manufacture historical ChatMessage identities or invoke S3 compression.
- `src/Mindweave.Application/Summaries/KnowledgeSummaryProtocol.cs:23-49,89-131`: exact unique Title/Markdown fields, scalar-aware title checks, strict Unicode, byte limit and split-surrogate bounded accumulation are implemented independently. The buffer explicitly disclaims transport completion proof.
- `src/Mindweave.Application/Summaries/SummaryVersionPolicy.cs:8-49,51-101`: candidate replacement confirmation binds the current token; edits can survive generation; late existing-node completion cannot recreate a deleted node; deletion excludes candidates from deterministic ordinal fallback and permits unrelated historical cleanup despite pending states; draft leases and normalized no-change saves have explicit outcomes.
- `src/Mindweave.Core/Summaries/SummaryVersions.cs:5-28`, `SummaryReading.cs:5-16`, `SummaryNaming.cs:6-15`: full body, retained identity, draft/read/selection epochs and naming identity/hash snapshots are separate. The three focused store interfaces cover downstream consumers without introducing SQLite, Avalonia or runtime implementations.
- `src/Mindweave.Application/Summaries/SummaryReadPolicy.cs:9-45,60-71`, `SummaryTitleNormalization.cs:5-10`: actual normalized Unicode scalar intervals are unioned, stale epoch/revision/sequence/hash/time observations are rejected, manual unread suppresses the active session, and naming normalization is deterministic NFKC/whitespace/invariant case.

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- **I1 — Reset short-body dwell when continuous visibility ends.** `src/Mindweave.Application/Summaries/SummaryReadPolicy.cs:42-58` initializes `visibleDwell` from the prior state's accumulated value and only stops adding time when hidden/inactive, empty-range, below-threshold or non-fitting observations end the current segment. It never clears the accumulated value. With a one-scalar body and valid increasing revisions/sequences: visible at t=0, visible at t=2 (dwell=2s), hidden at t=3, visible at t=30, visible at t=31 produces dwell=3s and `IsRead=true`. No continuous active-visible segment lasted 3 seconds. This violates the explicitly selected continuous short-body condition and can mark a briefly revisited summary read. Reset dwell when the qualifying segment ends; start the returning segment from zero. Add a focused regression that first accrues 2 seconds, interrupts visibility, then observes only 1 second after return (must remain unread), with coverage for hidden/inactive and empty visible intervals. Existing `tests/Mindweave.Tests/SummaryPolicyTests.cs:27-45,126-136` tests interrupt before any dwell has accrued, so their passing result does not cover this case. This finding is established by direct state-transition analysis; no test run or source mutation was needed.

#### Minor (Nice to Have)

- None.

### Checks and Evidence

- Named external-code risk: whether reused LatestInput selects the intended final own attempt and whether Fits includes actual reserve. Focused reads of unchanged `src/Mindweave.Core/Chat/ChatHistory.cs:6-13` and `src/Mindweave.Application/Chat/ChatRequestBudget.cs:12-22` confirmed last attempt per ordered user and full UTF-8 content/overhead/output reserve respectively. Initial guessed ChatHistory path under Application did not exist; the exact Core path was located with a filename-only search.
- Named specification check: `docs/architecture.md:275` confirms viewport-based progress, extra short-body dwell, independent per-version read state, and manual read/unread. The task plan's `docs/implementation/s4-summary-plan.md:21` supplies the stricter continuous 3-second engineering choice applied in I1.
- Read the raw existing final evidence: `.superpowers/sdd/s4-summary-plan/task-1-release.log:10-15` reports **440 passed, 2 existing credential skips, 0 failed**; `.superpowers/sdd/s4-summary-plan/task-1-build.log:9-13` reports **0 warnings, 0 errors**. No new warning/noise issue found. Did not rerun the suite or Python fixtures.
- No native app, user database, credential vault, network/supplier request, other-track code, subagent or Git mutation was used. Only this review report was written.

### Assessment

**Task quality: Needs fixes / QualityApproved: false.**

**Counts: Critical 0 / Important 1 / Minor 0.**

**Reasoning:** The domain contracts, frozen input, strict protocol and version rules are well scoped and supported by meaningful tests. The continuous short-body dwell error belongs to this task's implemented pure policy and should be fixed before its contract is approved for downstream use; later storage/runtime/UI guarantees remain separate verification gates.


## 第1轮限定复审（10505e6至a436efe）

- **I1 — Reset short-body dwell when continuous visibility ends — ADDRESSED.** `src/Mindweave.Application/Summaries/SummaryReadPolicy.cs:48-51,59` initializes dwell to zero and adds a delta only when both the preceding and current observations qualify. Qualification uses active/visible, nonempty current intervals, current visible scalar coverage at threshold, and FitsViewport; historical Seen coverage cannot qualify the current segment. Hidden, inactive, empty, below-threshold and non-fitting observations therefore clear dwell and the preceding-qualification flag; the returning observation starts at zero.
- **I1 regression evidence:** `tests/Mindweave.Tests/SummaryPolicyTests.cs:183-206` contains four interruption cases. Each first accrues 2 seconds, interrupts, returns at t=30 and remains unread after only 1 second at t=31, then becomes read after a fresh continuous 3 seconds at t=33. It asserts zero dwell on interruption/return and retained Seen/epoch. `SummaryPolicyTests.cs:210-222` separately checks non-fitting layout reset, return zero and subsequent 1-second dwell while retaining the independent long-body read result.

### New Breakage in the Fix Diff

- **None. Counts: Critical 0 / Important 0 / Minor 0.** The two-file fix changes only dwell initialization/continuation and adds regressions. `SummaryReadPolicy.cs:51` explicitly subtracts elapsed time before adding to prior dwell, preserving arithmetic order. The surrounding scalar union, manual suppression, stale revision/epoch checks, sticky IsRead and long-body coverage expression remain unchanged; no public declaration changes.

### Checks and Evidence

- Read the fixed package `review-10505e6..a436efe.diff` once: BASE `10505e6d14d8e3c6d12ffe723365c861b656e604`, implementation HEAD `a436efe01b53fdff6a021eae6312dfec866c3ce8`, exactly two owned source/test files, 52 additions and 5 deletions. Read the prior I1 finding, task brief, appended fix report and appended API continuous-dwell clarification; no whole-feature re-review.
- Confirmed existing raw RED evidence in `task-1-fix-1-red.log:16-61`: four visibility Assert.False failures and one non-fitting Assert.Equal failure, **5 failed / 33 passed / 0 skipped**. Existing final focused `task-1-fix-1-green.log:11`: **38 passed / 0 failed / 0 skipped**. Existing final full `task-1-fix-1-release.log:15`: **445 passed / 2 original skips / 0 failed**. Existing final build `task-1-fix-1-build.log:9-11`: **0 warnings / 0 errors**.
- No test rerun was needed: the supplied runs and added regression assertions answer the specific transition risks. No Git commands, source/index/HEAD/branch mutation, subagents, native app, user database, vault, network or other-track access. Only this report was written.

### Out-of-Scope Observations

- **None.** Later store/runtime/UI contracts and native/product acceptance were not re-reviewed and are not promoted by this policy fix.

### Verdict

- **Fix round: All findings addressed, no new Critical/Important breakage. I1 ADDRESSED; open findings: none.**
