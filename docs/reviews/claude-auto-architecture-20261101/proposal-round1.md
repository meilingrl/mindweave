# Proposal: architecture remediation and course compliance (Claude, round 1)

Author: Claude · 2026-11-01 · reviewed commit `684314393c81a2eff2026f21c221a0371b8de9a7`

This is a proposal. None of it has been implemented, and no test described here has been run. Statements about failure paths come from reading the code; where one has not been reproduced it is labelled **hypothesis** (H-n) and paired with the test that would confirm or refute it. Paths are relative to the repository root, with line numbers taken at the reviewed commit.

ID conventions: **F** architectural finding · **L** local defect (fixable without changing ownership) · **H** hypothesis needing deterministic reproduction · **D** decision for the user (or the instructor, where marked) · **C** course requirement · **S** implementation slice · **V** validation criterion.

---

## 1. Positions at a glance

| ID | Topic | Claude's position (round 1) | Action |
| --- | --- | --- | --- |
| F1 | Storage health lives in `GenerationCoordinator` | Accept. Move application-wide health into a small Application-level owner; domain-specific pending writes stay with their owners | S2, after D1/D2 |
| F2 | Summary edit drafts owned by page and `Workbench` | Accept. Move the newest draft into an Application-level owner; remove precedence logic from navigation | S3 |
| F3 | Scheduler breadth | Defer with recorded triggers; reassess after S2 | No slice now |
| F4a | `Entry` loads every version to select the current one | Exists; violates architecture §11.2 for the network; fix now | S4 |
| F4b | List and network contracts carry the current body | Exists; still violates §11.2 after F4a is fixed; fix now with a body-less header plus a narrow excerpt query for naming | S4 |
| F4c | Detail loads every historical body | Exists; violates no current design rule; defer with a measurable trigger | Deferred |
| F5 | Text-only request, stream and live-answer path | Acceptable for the first version; correct one comment that overstates readiness | Documentation only |
| C1–C8 | Course architecture requirements | Mixed: several satisfied, several partial, three need the instructor's interpretation. VM→application-service interfaces (C3) is the main gap | S5, after D4–D7 |

My first review **rejected F4's contract coupling "for now"**. I withdraw that (see §5): Codex is right that §11.2 is a current constraint, and deferring a remedy is not the same as denying that the coupling exists.

---

## 2. F1 · Storage-health ownership

### 2.1 What the code does now

- `GenerationCoordinator` owns the blocked flag, the failure counter, the probe/retry loop and `StorageStateChanged` (`src/Mindweave.Application/Conversations/GenerationCoordinator.cs:148-164`, `:1000-1088`). It also holds the queue of failed terminal writes (`_unsaved`), whose flush is a precondition for recovery (`:1075-1080`, `:1091-1119`).
- Non-AI use cases reach health through the coordinator. `ConversationService` re-exposes it (`ConversationService.cs:129-137`) and reports branch/question failures (`:343-358`, `:488-492`). `SummaryService` gates and reports only `SummarizeAsync` (`SummaryService.cs:144`, `:214-217`).
- The probe is a whole-database operation, but it is exposed through a domain store: `IConversationStore.ProbeWriteAsync` (`IConversationStore.cs:133`), forwarded to `SqliteDatabase.ProbeWriteAsync` (`SqliteDatabase.cs:151`).
- Gaps in coverage:
  - Summary commits neither gate nor report: `SaveEditAsync`, `AdoptAsync`, `SetCurrentAsync`, `DeleteVersionsAsync`, `DiscardAsync`, `RenameAsync` (`SummaryService.cs:239-303`).
  - Archive, restore and delete catch storage refusals in the page (`ArchivedViewModel.cs:90`, `:120`) but do not report them.
  - `Pump` dequeues queued work regardless of health (`GenerationCoordinator.cs:490-511`).
  - `SaveAsync` blocks on *any* exception type (`:978-998`), while summary saving blocks only on `StorageWriteException` (`:813-816`).
- Requirement basis:
  - Core-save failure pauses "new AI requests and operations that depend on commits"; only layout/page state may degrade (`docs/requirements.md:258`, §9.3).
  - Architecture names the paused operations "new AI requests and branch/summary commits that depend on persistent saving" (`docs/architecture.md:406`).

### 2.2 Proposed ownership and dependency direction

```
Desktop VMs ──read──▶ IStorageStatus            (IsBlocked, Changed)
ConversationService ─┐
SummaryService ──────┼─▶ StorageHealth : IStorageStatus   (Application/Storage)
GenerationCoordinator┘        │  ▲ registers as IPendingCoreWrites
                              ▼  │
                        IStorageProbe   ◀── implemented by SqliteDatabase (Infrastructure)
```

- **`StorageHealth` (new, `src/Mindweave.Application/Storage/StorageHealth.cs`) owns:**
  - the blocked flag and the monotonic failure counter;
  - the recovery loop and its interval (moved from `GenerationOptions.SaveRetryInterval` to a `StorageHealthOptions`);
  - the probe call;
  - the `Changed` event and its logging.
- **The coordinator keeps** its `_unsaved` queue, its `_flushGate`, and the guarded terminal-write semantics. It becomes one registered participant.
- **Contracts** (names are proposals; Codex should challenge them):

```csharp
public interface IStorageStatus { bool IsBlocked { get; } event EventHandler? Changed; }

/// Writes a participant kept after a core-write failure; recovery waits until none is left.
public interface IPendingCoreWrites
{
    bool HasPending { get; }
    Task<bool> FlushAsync();   // one pass; true when nothing is left pending
}

public interface IStorageProbe { Task ProbeWriteAsync(CancellationToken cancellationToken); }

public sealed class StorageHealth : IStorageStatus, IAsyncDisposable
{
    public void Report(StorageWriteException failure);   // core failures only
    public void Register(IPendingCoreWrites participant); // at composition time
    public Task WhenRecoveredAsync(CancellationToken ct); // optional; for retry hooks such as drafts
}
```

- `ProbeWriteAsync` moves off `IConversationStore` onto `IStorageProbe`, which `SqliteDatabase` implements and `InfrastructureServices` registers.
- `ConversationService.IsStorageBlocked` and `StorageStateChanged` either delegate to `IStorageStatus` or are removed in favour of injecting `IStorageStatus` into the two VMs that read it (`ConversationViewModel.cs:61-63`, `:251-256`). The second option is preferred because it also helps C3.

**Affected files:**
- `GenerationCoordinator.cs`: remove the health fields, `MarkStorageBlocked`, `EnsureRetryLoop`, `ProbeAsync` and `ClearStorageBlocked`; implement `IPendingCoreWrites`; call `Report`.
- `ConversationService.cs` and `SummaryService.cs`: depend on `StorageHealth` instead of the coordinator for gating and reporting.
- `IConversationStore.cs`, `SqliteConversationStore.cs:249`, `SqliteDatabase.cs`, `InfrastructureServices.cs`, `CompositionRoot.cs`.
- `ConversationViewModel.cs`, `ArchivedViewModel.cs`, `SummaryViewModel.cs`.
- Tests that construct the coordinator directly.

### 2.3 Which failures block which operations

A failure blocks only when it is a `StorageWriteException` from a **core** write. Every core write's owner calls `StorageHealth.Report` exactly where it already catches the exception.

| Write | Core? | Reports? | Basis |
| --- | --- | --- | --- |
| Question/conversation/retry rows, task rows, mark-running, answer checkpoints and terminal states | Core | Yes (as today) | §9.3; architecture §406 |
| Branch creation | Core | Yes (as today) | Architecture §406 "branch commits" |
| Generated summary version | Core | Yes (as today) | §406 "summary commits" |
| Manual summary save, adopt, set current, delete versions, discard candidate | Core | **New** | §406 "summary commits"; today unreported (`SummaryService.cs:239-282`) |
| Archive, restore, delete conversation | Core | **New, D1** | These commit content state; today refused but unreported |
| Automatic title; summary/conversation rename | Core metadata | Title: yes (as today); rename: **D1** | Not content, but a failure here means the disk is refusing commits |
| Message drafts, summary edit drafts | User input | **No (D1, recommended)** | Kept in memory and retried; exit refuses to close while they are unsaved |
| Network layout, preferences, last location, reading positions | Page state | No | "仅布局／页面状态失效时可降级" (`requirements.md:258`); "界面偏好失败可降级" (`architecture.md:406`) |
| AI request traces | Diagnostic | No | `GenerationCoordinator.cs:932-944` already excludes them |

**Why layout, preference and diagnostic failures must not block automatically:**
1. The requirements explicitly let them degrade.
2. Their failures can be local (a layout row conflict, a bad preference value), and pausing every AI request over them would be disproportionate.
3. Nothing is lost by not escalating. If the disk really is refusing writes, the next core write (at the latest, mark-running before any AI call, `GenerationCoordinator.cs:556-570`) fails, reports, and blocks before a request is sent.

**Gated while blocked** (new requests are refused with `StorageUnavailable`):
- new AI requests: start, ask, retry, summarize, title and naming scheduling;
- branch creation;
- summary version commits;
- archive/restore/delete if D1 says so.

Reading, copying and export of already-saved versions stay available, as §9.3 requires.

### 2.4 State transitions, recovery and race safety

States: `Healthy → Blocked → (Probing) → Healthy`.

1. `Report(e)`: under the lock, increment `failures` and set `blocked`. If this was the first transition, raise `Changed` (outside the lock) and start the loop if it isn't running. This is the current `MarkStorageBlocked` logic, moved unchanged.
2. Every interval while blocked:
   1. Read `f = failures`.
   2. For each participant, call `FlushAsync()`. Participants flush serially, each behind its own gate (the coordinator's `_flushGate` stays its own).
   3. If every participant returned true, probe.
   4. If the probe succeeded, under the lock: clear only if `failures == f` **and** no participant `HasPending`. Then raise `Changed`.
   5. Otherwise stay blocked.
3. **A newer failure is never cleared.** Any `Report` after step 2.1 changes `failures`, so step 2.4 refuses to clear. This is the current invariant (`GenerationCoordinator.cs:1077`).
4. **Recovery is never declared while required content is unsaved.** The `HasPending` re-check under the clear lock covers a write that a participant enqueues after its flush pass but before it calls `Report`. Today the coordinator enqueues first (`:992-994`) and then reports, and the check `!_unsaved.IsEmpty` (`:1077`) covers the gap. The interface keeps that property for every participant.
5. Running tasks are not stopped when storage blocks. Their terminal writes fail, are kept, and are retried (unchanged).

### 2.5 Queued AI work while blocked — D2

- **Fact:** `Pump` starts queued work while blocked (`GenerationCoordinator.cs:494`). A queued answer then either fails at mark-running with `StorageUnavailable`, or calls the AI if that small write happens to succeed on a nearly full disk.
- **Proposal (recommended option of D2):**
  - `Pump` does not dequeue while `IsBlocked`. On `Changed → healthy` it pumps again.
  - Queued work keeps its "queued" state and its frozen request. It was never sent, so starting it after recovery is not an automatic resend (architecture §157 forbids reviving *interrupted* requests, not starting accepted, never-sent ones).
  - Exit while blocked cancels queued work as today.
- **Alternative:** keep today's behaviour and document that queued work fails fast while blocked. This is simpler, but it can lose a user's accepted question to a transient lock.

### 2.6 Lifecycle and shutdown

- `StorageHealth` is a singleton created by the container. Participants register in `CompositionRoot` after construction, either through a small `Register` call in `AppStartup` or a factory registration. It owns its loop's `CancellationTokenSource`.
- Order at shutdown:
  1. `ExitCoordinator` stops tasks (`StopForExitAsync`).
  2. It flushes pages, drafts and `generation.SaveUnsavedAsync()` (`ExitCoordinator.cs:229-237`, unchanged).
  3. The coordinator shuts down.
  4. `StorageHealth.DisposeAsync` cancels and awaits the loop.

  The exit flush and a concurrent loop pass are already serialized by the coordinator's own `_flushGate`, and draft owners need the same per-owner gate.
- "Return" after a failed exit reopens the coordinator (`Reopen`). It does not reset health: the blocked state persists until recovery.

### 2.7 Schema, compatibility, alternatives

- **Schema:** none.
- **Behavioural changes:** new gating of summary/archive commits, and queued work held (D2). Both need the user's decision because they change what the user can do while storage is failing.
- **Alternatives considered:**
  1. Keep health in the coordinator and document it. Rejected: summary and archive commits would still have to depend on the AI module to take part.
  2. Report from the Infrastructure write queue automatically. Attractive as a single choke point, but it would block on layout and preference writes unless every write were tagged by category. Classification is an Application decision, so this is rejected for now. It could later complement this design as a safety net.
  3. An event bus or mediator. Disproportionate.

  The chosen design moves about 150 lines and adds three small interfaces.

### 2.8 Separate local defect L3 (not architecture)

`SaveAsync` blocks storage on any exception (`GenerationCoordinator.cs:985-996`), so a programming error in a terminal write would pause all AI work and be retried forever. Narrow it to `StorageWriteException` and fail other exceptions visibly. This changes behaviour, so it is listed separately and needs one deterministic test.

---

## 3. F2 · Summary edit-draft ownership

### 3.1 What the code does now

- Architecture assigns summary edit drafts to the summary use case, autosaved independently (`docs/architecture.md:68`). Message drafts already follow that design: `ReadingStateService` keeps the newest value per owner in memory, combines writes, keeps failures marked unsaved and flushes on exit (`ReadingStateService.cs:8-13`).
- Summary drafts do not:
  - `SummaryService.SaveDraftAsync` passes the call straight to the store (`SummaryService.cs:266-267`).
  - The page owns the content, the revision counters `_draftVersion` and `_savedDraftVersion`, and the one-second throttle (`SummaryViewModel.cs:80`, `:89-90`, `:105`, `:513-549`).
  - `Workbench` keeps dropped pages, retries their saves at exit, and decides precedence through `FlushKey`/`DraftSettled` (`Workbench.cs:21`, `:206-238`, `:245-300`).
- `IPageFlush` (`PageViewModel.cs:17-30`) has exactly one implementer, `SummaryViewModel`.
- `TRACK.md:109` records how this logic built up: a fix for "retrying exit loses a left page's draft" introduced "an old draft overwrites a newer one", which needed a second fix.

### 3.2 Loss paths (hypotheses until reproduced)

- **H1 · reopen after a failed save.**
  1. Edit; a draft write fails; leave the page. The dropped page holds the newer text.
  2. Reopen and begin editing. `BeginEdit` reloads the stored draft (`SummaryViewModel.cs:430-446`) and shows the older text.
  3. Then either:
     - (a) Save a version without typing. `DraftSettled` discards the newer leftover (`Workbench.cs:275-286`), and the text is lost without any notice.
     - (b) Just leave and exit. The leftover is written at exit, so the stored draft differs from what the user last saw.

  The existing tests (`tests/Mindweave.Desktop.Tests/ExitTests.cs:208`, `:249`) cover leaving and retrying, and a *newer typed* draft winning. They do not cover the user seeing or committing the reopened page's content.
- **H2 · version switch after a failed save.** Leave with an unsaved draft (no stored row), reopen, and set another version current or delete versions. `ThrowIfDraft` sees no stored draft (`SqliteSummaryStore.cs:429-435`), so the switch succeeds. At exit the leftover's save is refused with `Changed` (`SqliteSummaryStore.cs:186-189`). `SaveDraftAsync` treats `SummaryRefusedException` as settled (`SummaryViewModel.cs:534-548`), so the draft is lost with only a notice on a disposed page.

### 3.3 Proposed ownership

- **`SummaryDrafts` (new, `src/Mindweave.Application/Summaries/SummaryDrafts.cs`)**: a singleton modelled on `ReadingStateService`, used by `SummaryService` and the page.
- **Draft identity:** `SummaryNodeId`. There is still exactly one draft per node, and the entry carries its `Baseline` (`SummaryVersionId`). Unchanged from the schema (`summary_edit_draft` keyed by `node_id`).
- **Contract:**

```csharp
public sealed record DraftSnapshot(SummaryNodeId Node, SummaryVersionId Baseline, string Title, string Body, long Revision);

public sealed class SummaryDrafts
{
    // Newest value: memory first, else the stored draft; null when none or its baseline is no longer current.
    Task<DraftSnapshot?> GetAsync(SummaryNodeId node, SummaryVersionId current, CancellationToken ct);
    void Set(SummaryNodeId node, SummaryVersionId baseline, string title, string body); // sync; revision++; schedule
    bool HasDraft(SummaryNodeId node);   // memory entry exists (saved or not)
    Task<bool> FlushAsync();             // write every unsaved entry; false if any stays unsaved
    Task DiscardAsync(SummaryNodeId node, CancellationToken ct);
    // Commit and version operations run inside RunExclusiveAsync(node, ...) so no draft write interleaves.
}
```

- **Moves from the page to `SummaryDrafts`:** the newest content, revision ordering, throttle/scheduling, the unsaved flag and retry.
- **Stays in the page:** the editor's bound text, which it forwards with `Set` on each change, and `IsEditing`.
- **Moves from `Workbench`:** nothing moves to it. `DroppedPage`, `_dropped`, `OnDraftSettled`, `Track`'s subscription, the page-flush part of `FlushAsync`, and the `IPageFlush` interface are removed. `Release` disposes only.
- **Dependency direction:** `SummaryViewModel → SummaryService/SummaryDrafts (Application) → ISummaryStore`. `Workbench` no longer knows about content persistence.

### 3.4 Behaviour

- **Reopened pages:** `BeginEdit` calls `GetAsync(node, current)`. The memory entry wins over the store because it is never older. This addresses H1.
- **Revision ordering and stale writes:**
  - Each `Set` increments the entry's revision. A flush captures `(revision, content)`, writes, and marks it saved only if the revision is unchanged (the same idea as `InterlockedMax` today, moved).
  - Commit and discard take the node's exclusive gate and remove the entry, so a flush that captured content before them cannot write after them. The single SQLite writer is FIFO, so a write already queued before a commit lands before the commit's delete.
- **Save as a version:** `SummaryService.SaveEditAsync` runs under the node gate. It writes the version and deletes the draft row in one store transaction (unchanged, `ISummaryStore.cs:97-98`), then removes the memory entry. On failure the entry stays and the page keeps editing.
- **Discard:** under the gate, remove the memory entry and delete the stored row. If the delete fails, keep a tombstone so exit retries the delete instead of resurrecting the draft.
- **Version switching and deletion:** `AdoptAsync`, `SetCurrentAsync`, `DeleteVersionsAsync` and `DiscardAsync` first check `SummaryDrafts.HasDraft(node)` under the gate and refuse with `SummaryRefusal.HasDraft` when a memory draft exists, even if it is unsaved. The store's own `ThrowIfDraft` stays as the transactional check. This addresses H2 and keeps the existing rule that a draft must be saved or discarded first.
- **Baseline no longer current** (for example, a candidate adopted elsewhere — not reachable while a draft exists after the check above, but possible through a race): the store refuses with `Changed`. The entry is kept and marked conflicted rather than dropped, and the page shows "draft is based on an older version" with discard or copy. This replaces today's silent "settled" treatment (`SummaryViewModel.cs:534-548`).
- **Failed persistence:** the entry stays unsaved. It is retried on the next `Set`, by a scheduled retry (the same throttle), when `StorageHealth` recovers (`WhenRecoveredAsync`), and at exit. Per D1 (recommended), draft failures do not block AI requests.
- **Page disposal:** nothing to do. The newest value is already in `SummaryDrafts`, because `Set` is synchronous per change.
- **Application shutdown:** `ExitCoordinator.SaveEverythingAsync` calls `summaryDrafts.FlushAsync()` next to `reading.FlushAsync()` (`ExitCoordinator.cs:232-233`). `workbench.FlushAsync()` keeps only the last-location write.
- **Single draft per node:** two pages on the same node (possible across sections or workspaces) write into the same entry, and the newest revision wins, as the stored row does today. No live sync between open editors is introduced. A page already in edit mode keeps its text until its next `Set`. This is the current product behaviour, stated explicitly; no multi-editor or multi-window behaviour is added.

### 3.5 Schema, compatibility, alternatives

- **Schema:** none. The stored draft row and its baseline are unchanged.
- **Behaviour:** the throttle moves from the page (1 s) to the service (keep 1 s). H1 and H2 change from losing input silently to preserving it, or refusing visibly.
- **Alternatives considered:**
  1. Keep the drafts in the page and fix H1 by having `BeginEdit` ask `Workbench` for leftovers. Rejected: it deepens navigation's role in persistence.
  2. Put the logic inside `SummaryService`. Possible, but `SummaryService` is already 488 lines with generation and naming. A separate class with one job mirrors `ReadingStateService` and is easier to test.
  3. A generic draft framework shared with message drafts. Not justified: two owners with different identities and gating rules.

---

## 4. F3 · Scheduler breadth — deferred with triggers

- **Agreed with Codex:** a single scheduling authority (the slots, priorities, stop/exit handling and the guarded-terminal-write invariants) is required by architecture §6 and §218. It does not require every executor to live in the same class, but that alone does not justify refactoring now.
- **After S2**, the coordinator loses the health state and loop (about 150 lines). Reassess then, against these **triggers** (any one reopens F3):
  1. a fifth task kind is proposed;
  2. any task needs more than one AI request inside its slot, beyond compression (for example, a tool loop);
  3. per-kind policy diverges further (priority, exit prompt or cancel-with-conversation rules beyond the current `is TitleWork or NamingWork` checks, `GenerationCoordinator.cs:326-331`);
  4. a change in one executor forces edits in another executor's error handling.
- **What a fifth kind requires today:**
  - an enum value plus `SqlNames` (`SqlNames.cs:69-72`);
  - a `GenerationWork` record;
  - a `RunAsync` switch case (`:517-531`);
  - a copied `RunXAsync`;
  - decisions in `UnfinishedUserWork` (`:195-212`), `CancelBackgroundAsync` and the exit path;
  - possibly a new finished event.
- **Smallest step at that point:**
  - one "complete-text work" path with a save callback (titles join naming);
  - per-work flags (`IsUserWork`, `CancelWithConversation`) in place of type tests.
- **Kept separate from F3:** **L2**, titles not rejecting truncated output and duplicating the stream loop (`:706-765` versus `RequestCompleteTextAsync`, `:890-930`), is a local fix.

---

## 5. F4 · Summary read contracts — reassessed

The shared design constraint is in architecture §11.2: the network is read in batches on entering a workspace, and "不读取全部总结正文来绘制网络" (`docs/architecture.md:380`). Bodies have no upper bound: the 16k-token cap applies only to generated output (`SummaryService.cs:60`), and manual edits are unlimited (`SummaryRules.cs` has no body limit). I have no measured node counts or version counts, so the impact estimates below are unmeasured.

| | (a) `Entry` loads every version, then picks current | (b) List/network contracts carry the current body | (c) Detail loads every historical body |
| --- | --- | --- | --- |
| **Exists?** | Yes: `Versions(...).Single(...)` (`SqliteSummaryStore.cs:330`, query `:387` selects `body`) | Yes: `SummaryEntry(..., SummaryVersion Current, ...)` (`ISummaryStore.cs:11`); `SummaryVersion` includes `Body` (`Summary.cs:69-81`) | Yes: `SummaryDetail.Versions` (`ISummaryStore.cs:14-31`) |
| **Violates current design?** | Yes. `NetworkService.ProjectAsync` → `ListAsync` (`NetworkService.cs:43-58`) reads every body, including history | Yes. Even after (a), the network reads every *current* body, which is still "all summary bodies" | No stated rule. §8 asks only for simple history |
| **Likely impact** | Grows with nodes × versions × body size on every network refresh and every naming check (`SummaryService.cs:410-419` lists the whole workspace) | Grows with nodes × body size; the only body consumer is the naming excerpt (`SummaryService.cs:434-435`) | Grows with one node's edit count × body size, on opening a summary page |
| **Unmeasured** | Workspace sizes, refresh frequency, latency | Same | Versions per node in real use |
| **Recommendation** | **Fix now** (S4) | **Fix now** (S4) | **Defer.** Trigger: a seeded measurement shows detail load > 100 ms at 50 versions × 50 KB, or history gains features (diff, search) |

**Minimal proposal for (a)+(b):**
- Add a body-less header in Core, `SummaryVersionHead(Id, Node, Seq, Title, ShortTitle, ToSeq, Origin, CreatedAt)`. `SummaryEntry.Current` becomes `SummaryVersionHead`.
- `SummaryRules.Markers` and `ShortTitleOf` take the fields they use: `ToSeq`, and `Title`/`ShortTitle`.
- `Entry` selects only the current version's columns, without `body`: `... FROM summary_version WHERE id = $current`.
- **Naming** gets `ISummaryStore.CurrentExcerptsAsync(IReadOnlyCollection<SummaryNodeId> nodes, int maxChars)`. It uses `substr(body, 1, $max)`; SQLite `substr` counts characters for TEXT. It is called only for the same-name group being named, so network consumers never fetch bodies.
- `ForConversationAsync` (the conversation header uses only `DisplayName`, `ConversationViewModel.cs:516-524`) returns the same lighter entry.
- No schema change.
- The N+1 pattern in `Entry` (4 queries per node) is left as is. Defer with a trigger: a seeded projection measurement over 50 ms at 500 nodes.

**Deterministic check:** a structural test asserts that `SummaryEntry` exposes no `Body`. The compiler then guarantees that network consumers cannot read bodies. Existing network and naming tests must pass unchanged, and a new naming test asserts that excerpts are at most 600 characters.

---

## 6. F5 · Text-only pipeline

- **Accurate:**
  - Messages, storage (`message_block`) and anchors are organised as blocks, and `ContentBlock` says "Only text exists today" (`ContentBlock.cs:3-7`).
  - `ModelSnapshot.Protocol` is frozen per task (`ModelSnapshot.cs:12-19`). Another text protocol needs an adapter plus routing on `Protocol`; today a single `IChatClient` is registered (`InfrastructureServices.cs:44`).
- **Not ready, and not claimed to be:**
  - Multimodal input: `ChatTurn(Role, string)`. `ContentBlocks.TextOf` silently drops non-text blocks when building context.
  - Rich output: `LiveAnswer` is a string builder, and `Snapshot.Blocks = FromText(Text)`.
  - Tools: no tool events or role, no multi-request loop per task, no place to store tool results.
- **Correction (documentation, no code):** `ContentBlock.cs:3-5` says later kinds "slot in beside text without reshaping messages". That is true of messages only. Add that requests, streaming and live answers are text-only and will need their own changes. Record the limitation in `TRACK.md` at the next track merge.

---

## 7. Local defects (no ownership change)

| ID | Defect | Evidence | Fix | Validation |
| --- | --- | --- | --- | --- |
| L1 | Summary page commands do not handle `StorageWriteException`, so the exception escapes the command and the process probably ends | `SummaryViewModel.cs:560-572` catches only `SummaryRefusedException`; the global handlers only log (`Program.cs:36-37`) | Catch it, show the storage notice, keep the input, and report it (after S2, through the service) | A fault-injected store fails save-version: the page shows the notice, the editor keeps its text, the process lives |
| L2 | Titles ignore truncation and duplicate the stream loop | `GenerationCoordinator.cs:706-765` | Use `RequestCompleteTextAsync` | A fake client returns `Completed(Truncated: true)`: no title stored, task failed |
| L3 | Terminal-write failures of any exception type block storage | `GenerationCoordinator.cs:985-996` | Block only on `StorageWriteException` | A non-storage exception in a terminal write leaves health unblocked and the task failed |
| L4 | Queued work starts while blocked | `GenerationCoordinator.cs:494` | Via D2 in S2 | V-R5 |
| L5 | Two view models expose `Avalonia.Thickness` | `ConversationListViewModel.cs:52`, `ArchivedViewModel.cs:25` | Expose `Depth`; convert in the view | Architecture test: no `Avalonia` type in `ViewModels/` (C8) |
| L6 | `AnswerView` reaches its ancestor's `DataContext` and drives button state imperatively | `AnswerView.axaml.cs:167-185` | Optional: a selection-state property plus a command binding | Existing branch UI tests |

L1–L3 and L5 are independent of S2/S3 and can land first. L1's "report" part waits for S2.

---

## 8. Course architecture compliance (C1–C8)

**Source:** `期末项目与报告要求.docx`, section "技术与架构要求". It states that the project "只需要反映对应的思想，但无需采用一模一样的技术". Items marked "(要求复刻)" are stricter. I inspected the DailyPoetryA reference at the path in the README.

**Classification:** **S** satisfied · **P** partially satisfied · **N** not satisfied · **I** needs the instructor's interpretation.

| ID | Requirement (short quote) | Mindweave evidence | DailyPoetryA reference | Class | Smallest remedy or clarification |
| --- | --- | --- | --- | --- | --- |
| C1 | MVVM: "Views/ 仅承载 XAML 与极薄 code-behind" | VMs under `src/Mindweave.Desktop/ViewModels/` on CommunityToolkit; Core records are plain data. Most code-behind is 8–41 lines; `ConversationView.axaml.cs` 193, `AnswerView.axaml.cs` 185, `SummaryView.axaml.cs` 111 hold scroll geometry, reading-position capture, selection→anchor and Markdown rendering (architecture assigns geometry to views, `architecture.md:67`) | Code-behind is near-empty | **P / I** | Explain the geometry code in the report. Optional L6. Ask whether geometry/rendering code-behind counts as "thin" |
| C2 | Navigation parameters: "SetParameter(object parameter)" … "要有类似机制和设计" | Typed `ContentRoute` records and `NavigationRequest` (`NavigationRequest.cs`, `ContentRoute.cs`); `PageFactory` builds the VM with `(route, PageContext)` (`PageFactory.cs`). VMs never see the stack (they get `IWorkbenchNavigator`). The navigation layer (`Workbench`) depends on `IPageFactory`, not on VM types | `ViewModelBase.SetParameter` virtual; navigation services switch on constants to concrete VMs | **S** | Explain in the report: the parameter arrives via the constructor (immutable, typed) instead of a virtual setter; same decoupling |
| C3 | Interfaces and constructor injection: "ViewModel 通过构造函数注入…拿到接口引用，避免硬依赖实现类型" | IO is behind interfaces: `ISummaryStore`, `IConversationStore`, `IChatClient`, `ICredentialStore`, `IExportFileWriter`, `IReadingStateStore`, … UI services are interfaces: `ISystemShell`, `IUiDispatcher`, `IThemeApplier`, `IPageFactory`, the navigators. **But** VMs take concrete application classes: `ConversationService`, `SummaryService`, `ReadingStateService`, `ModelSettingsService`, `AppearanceService`, `SummaryExportService`, and `GenerationCoordinator` (`ConversationListViewModel.cs:72`); `Workbench` takes `WorkspaceService` | Every VM constructor takes only `I*` interfaces | **P** | **D4.** (A) Add interfaces only for the application services VMs consume, and remove the VM's direct coordinator dependency. (B) Ask the instructor whether IO-boundary interfaces plus concrete use cases satisfy the intent. Conflicts with shared architecture "不引入…逐类接口" (`architecture.md:36`), so A needs a user decision on `main` |
| C4 | Central composition: "以 ServiceCollection 通过 IoC 容器集中组装" | `CompositionRoot.Build` with `ValidateOnBuild` plus `AddMindweaveInfrastructure`; `LayeringTests.Desktop_mentions_infrastructure_only_in_the_composition_files`. Page VMs are created by `PageFactory` from injected services, startup pages by `AppStartup` with `new` (`AppStartup.cs:47-111`) | `ServiceLocator` registers every VM and service | **S**; "全依赖注入化?" is **I** | The course text ends that heading with a question mark. Ask whether every VM must come from the container; if so, register factories for the startup/recovery/onboarding VMs |
| C5 | Three-layer navigation "(要求复刻)": Root/Menu/Content interfaces, "XxxNavigationConstant 常量类", stack in "MainViewModel.ContentStack / PushContent / GoBack", "接口定义能力、ViewModel 维护状态" | `IRootNavigator`, `IMenuNavigator`, `IContentNavigator` (plus `IWorkbenchNavigator`) (`Navigators.cs`). Menu targets: `MainSection` enum; content targets: `ContentRoute` types; root: **no target constants**, `Show(ViewModelBase)` takes an instance built by the caller (`RootViewModel.cs:18`, `AppStartup.cs`). Stacks: per workspace/slot/section, in `Workbench` (an `ObservableObject`; architecture calls it the main VM's state, `architecture.md:111`). It implements the menu and content interfaces itself; the interfaces expose no stack | Separate `*NavigationService` classes call `MainViewModel.PushContent`/`SetMenuAndContent`; string constants per layer | **P / I** | Gaps from the literal text: the names; typed targets instead of string constants (meets "避免散落的魔法字符串" more strongly); the root takes instances; the stack owner implements the interfaces. **D5:** ask whether typed routes and the workbench satisfy "复刻". Minimal code if not: add a `RootTarget` enum (Startup, Recovery, Onboarding, Workbench) with `IRootNavigator.Navigate(RootTarget, object? parameter)` and a root page factory |
| C6 | ViewLocator: "实现了 View 的自动渲染" … "类似的优雅机制" | `ViewLocator : IDataTemplate` with an explicit registry, chosen so tests catch a missing view (`ViewLocator.cs`) | Name-based reflection with caching | **S** | Explain in the report why there is an explicit registry instead of reflection |
| C7 | IO as services, async: "文件 IO、网络 IO、UI 反馈全部封装为可注入 Service" … "使用异步操作避免 UI 卡死" | Async stores behind a single writer queue; streaming `IChatClient`; `IExportFileWriter` and `ISystemShell.PickExportFileAsync`; `IUiDispatcher` marshalling. **UI feedback** (notices, confirmations, exit prompt) is VM state rendered inline (`PendingConfirmation`, `Notice`, `ExitCoordinator`), not an injectable alert service | `IAlertService` with an Avalonia implementation | IO: **S**; UI feedback: **P / I** | **D6:** ask whether inline VM-state feedback is acceptable. If not, the minimal option is an `IAlertService`-like service for confirmations only |
| C8 | Library/UI one-way: "Library/UI 双工程目录即抽象说明书"; the Library holds Models/Services/**ViewModels** | Four projects; Core ← Application ← Infrastructure; Desktop → Application (+ Infrastructure only in the composition files), enforced by `tests/Mindweave.Architecture.Tests/LayeringTests.cs`. **ViewModels live in the Avalonia project**; two use `Avalonia.Thickness` (L5) | VMs live in `DailyPoetryA.Library`; that library itself references `AvaloniaInfiniteScrolling.Collection` (used by `ResultViewModel`), so the reference is not framework-free either | One-way: **S**; VM placement: **P / I** | Fix L5 and add an architecture test that VM folders use no `Avalonia` types. **D7:** ask whether VMs must move to a UI-independent library. If yes: a `Mindweave.Presentation` project holding ViewModels, the navigation contracts and `UiText`, without an Avalonia reference — a larger move to schedule after S2–S4 |

**Not assessed here** (the source lists them separately):
- page count at least DailyPoetryA's;
- interfaces and classes at least 70% of DailyPoetryA's;
- the source-only zip under 10 MB;
- the PDF report and its template.

Architecture compliance alone does not establish course acceptance.

**Evidence gaps:**
- No instructor interpretation is available for C1, C4, C5, C7 or C8.
- I did not use the Worklog project's own compliance document (`Worklog/docs/04-期末项目要求符合性检查.md`); it describes another project.

**What can be checked deterministically and what needs a decision:**
- **Testable:**
  - one-way layering (existing tests);
  - no Avalonia types under `ViewModels/` (new architecture test);
  - every page VM registered in `ViewLocator` (existing `RegisteredViewModels`);
  - every VM constructor parameter being an interface or a primitive/record parameter (a new reflection test, only if D4-A is chosen).
- **Needs a decision:** C1 thinness, C4 "全依赖注入化", C5 "复刻" fidelity, C7 UI-feedback form, C8 VM placement.

---

## 9. Unified plan

### 9.1 Slices (narrow; one writer per set of files; none executed)

| Slice | Kind | Content | Depends on | Files (main) |
| --- | --- | --- | --- | --- |
| S0 | Docs | F5 comment correction; record F1–F5, L1–L6 and D1–D7 in `TRACK.md` when merging to the track branch | — | `ContentBlock.cs` (comment), `TRACK.md` (track branch only) |
| S1 | Local fixes | L2, L3, L5, and L1 without the report step | — | `GenerationCoordinator.cs` (title, `SaveAsync`), the two VMs, the summary page, architecture tests |
| S2 | Architecture | F1 `StorageHealth` (+ L4 per D2, + reporting from summary/archive commits per D1, + L1's report step) | D1, D2; after S1 (same file) | §2.2 list |
| S3 | Architecture | F2 `SummaryDrafts`; remove page-flush machinery from `Workbench` | S2 (recovery hook, `CompositionRoot`/`ExitCoordinator` overlap) | `SummaryDrafts.cs`, `SummaryService.cs`, `SummaryViewModel.cs`, `Workbench.cs`, `PageViewModel.cs`, `ExitCoordinator.cs`, `CompositionRoot.cs` |
| S4 | Architecture (contracts) | F4a+b header plus excerpt query | After S3 (`SummaryService.cs` overlap) | `Summary.cs`, `SummaryRules.cs`, `ISummaryStore.cs`, `SqliteSummaryStore.cs`, `NetworkService.cs`, `SummaryService.cs` naming |
| S5 | Course compliance | Per D4–D7 outcomes: VM-facing interfaces, root targets, optional alert service, optional presentation project | Decisions; after S2–S4 | VMs, `CompositionRoot.cs`, `PageFactory.cs`, navigators |

Shared files (`GenerationCoordinator.cs`, `SummaryService.cs`, `CompositionRoot.cs`, `ExitCoordinator.cs`) force S1 → S2 → S3 → S4 to run in sequence. Per `AGENTS.md`, each slice uses its own task worktree, merges the latest track head before merging back, and runs the full suite. Every fix gets the reverse check from `TRACK.md:109`: revert the fix, and the new test must fail.

### 9.2 Validation criteria (deterministic; to be written, none run)

**Drafts (S3):**
- **V-H1 (run before S3 to confirm H1):** the existing `ExitTests.cs:249` setup (`discard: false`). After `second.BeginEditCommand`, assert `second.EditBody == "旧的草稿"`. **Prediction:** fails today (shows the version body); passes after S3. Then save a version without typing, and assert the saved version's body is `"旧的草稿"`.
- **V-H2 (confirm H2):** leave with a failed draft, reopen, and set an older version current. **After S3:** refused with `HasDraft` and the draft is kept. **Today, predicted:** succeeds, and the draft is lost at exit.
- **V-D3:** a gated fake store holds a draft write; `SaveEditAsync` is issued; the write is released. Expect no stored draft row and no memory entry afterwards.
- **V-D4:** a discard while a throttled save is pending. Expect no draft row after the throttle interval and at exit; a failed delete is retried at exit.
- **V-D5:** a page is disposed during a failing save, with no other page on the node. The exit flush retries and succeeds after the fault clears. This is the replacement for `ExitTests.cs:208`.

**Recovery (S2):**
- **V-R1:** a gated probe. While the probe is in flight, `Report` a new failure, then release the probe. Expect storage to stay blocked.
- **V-R2:** a participant enqueues a pending write after its flush pass and before `Report`. Expect not cleared (`HasPending`).
- **V-R3:** the coordinator's unsaved terminal write still fails while the probe succeeds. Expect not cleared.
- **V-R4:** layout, preference and trace write failures leave health unblocked.
- **V-R5 (per D2):** an answer queued while blocked does not start. After recovery it starts once; the fake client sees exactly one request.
- **V-R6:** exit while blocked. Queued work is cancelled, the failed exit can be retried or returned from, and return does not clear the block.
- **V-R7:** a summary save-version and an archive while blocked are refused with `StorageUnavailable`; reading and export still work.

**Contracts (S4):** the structural check in §5; existing network and naming tests unchanged.

**Local fixes:** per the table in §7.

**Performance numbers are measurements, not gates:** seeded projection and detail timings before and after S4, recorded as evidence for the F4c and N+1 triggers.

### 9.3 Decisions needed (user, or instructor where marked)

- **D1** Blocking matrix (§2.3), specifically:
  - whether archive/restore/delete and renames report and are gated;
  - whether draft failures stay non-blocking (recommended).
- **D2** Whether queued AI work is held while blocked (recommended) or fails fast (today).
- **D3** Summary-draft conflict UX when the baseline is no longer current: keep a conflicted draft with discard/copy (recommended), or drop it with a notice (today).
- **D4** (user + instructor) VM-facing application-service interfaces. A conflicts with `architecture.md:36`; see §10.
- **D5** (instructor) Whether typed routes and the workbench satisfy the "复刻" navigation requirement; whether a root target enum is needed.
- **D6** (instructor) Whether inline VM-state feedback satisfies "UI 反馈…封装为可注入 Service".
- **D7** (instructor, then user) Whether ViewModels must live in a UI-independent library.

### 9.4 Risks

- **S2 changes reporting points across services.** A missed `Report` keeps today's gap. **Mitigation:** V-R7, plus a review checklist of every `catch (StorageWriteException)`.
- **S3 moves timing** (the throttle moves from page to service). Existing exit/draft UI tests must keep passing, and the `ExitTests.cs:208/249` cases are rewritten to target `SummaryDrafts`, not deleted.
- **D2 changes visible behaviour:** a queued answer can wait indefinitely while blocked. The UI must show it paused, not queued as normal.
- **S5 (if D4-A) adds about 7 interfaces.** The risk is mechanical churn; limit it to the VM-facing boundary.

### 9.5 Explicit deferrals

- F3 decomposition (triggers in §4).
- F4c detail bodies and the `Entry` N+1 queries (measured triggers in §5).
- Multimodal and tool readiness (F5).
- Automatic reporting from the Infrastructure write queue (§2.7).

### 9.6 Completion criteria

- S1–S4 merged, with each new test passing and failing on revert, the full suite green on both CI platforms, and V-H1/V-H2 run first and their outcomes recorded (confirmed or refuted).
- D1–D3 answered by the user before S2/S3. D4–D7 answered before S5, which is optional until then.
- `TRACK.md` records the outcomes at merge time.
- Codex has reviewed this proposal, and the remaining disagreements are written down in `discussion.md`.

---

## 10. Proposed changes to shared documents (user decision; not adopted)

1. **`docs/architecture.md` §2 state table:**
   - add a row "存储健康 | Application 存储健康组件 | 核心写失败阻塞，参与者补存后探测恢复 | 布局／偏好／诊断失败不阻塞";
   - make the paused-operation list in §406 explicit (D1).
2. **`docs/requirements.md` §9.3:** state whether accepted, never-sent queued AI work waits during a storage pause (D2).
3. **`docs/architecture.md:36`, if D4-A is chosen:** add an explicit exception: "ViewModel 依赖的应用服务以接口暴露（课程要求），其余仍只在实际替换边界抽象".
4. **`docs/architecture.md` §11.2:** none needed; S4 brings the code into line with it.
