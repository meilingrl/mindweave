# S4 编辑、阅读与版本事务审查 · 2026-10-27

范围：`199105e4f94719fc04a55c007ee2a8071878a4f5..bd037ecc16c3692b3784052d5cd14de2117cd6e9`。独立审查结论为 Spec compliant / Approved，0 Critical、0 Important、0 Minor。仅确认 Task3 存储边界；调度、桌面和真实服务验收另行推进。

### Spec Compliance

- ✅ Spec compliant for Task 3's storage boundary. Reviewed fixed package `review-199105e..bd037ec.diff` (79,426 bytes, ten owned files), base `199105e4f94719fc04a55c007ee2a8071878a4f5`, head `bd037ecc16c3692b3784052d5cd14de2117cd6e9`. No controller documentation or schema migration is part of the change.
- ✅ All **22** declarations in `src/Mindweave.Application/Summaries/ISummaryVersionStore.cs:8` have concrete implementations across the five new partials. The dispatch's 23-method count is not a missing method. The test diff expands to **34** cases, consistent with the focused evidence; the dispatch's 35-test count is also a prose discrepancy.
- ✅ Reads use one deferred transaction on the existing read-only connection; writes use the existing single-reader channel and commit-before-success transaction (`src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryVersions.cs:10`; `src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.cs:16`, `:169`). No new writer, migration, output-body copy, HTTP path, UI implementation, or unrelated feature was introduced.
- ⚠️ Cannot verify from this diff: renderer-to-scalar mapping, desktop confirmation wording/interaction, runtime persistence gate and shutdown/scheduling, future naming completion/coalescing CAS, native/supplier/Windows acceptance, or forensic erasure of SQLite pages/WAL/backups. These remain later-task or acceptance obligations, not missing Task 3 store implementations. SQL row deletion and reference retention are verified below; they do not establish physical-media privacy.

### Strengths

- Editing is current-only and epoch/revision guarded. Renew preserves dirty text; autosave stores empty/incomplete content; invalid explicit commit returns without mutation. Clear retains a tombstone; normalized-title/exact-Markdown no-change creates no version. Changed manual save uses the monotonic ordinal helper, copies exact coverage JSON and effective message-reference rows, creates read=true state, and preserves candidate/baseline even while Summary runs (`src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryDrafts.cs:24`, `:29`, `:38`, `:58`; `tests/Mindweave.Tests/SummaryVersionStorageTests.cs:25`, `:39`, `:56`; `tests/Mindweave.Tests/SummaryVersionStorageTests.Edges.cs:43`, `:56`).
- Adoption forwards the locked policy's exact current/candidate conflict and confirmation CAS; history switching preserves saved versions and their reading state. Current changes clear resolved draft leases, maintain base title/group key, and preserve manual display names; rename has its own name revision (`src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryVersions.cs:46`, `:55`, `:77`, `:90`; `tests/Mindweave.Tests/SummaryVersionStorageTests.cs:56`, `:78`).
- Deletion recomputes the policy preview inside the writer and compares token/scalars plus array sequences. Current/last deletion remains blocked by affected pending states; unrelated history deletion preserves dirty draft, candidate, and Summary job. Candidate discard is separate. Selection repair, full-body deletion, cascading read/source deletion, node removal, minimal identity pruning, and affected naming-job stop share the transaction (`src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryDeletion.cs:15`, `:40`, `:44`, `:65`; `src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryVersions.cs:64`). Tests cover serialized preview round-trip, forged fallback, previous-then-latest fallback, chat preservation, late writes, and retained baseline/naming identities (`tests/Mindweave.Tests/SummaryVersionStorageTests.cs:93`, `:113`, `:134`; `tests/Mindweave.Tests/SummaryVersionStorageTests.Edges.cs:13`, `:29`, `:81`).
- Reading uses the locked policy, never marks read on load/Open, keeps each version independent, and enforces exact revision/epoch/hash/sequence plus finite location bounds. Save/Set are UPDATE-only; explicit Begin checks live full content before initializing absent metadata. Manual-unread suppression and continuous short-body dwell are exercised against real storage (`src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryReading.cs:37`, `:61`, `:75`; `tests/Mindweave.Tests/SummaryReadStorageTests.cs:13`, `:33`, `:46`, `:63`). Later-content detection uses terminal own-conversation rows above frozen highwater, with a branch regression excluding later ancestor content (`src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummaryVersions.cs:35`; `tests/Mindweave.Tests/SummaryVersionStorageTests.Edges.cs:56`).
- Summary selection validates workspace/node/live-version ownership, retains clear revisions, and writes legacy Network/Overview atomically. Ordinary navigation invalidates the companion within its existing writer transaction; table detection preserves custom old-schema use. Repairing deletion never puts summary identities into conversation FKs (`src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.SummarySelection.cs:10`, `:29`, `:35`, `:57`; `src/Mindweave.Infrastructure/Storage/SqliteWorkspaceStore.cs:155`; `tests/Mindweave.Tests/SummaryVersionStorageTests.cs:145`).
- Tests use fresh synthetic SQLite, actual SQL triggers, and complete logical-table snapshots, rather than mocked persistence. Competing tokens yield one winner; late failures and exhausted counters preserve rows (`tests/Mindweave.Tests/SummaryStorageFixture.cs:20`, `:51`; `tests/Mindweave.Tests/SummaryVersionStorageTests.cs:163`; `tests/Mindweave.Tests/SummaryVersionStorageTests.Edges.cs:106`, `:120`, `:145`; snapshot implementation `tests/Mindweave.Tests/SummaryMigrationTests.cs:11`).

### Issues

#### Critical (Must Fix)

- None found.

#### Important (Should Fix)

- None found.

#### Minor (Nice to Have)

- None found.

### Assessment

**Task quality:** Approved.

**Reasoning:** The implementation composes the locked pure policies with authoritative SQLite reads and a single transactional writer. The code and synthetic fault/concurrency tests support the Task 3 claims without promoting storage verification into runtime or native acceptance.

- Scope check: diff read once in bounded sections, all 1,017 package lines consumed without truncation; no changed implementation was reopened for another review pass. A prior combined documentation output was truncated and the relevant handoff/plan sections were recovered separately.
- Named external check — policy-contract risk: read the actual `ISummaryVersionStore`, `SummaryVersionPolicy`, `SummaryReadPolicy`, and Core version/token DTOs to verify method completeness, pending-state guards, precise preview comparisons, and read CAS rather than trusting the report.
- Named external check — FK/retention/ordinal/group-epoch risk: read unchanged `SummarySchema.cs` and `SqliteWorkspaceStore.SummaryQueries.cs`, together with Task 2's private-helper handoff. Deferred pointer ownership, body-owned cascades, minimal-identity references, monotonic ordinal triggers, and naming-group invalidation match the new transaction ordering. No finding requires DDL; schema 6 remains frozen.
- Named external check — writer/navigation integration risk: inspected existing writer construction and `WriteAsync`, plus the ordinary `SaveSelection` call sites. The fixed diff cuts off the existing `ReadAsync` function; its continuation was read together with the adjacent writer implementation. No nested writer call or competing transaction path was introduced.
- Named external check — rollback-evidence risk: inspected `SummaryMigrationTests.Snapshot` to confirm assertions compare every logical application-table row rather than only counts or selected summary rows.
- Evidence checked: existing `task-3-release.log` reports **522 passed, 2 original CredentialVault platform skips, 0 failed**; existing `task-3-build.log` reports **0 warnings, 0 errors**. No additional warning was present in the inspected output. No suite or focused test was rerun; code inspection raised no unresolved behavioral doubt requiring a new run.
- Actions: read-only review except this requested report. No source/index/branch mutation, native application, supplier call, user database, vault, network, other track, or additional subagent was used.
