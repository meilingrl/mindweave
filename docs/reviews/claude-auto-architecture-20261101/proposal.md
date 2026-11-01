# Proposal: architecture remediation and course compliance (Claude, round 2)

Author: Claude · 2026-11-01 · reviewed commit `684314393c81a2eff2026f21c221a0371b8de9a7`.

**Revision history**
- **Round 2** (this file) revises round 1 in response to [Codex's review](/Users/meilingluo/Developer/Projects/mindweave.worktrees/codex-arch-review/docs/reviews/claude-auto-architecture-20261101/codex-review.md) (R1–R7).
- **Round 1** is frozen unchanged as [proposal-round1.md](proposal-round1.md) (SHA-256 `90c9ff13…f0`, the version Codex reviewed). §0 lists what changed.

This is a proposal. Nothing in it is implemented, and no test described here has been run. Failure paths come from reading the code; any not yet reproduced is labelled a **hypothesis** (H-n) and paired with the test that would confirm or refute it. Paths are relative to the repository root, with line numbers at the reviewed commit.

**ID prefixes:** F finding · L local defect · H hypothesis · D decision · C course requirement · S slice · V validation · R Codex review item.

---

## 0. Changes from round 1

| Codex item | Response | Where |
| --- | --- | --- |
| R1 draft failures are core | **Accepted.** Draft-write storage failures report and block, like other core writes. Recovery passes actively retry retained writes; `WhenRecoveredAsync` is removed. The matrix separates new mutations from persistence of retained content | §2.3 |
| R2 concurrency contract | **Accepted.** Replaced "`HasPending` under the health lock" with obligations counted inside `StorageHealth`, plus a failure epoch. The health lock is a leaf; the participant list is sealed before startup. Added H3, a race in the *current* code. Specified admission before each external request | §2.4–2.5 |
| R3 teardown order | **Accepted.** Explicit idempotent `StopAsync` before the database closes; `AppHost` added; the retry/return exit path keeps health alive | §2.6 |
| R4 draft state machine | **Accepted.** Explicit read outcomes; generation and revision checks for cold reads, stale pages, Set-during-commit and discard; node-gate scope and lock order. Corrected the round-1 error: discarding a candidate does **not** refuse an existing draft today | §3 |
| R5 version-identified excerpts | **Accepted.** Excerpts by version ID; projection DTO in Application; SQL projections checked through the central command helper; length unit is code points; thresholds marked provisional | §5 |
| R6 course | **Mostly accepted.** No instructor gates on engineering work; C1/C4 reclassified; C3 port mapping by consumer; universal reflection rule withdrawn. One point kept for the user (§8.3) | §8 |
| R7 local defects and order | **Accepted.** L3 now specifies a visible terminal outcome that never drops content; reproduction tests come before fixes; Codex's sequence adopted | §7, §9 |
| F5 wording | Accepted as **optional** | §6 |

---

## 1. Positions at a glance

| ID | Position | Action |
| --- | --- | --- |
| F1 | Accept: storage health moves to an Application-level owner. Domain-specific retained writes stay with their owners; obligations are counted centrally | A3 |
| F2 | Accept: summary drafts move to an Application-level owner with an explicit state machine; navigation loses all content-retention logic | A4 |
| F3 | Defer; reassess after A3 against the recorded triggers | — |
| F4a/b | Fix now: body-less list/network header and version-identified excerpts | A5 |
| F4c | Defer; triggers are provisional until measured | — |
| F5 | Acceptable scope boundary; comment clarification optional | — |
| C3 | Real partial gap: narrow, consumer-mapped VM-facing ports | A6 |

---

## 2. F1 · Storage health

### 2.1 Current code (unchanged from round 1, plus H3)

- **Ownership:** the health flag, failure counter, loop and events live in `GenerationCoordinator` (`GenerationCoordinator.cs:148-164`, `:1000-1088`).
- **Recovery:** it requires the coordinator's `_unsaved` queue to be empty (`:1077`).
- **Probe:** exposed via `IConversationStore.ProbeWriteAsync` (`IConversationStore.cs:133`).
- **Gaps** (see round 1 §2.1):
  - summary version commits and archive/restore/delete do not report;
  - `Pump` admits while blocked (`:494`);
  - `SaveAsync` blocks on any exception (`:985-996`).
- **H3 — a race in the current code (unreproduced).**
  - **The failure path never reports.** `FlushUnsavedAsync` dequeues a write and awaits it; if the write fails it is re-enqueued *without* calling `MarkStorageBlocked` (`:1098-1110`).
  - **Exit and recovery passes interleave.** The exit's `SaveUnsavedAsync` (`:439`) shares `_flushGate` with the loop. It can therefore run between the loop's flush pass and `ClearStorageBlocked`, which checks `_unsaved.IsEmpty` and the failure counter (`:1075-1080`).
  - **The sequence:**
    1. The exit flush dequeues item X; the queue is now empty, and X is still writing.
    2. The probe succeeds.
    3. `ClearStorageBlocked` sees an empty queue and an unchanged counter, so it clears.
    4. X fails and is re-enqueued.
  - **Outcome:** health reads healthy with an unsaved terminal write, and the loop has stopped. The exit itself reports the failure (the flush returns false), but "return" would admit new AI work.
  - **V-H3** reproduces it with a gated store (§9.2).

### 2.2 Ownership and dependency direction

```
Desktop VMs ──▶ IStorageStatus (IsBlocked, Changed)
ConversationService, SummaryService, SummaryDrafts, ReadingStateService, GenerationCoordinator
      │ Report / Retain / TryAdmit                     │ registered retry callbacks (sealed at Start)
      ▼                                                ▼
StorageHealth (Application/Storage) ──probe──▶ IStorageProbe ◀── SqliteDatabase (Infrastructure)
```

**Moves into `StorageHealth`:**
- the blocked flag;
- the failure epoch;
- the outstanding-obligation count;
- admission;
- the recovery loop and its interval;
- the probe call;
- the `Changed` event;
- lifecycle (start, stop).

**Stays with owners:**
- what was retained (the coordinator's terminal writes, drafts, pending deletes);
- each owner's flush gate;
- how a retained write is retried.

**Contracts:**

```csharp
public interface IStorageStatus { bool IsBlocked { get; } event EventHandler? Changed; }

public interface IStorageProbe { Task ProbeWriteAsync(CancellationToken ct); }

/// Something an owner keeps after a storage failure. Open from Retain until Resolve; Resolve is idempotent.
public sealed class StorageObligation { public void Resolve(); }

public sealed class StorageHealth : IStorageStatus
{
    public void Report(StorageWriteException failure);                // a failed write that keeps nothing (e.g. a refused question)
    public StorageObligation Retain(StorageWriteException failure);   // a failed write whose content the caller keeps
    public bool TryAdmit();                                           // false while blocked or closed: new external work is refused
    public void RegisterRetry(Func<CancellationToken, Task> retryRetained); // composition time only
    public void Start();                                              // seals registration; called before startup admits work
    public void CloseAdmission();  public void ReopenAdmission();     // exit stop step / "return"
    public Task StopAsync();                                          // idempotent; quiesces the loop (see §2.6)
}
```

The probe moves onto `IStorageProbe`, which `SqliteDatabase` implements. `IConversationStore.ProbeWriteAsync` is removed.

**Affected files:**
- `GenerationCoordinator.cs`, `ConversationService.cs`, `SummaryService.cs`, `ReadingStateService.cs`;
- `SummaryDrafts.cs` (A4) and `ExitCoordinator.cs`;
- `AppHost.cs`, `AppStartup.cs`, `CompositionRoot.cs`;
- `IConversationStore.cs`, `SqliteConversationStore.cs`, `SqliteDatabase.cs`, `InfrastructureServices.cs`;
- the VMs that read health or catch storage refusals: `ConversationViewModel.cs`, `ArchivedViewModel.cs`, `SummaryViewModel.cs`;
- tests constructing the coordinator.

### 2.3 Operation and failure matrix

Following R1, the core/non-core split is taken from the requirements, not chosen:
- "消息、来源、总结正文及版本、手写内容与草稿不可当作可丢缓存" (`docs/architecture.md:406`);
- only layout and page state may degrade (`docs/requirements.md:258`).

Small writes can succeed while larger ones fail (`SqliteDatabase.cs:144-151`, the probe rationale), so a core failure is reported where it happens. It is not left for a later write to detect.

| Write | Class | On `StorageWriteException` | While blocked |
| --- | --- | --- | --- |
| New question / conversation / retry rows | Core, new mutation | `Report`; input stays in the composer | **Refused** (`TryAdmit`) |
| Task row, mark-running | Core, new mutation | `Report` (as today) | Refused before the AI is called |
| Answer checkpoint | Core, retained content | `Report`; the newest text stays in `LiveAnswer` and is written by the next checkpoint or the terminal write | Allowed (persists retained content) |
| Answer terminal write, cancelled-queued terminal write | Core, retained content | `Retain`; resolved when the write commits | Retried by recovery and exit |
| Branch creation | Core, new mutation | `Report` | Refused |
| Generated summary version and task completion | Core | `Report`; the task fails visibly, no version (MW-C06) | Not started (admission) |
| Manual version save, adopt, set current, delete versions, discard candidate | Core, new mutation | `Report` (**new**) | **Refused** (new) |
| Archive, restore, delete conversation | Core, new mutation | `Report` (**new**) | **Refused** (new) |
| Conversation rename, summary rename | Core metadata, new mutation | `Report` (**new**) | **Refused** (new) |
| Automatic title, automatic names | Core metadata from AI | `Report` (as today for the title) | Not started (admission) |
| Message draft | Core, retained content | `Retain` per key; resolved when any later write of that key at or above the failed revision commits | Autosave keeps attempting (persists retained content) |
| Summary edit draft write, pending draft delete | Core, retained content | `Retain` per node (see §3) | Autosave and delete retry keep attempting |
| Reading positions, summary reading progress | Page state | Logged; no report | Allowed; may degrade |
| Network layout, preferences, last location | Page state / preference | Logged; no report | Allowed; may degrade |
| AI request traces | Diagnostic | Logged; no report (as today) | — |

**While blocked, still allowed:**
- editing in memory; reading, copying and exporting already-saved versions;
- stopping tasks;
- persisting retained content (autosave, retries, the exit save).

A universal gate on every write would stop recovery itself, so new mutations and retained-content persistence are gated separately.

**Two classes inside `ReadingStateService`** (`ReadingStateService.cs:127-160`): its flush currently treats every failure alike. Drafts move to the core-retained class; positions stay degradable.

**Starvation:**
- Ordinary debounce dirtiness is **not** an obligation. Only a failed write opens one.
- Continuous typing therefore cannot hold recovery open. Each successful write of the newest content resolves the key's obligation, and a new failure only reopens it.

### 2.4 Recovery synchronization

All health state sits behind one private lock, the **health lock**:
- `blocked`;
- `epoch` (incremented on every `Report` and `Retain`);
- `outstanding` (open obligations);
- `admissionClosed`;
- `stopped`.

**Invariants (Codex R2 1–5):**
1. **An obligation counts until its content commits.** `Retain` increments `outstanding` inside the health lock *before* returning. Owners attach the obligation to the retained item, and only the code that commits that item calls `Resolve`. An item dequeued and still writing therefore keeps `outstanding > 0`, whatever the owner's queue looks like. This closes H3.
2. **Linearization point:** `Retain` and `Report` update `epoch`/`outstanding`/`blocked` under the health lock. A failure that happens after a recovery pass reads `epoch` changes it, so the clear refuses (step 4 below). A failure after a clear re-blocks synchronously at that `Report`/`Retain` call. Admission for any later request goes through `TryAdmit` under the same lock, so nothing is admitted on stale health.
3. **No stale observations.** A pass reads `e0 = epoch`, then without any lock:
   1. runs every registered retry callback (each owner under its own gate);
   2. probes.

   It then clears, under the health lock, only if `epoch == e0 && outstanding == 0 && !stopped`. Both values are the live current state, never a cached participant observation.
4. **Lock order:**
   - The health lock is a **leaf**: no IO, no awaits, no callbacks while holding it.
   - Owners may call `Report`/`Retain`/`Resolve`/`TryAdmit` while holding their own locks or gates.
   - `StorageHealth` invokes owner callbacks only from the loop, without holding the health lock. `Changed` is raised after release.
   - `RegisterRetry` after `Start()` throws. Registration happens in composition and `AppStartup` before any work is admitted.
5. **Not every failure is a storage failure.** Domain conflicts (`SummaryRefusedException`, archived or missing conversation) and programming errors never call `Retain`/`Report`, and are never resolved by a probe. Their content is kept by the owner in a visible state (§3 conflict, L3 below). A successful probe resolves nothing by itself: only commits resolve obligations.

**Deterministic tests** (V-R1–R9, §9.2) cover:
- an exit flush holding a dequeued write in flight while recovery probes (H3);
- a failure published between the pass's retries and its clear;
- concurrent `Report`/flush/exit without deadlock.

### 2.5 Admission (D2)

- **Queued work.** `Pump` dequeues only if `TryAdmit()`. Accepted, never-sent work stays queued while storage is blocked, and resumes once with its frozen input when `Changed` reports healthy. This does not revive anything: interrupted, cancelled and failed tasks are never restarted. Exit cancels queued work as today.
- **Admission before every external request.**
  - Admission is checked again immediately before each external request inside a running task: every compression request in `ContextCompressor.PrepareAsync`, the main answer/summary/title/naming request, and the model-settings test request.
  - If refused at that point, the task ends `Failed(StorageUnavailable)` without sending. Its terminal write follows the matrix: `Retain` if it fails.
  - It does not hold its slot waiting; that keeps exit bounded.
  - A request already in flight finishes. Its admission is not revoked retroactively.
- **Today is not a reliable fail-fast option** (Codex R2). Mark-running can succeed on a nearly full disk and then send. The alternative to holding queued work would therefore need explicit cancellation of queued work when blocking starts. I do not recommend it.
- **UI:** a held queued answer shows "paused: storage" rather than plain "queued".

### 2.6 Lifecycle and teardown

Today `AppHost.DisposeAsync` does the following (`AppHost.cs:24-55`):
1. `GenerationCoordinator.ShutdownAsync`, which stops the coordinator's own retry loop and flushes `_unsaved`;
2. the reading flush;
3. `SqliteDatabase.CloseAsync`;
4. provider dispose;
5. instance release.

Once the loop leaves the coordinator, it must be stopped explicitly. Disposal order in DI is not a lifecycle guarantee.

**Startup:** composition registers retry callbacks; `AppStartup` calls `StorageHealth.Start()` before showing the workbench or admitting work.

**Exit (ExitCoordinator)** — health and owners stay alive throughout:
1. **Stop step:** `CloseAdmission()` plus the coordinator's existing `StopForExitAsync` (bounded by `ExitOptions.StopWait`). Tasks that time out follow the current policy: given up, and recorded as interrupted on the next start (`ExitCoordinator.cs`, `AppHost.cs:30-33`).
2. **Save step:**
   - `SaveEverythingAsync` (`ExitCoordinator.cs:229-237`): exports idle, `SummaryDrafts.FlushAsync`, `ReadingStateService.FlushAsync`, `generation.SaveUnsavedAsync`, then `DrainAsync`.
   - It fails if anything retained is still unresolved.
   - On failure the window offers retry or return. **Return** calls `ReopenAdmission()` and `Reopen()`. It does not clear `blocked` (health stays blocked until a real recovery).

**Release (AppHost, last resort; every step idempotent):**
1. `CloseAdmission()`; coordinator `ShutdownAsync(wait)` (as today).
2. `await StorageHealth.StopAsync()`: set `stopped`, cancel the loop, and await an in-flight pass, including a probe or retry callback already running. After this, health starts no IO.
3. Final drain: the registered retry callbacks run once more, directly and sequentially (summary drafts, message drafts, coordinator terminal writes), then `DrainAsync`.
4. `SqliteDatabase.CloseAsync()`.
5. Provider dispose. Owners' `DisposeAsync` must do no IO; the coordinator's is already a no-op after shutdown.
6. Single-instance release (as today, in `finally`).

**Validation:** V-R8 holds a probe in flight at release, with a fake database that throws after close. The probe completes before close, and no database access happens after close.

### 2.7 Schema, compatibility, alternatives

- **Schema:** none.
- **Visible behaviour changes:**
  - more operations are refused while blocked (§2.3, rows marked new);
  - queued work is held (§2.5);
  - draft failures now pause AI requests. That last one is the current requirement, applied consistently.
- **Alternatives considered:**
  1. **Keep health in the coordinator.** Rejected: summary/archive commits would still depend on the AI module.
  2. **Auto-report from the Infrastructure write queue.** Rejected for now: it would block on layout/preference writes, and classification is an Application decision.
  3. **A per-participant `HasPending` check.** The round-1 design; rejected per R2, because a queue that looks empty is not proof that nothing is in flight.
  4. **A general workflow or transaction framework.** Disproportionate.

---

## 3. F2 · Summary edit drafts

### 3.1 Current code

- **Ownership today:**
  - The page owns the content, the revision counters and the throttle (`SummaryViewModel.cs:80`, `:89-90`, `:105`, `:513-549`).
  - `Workbench` retains dropped pages, retries their saves, and decides precedence (`Workbench.cs:21`, `:206-238`, `:245-300`).
  - `IPageFlush` has one implementer (`PageViewModel.cs:17-30`).
  - `SummaryService.SaveDraftAsync` passes straight to the store (`SummaryService.cs:266-267`).
- **Store rules:**
  - Draft writes are refused with `Changed` when the baseline is no longer current (`SqliteSummaryStore.cs:186-189`).
  - **Only adopt, set current and delete versions refuse an existing stored draft** (`SqliteSummaryStore.cs:88`, `:119`, `:143-145`). Discarding a candidate does not, and round 1 was wrong to list it.
- **Hypotheses (unreproduced):**
  - **H1:** reopening after a failed save shows the older stored draft, and a commit without typing then discards the newer leftover.
  - **H2:** after a failed save, a version switch passes the store's stored-row check; the leftover is later refused with `Changed`, treated as settled (`SummaryViewModel.cs:534-548`), and lost.

### 3.2 Owner and state

**`SummaryDrafts`** (`src/Mindweave.Application/Summaries/SummaryDrafts.cs`) is a singleton. Each node touched in this run has a record that lives for the app's lifetime and holds:

| Field | Meaning |
| --- | --- |
| `Generation` | Incremented by every commit and every discard. An edit session is bound to the generation it opened under |
| `Revision` | Monotonic per node, incremented by every accepted `Set`; never reset |
| `Content?` | `(Baseline, Title, Body)` or none |
| `SavedRevision` | Highest revision known to be stored |
| `Status` | `None` · `Clean` (memory equals the stored row) · `Dirty` (newer than stored) · `Failed` (a storage failure is open; carries a `StorageObligation`) · `Conflict` (the store refused the baseline; content kept) · `Deleting` (user discarded; delete not yet confirmed; tombstone, content dropped by user intent; carries an obligation if the delete failed) |

**Draft identity:** `SummaryNodeId`, one draft per node, exactly the schema's `summary_edit_draft` keyed by `node_id`. No schema change.

**Locks:**
- A per-node async **gate** covers capture → durable write → state update. It never covers `Changed`, naming or other re-entrant work.
- A short in-memory **record lock** covers field reads and updates, with no IO or awaits.
- **Lock order:** node gate → record lock → health lock (leaf). Never two node gates at once.
- Database calls happen under the node gate only, never under the record lock.

**Contract:**

```csharp
public abstract record DraftRead
{
    public sealed record None(long Generation) : DraftRead;
    public sealed record Draft(long Generation, long Revision, SummaryVersionId Baseline, string Title, string Body) : DraftRead;
    public sealed record Conflict(long Generation, long Revision, SummaryVersionId Baseline, string Title, string Body) : DraftRead;
}

public sealed class SummaryDrafts
{
    Task<DraftRead> OpenAsync(SummaryNodeId node, SummaryVersionId current, CancellationToken ct);
    SetResult Set(SummaryNodeId node, long generation, SummaryVersionId baseline, string title, string body); // Accepted(rev) | StaleSession
    Task<bool> FlushAsync(CancellationToken ct);                 // every Dirty/Failed/Deleting node; false if any stays unresolved
    Task<CommitResult> CommitAsync(SummaryNodeId node, long generation, long revision, CancellationToken ct);
    Task<DiscardResult> DiscardAsync(SummaryNodeId node, long generation, long revision, CancellationToken ct);
    Task<T> RunVersionOperationAsync<T>(SummaryNodeId node, Func<Task<T>> storeOperation, CancellationToken ct); // adopt, set current, delete versions
}
```

`SummaryService` keeps the use cases and calls `SummaryDrafts` for the draft-sensitive parts. The page holds only the editor text, its session `(Generation, Revision)` and `IsEditing`.

### 3.3 Transition table

| Event | Under | Rule |
| --- | --- | --- |
| **OpenAsync** (begin edit) | Record lock, then a cold read without locks, then the record lock again | Memory content in `Clean`/`Dirty`/`Failed` → `Draft`. `Conflict` → `Conflict`. `Deleting` → `None` (the tombstone suppresses the stale stored row). No content → note `(g0, r0)`, read the stored draft, then under the record lock: if `Generation != g0` or `Revision != r0`, drop the read result and answer from memory (a Set, commit or discard won the race). Otherwise install `Clean` from the stored row (or `None`). A stored row whose baseline ≠ `current` is installed as `Conflict`, never shown as absent |
| **Set** | Record lock (synchronous) | Session generation ≠ `Generation` → `StaleSession` (the page keeps its text, says the draft was saved or discarded elsewhere, and offers copy/reopen). Otherwise `Revision++`, replace content, status `Dirty` (a `Failed` entry keeps its obligation), schedule a write (1 s debounce, as today). From `Deleting`: the new session's content replaces the tombstone; the pending delete is cancelled because the upsert overwrites the row |
| **Write** (debounce, `FlushAsync`, recovery retry, exit) | Node gate | Capture `(g, r, content)` under the record lock. Write `SaveDraftAsync`. **Success:** under the record lock, if `Generation == g`: `SavedRevision = max(SavedRevision, r)`; status `Clean` if `Revision == r`, else stays `Dirty`; resolve the obligation if `r ≥` its failed revision. **`StorageWriteException`:** status `Failed`; `Retain` if no obligation is open, else `Report`. **`SummaryRefusedException(Changed)`:** status `Conflict`, content kept, no obligation; the page and the conversation header show it |
| **Write of a `Deleting` node** | Node gate | Retry `DeleteDraftAsync` only if status is still `Deleting` when the gate is acquired. Success: status `None`, resolve the obligation. Failure: `Retain`/`Report` |
| **CommitAsync** (save as version) | Node gate | Under the record lock: refuse `StaleSession` if `Generation != g` or `Revision != r` (a newer Set from another page exists). The page disables its editor while committing, so it can't race itself. Write `SaveEditAsync` (version plus draft-row delete in one transaction, `SqliteSummaryStore.cs:202-225`). **Success:** under the record lock, if `Revision == r`: content removed, status `None`, obligation resolved, `Generation++`. If `Revision > r` (only possible from another page's Set during the IO): `Generation++`, and the newer content is kept as `Conflict` (its baseline is no longer current), never rebased or dropped. **`StorageWriteException`:** `Report` (a new mutation failed; content unchanged). **Refused:** notice; content unchanged |
| **DiscardAsync** | Node gate | Refuse `StaleSession` on a generation/revision mismatch (a stale page cannot discard newer input). Otherwise: content dropped (user intent), status `Deleting`, `Generation++`. Delete the row: success → `None`; failure → `Retain`, and keep the tombstone so stale reads and stale scheduled writes are suppressed |
| **RunVersionOperationAsync** (adopt, set current, delete versions only) | Node gate around the store call only | Refuse `HasDraft` if status is `Dirty`, `Failed` or `Conflict` (real unsaved input). If `Deleting`, retry the delete first; on failure refuse `StorageUnavailable`. `Clean` and stored-row cases are left to the store's transactional `ThrowIfDraft`, which is unchanged. A page open in edit mode with no edits is not a draft. **After the gate is released**, `SummaryService` runs `CheckNameAsync`, naming scheduling and `Changed` (today they run inside the operation callback, `SummaryService.cs:357-379`; this is restructured so they run outside the gate) |
| **Discard candidate, rename, read state** | Not gated by drafts | Unchanged semantics: they do not refuse drafts today |
| **Page disposal** | — | Nothing. The newest text is already in `SummaryDrafts` |
| **Exit / release** | §2.6 | `FlushAsync` during the save step; one final retry before the database closes |

**What `Workbench` loses:**
- `_dropped`, `DroppedPage`, `OnDraftSettled`;
- the `Track` subscription;
- the page part of `FlushAsync` (it keeps the last-location write);
- `IPageFlush` itself.

`ExitTests.cs:208/249` are re-targeted at `SummaryDrafts` with the same behavioural intent: a left draft is kept and retried, and an older leftover never overwrites newer input or a discard.

**Single draft per node, no new product behaviour:**
- Pages on the same node share one record.
- An older page's `Set`, commit or discard after a newer generation is refused visibly instead of silently winning.
- No live sync between editors, multi-editor UI or multi-window behaviour is added.

### 3.4 Alternatives

- **Page plus a `Workbench` leftover lookup.** Rejected: it deepens navigation's role.
- **Drafts inside `SummaryService`.** Possible, but a separate owner mirrors `ReadingStateService` and keeps the gate scope auditable.
- **A generic draft framework.** Not justified.

---

## 4. F3 · Scheduler breadth (deferred)

Unchanged from round 1 §4, and Codex agreed. Reassess after A3, when the health code has left the coordinator. **Triggers:**
1. a fifth task kind;
2. more than one external request per task beyond compression;
3. further per-kind policy divergence;
4. a change in one executor forcing edits in another's error handling.

L2 and L3 stay local fixes.

---

## 5. F4 · Summary read contracts

**Constraint:** "不读取全部总结正文来绘制网络" (`docs/architecture.md:380`). Bodies are unbounded, because manual edits have no cap. Workspace sizes and version counts are unmeasured.

| | (a) `Entry` loads every version | (b) Lists/network carry the current body | (c) Detail loads every historical body |
| --- | --- | --- | --- |
| Exists | Yes (`SqliteSummaryStore.cs:330`, `:387`) | Yes (`ISummaryStore.cs:11`; `Summary.cs:69-81`) | Yes (`ISummaryStore.cs:14-31`) |
| Violates current design | Yes | Yes, even after (a) | No stated rule |
| Action | Fix now (A5) | Fix now (A5) | Defer; trigger provisional |

**Contract:**
- `SummaryVersionHead(Id, Seq, Title, ShortTitle, ToSeq, Origin, CreatedAt)` lives in `src/Mindweave.Application/Storage/ISummaryStore.cs`, next to `SummaryEntry`. Placing it in Core is not needed.
- `SummaryEntry.Current` becomes `SummaryVersionHead`.
- `SummaryRules.Markers` takes `int toSeq`. `ShortTitleOf` takes `(title, shortTitle)`; a thin overload keeps `SummaryVersion` callers working.
- `Entry` selects only the current version's head columns.

**Version-identified excerpts:**
- **Contract:** `Task<IReadOnlyDictionary<SummaryVersionId, string>> ExcerptsAsync(IReadOnlyCollection<SummaryVersionId> versions, int maxCodePoints, CancellationToken ct)`, which runs `SELECT id, substr(body, 1, $max) FROM summary_version WHERE id IN (…)`.
- **Naming flow:** read the group (heads carry version IDs) → request excerpts for exactly those version IDs → build the prompt.
- **Group members that changed:**
  - A member whose version no longer exists is dropped from the group; if fewer than two remain, naming is skipped.
  - A member whose node moved to a newer current version still gets its group-time excerpt. The existing commit guard (`NamingExpectation(Node, Current, DisplayName)`, `ISummaryStore.cs:65`, `SqliteSummaryStore.cs:271-294`) discards that node's name at apply time. No new rule is needed.
- **Length unit: Unicode code points.**
  - SQLite `substr` on TEXT counts characters (code points), so a surrogate pair is never split.
  - Today's `body[..600]` counts UTF-16 units and can split one (`SummaryService.cs:435`). That is local defect **L7**, fixed by the same change.
  - The test includes non-BMP text at the boundary.

**Query evidence, not just shape:**
- **Type test:** `SummaryVersionHead`/`SummaryEntry` have no body member.
- **Projection test:**
  - Commands are created centrally (`SqliteConnections.cs:64-89`), so add an internal command-text observer that is active only in tests (Infrastructure already exposes internals to its tests).
  - **Assert:** `NetworkService.ProjectAsync` executes no statement that selects `body` from `summary_version`. Naming selects `body` only through `substr(body, 1, $max)` with an `id IN` filter.
- **Concurrency test:** change a member's current version between the group read and the excerpt read. The prompt uses the group-time version's excerpt, and apply discards the changed node's name.
- **Existing tests:** network and naming tests keep their behavioural assertions; their types may change.

**Deferred, triggers provisional:** F4c and the `Entry` N+1 queries. The thresholds (detail > 100 ms at 50 versions × 50 KB; projection > 50 ms at 500 nodes) are suggestions, not accepted criteria. Each measurement records hardware, build configuration, seeded shape and repetitions before it is used as a trigger.

---

## 6. F5 · Text-only pipeline

This is an acceptable scope boundary (round 1 §6). `ContentBlock.cs:3-6` already says "Only text exists today". Adding "requests, streaming and live answers are text-only" is **optional**, and it is recorded in `TRACK.md` at merge.

---

## 7. Local defects

| ID | Defect | Fix specification | Validation |
| --- | --- | --- | --- |
| L1 | Summary page commands don't handle `StorageWriteException` (`SummaryViewModel.cs:560-572`). Process termination is **unverified** | Catch it; show the storage notice; keep editor content; report through the service (after A3, via `StorageHealth`) | A fault-injected save-version shows the notice, keeps the text, and the process survives; also confirm or refute the termination |
| L2 | Titles ignore truncation and duplicate the stream loop (`GenerationCoordinator.cs:706-765`) | Use `RequestCompleteTextAsync` | A fake client returns `Truncated`: no title stored, task failed |
| L3 | Any exception in a terminal write blocks storage (`:985-996`) | `StorageWriteException` → `Retain` (as specified in §2.3). Any other exception → the answer is **not** dropped: `LiveAnswer` stays terminal with its text; the message shows "could not be saved" (a visible terminal outcome); the write is kept in the coordinator's retained list without opening a storage obligation; exit reports it unsaved; nothing falls through to `RunAsync`'s log-only catch (`:532-537`) leaving a durable running task | A fake store throws `InvalidOperationException` on the terminal write: health not blocked; the answer is visible as unsaved with its text; the task row is not left running in memory; exit reports unsaved content |
| L4 | Queued work admitted while blocked | §2.5 | V-R5 |
| L5 | `Avalonia.Thickness` in two VMs (`ConversationListViewModel.cs:52`, `ArchivedViewModel.cs:25`) | Optional cleanup: expose `Depth`; convert in the view | — |
| L6 | `AnswerView` reaches its ancestor's `DataContext` (`AnswerView.axaml.cs:177-185`) | Optional cleanup | — |
| L7 | Naming excerpt can split a surrogate pair (`SummaryService.cs:435`) | Fixed by A5's code-point excerpt | Non-BMP boundary test |

L5 and L6 are optional presentation cleanup, not prerequisites.

---

## 8. Course architecture compliance (revised per R6)

**Source:** `期末项目与报告要求.docx`, section "技术与架构要求". It allows equivalent technology ("只需要反映对应的思想，但无需采用一模一样的技术") and marks navigation "(要求复刻)". **Reference:** DailyPoetryA at git `9479829`. The instructor is the final grading authority. Unresolved interpretations are recorded as **residual risk**; they do not gate engineering work.

### 8.1 Matrix

| ID | Requirement (short quote) | Evidence | Class | Remedy or report treatment |
| --- | --- | --- | --- | --- |
| C1 | "Views/ 仅承载 XAML 与极薄 code-behind" | Code-behind holds geometry, hit testing, reading-position capture, selection→anchor and Markdown rendering (`ConversationView.axaml.cs`, `AnswerView.axaml.cs`, `SummaryView.axaml.cs`); these are View responsibilities (`architecture.md:67`). No business logic or persistence in views. One narrower coupling: `AnswerView` reads its ancestor's `DataContext` (L6) | **Satisfied** (by responsibility, not line count) | Explain the responsibilities in the report. L6 optional |
| C2 | "SetParameter(object parameter)" … "要有类似机制和设计" | Typed `ContentRoute` delivered through `PageFactory` into the VM constructor; VMs never see the stack | **Satisfied** | Report: equivalence explanation. Optional test: parameter delivery and stack independence |
| C3 | "ViewModel 通过构造函数注入…拿到接口引用，避免硬依赖实现类型" | VMs and VM-level coordinators take concrete `ConversationService`, `SummaryService`, `ReadingStateService`, `ModelSettingsService`, `AppearanceService`, `SummaryExportService`, `WorkspaceService`, `NetworkService` (through `NetworkSession`) and `GenerationCoordinator` | **Partially satisfied** | Narrow, consumer-mapped ports (§8.2). This is the one course gap that needs code |
| C4 | "以 ServiceCollection 通过 IoC 容器集中组装" | `CompositionRoot.Build` (`ValidateOnBuild`) plus `AddMindweaveInfrastructure`; `PageFactory` and `AppStartup` build VMs from injected dependencies | **Satisfied** | None. The heading's "全依赖注入化?" ends in a question mark and does not show that every object must be resolved from the container |
| C5 | Root/Menu/Content interfaces, "XxxNavigationConstant 常量类", stack in the main VM, "接口定义能力、ViewModel 维护状态" "(要求复刻)" | Root/Menu/Content contracts exist (`Navigators.cs`). Typed targets (`MainSection`, `ContentRoute`) serve the purpose of centralized names. The stack owner (`Workbench`) keeps the state; interfaces expose capabilities only. **Most concrete difference:** the root receives a VM instance (`RootViewModel.cs:18`; `AppStartup.cs:47-111`) instead of a target | **Satisfied in substance**; **residual literal-grading risk** | Report: an explicit equivalence map (below). Optional bounded improvement: a `RootTarget` enum plus a root page factory. No navigation rewrite |
| C6 | "ViewLocator … 实现了 View 的自动渲染" | Explicit-registry `ViewLocator : IDataTemplate` | **Satisfied** | Report: why an explicit registry rather than reflection |
| C7 | "文件 IO、网络 IO、UI 反馈全部封装为可注入 Service" … "使用异步操作避免 UI 卡死" | IO: async stores, a single writer, streaming client, file writer. **Imperative UI side effects** are already behind `ISystemShell` (`PickExportFileAsync`, `OpenFolder`, `ExitApplication`). **Feedback** (notices, confirmations, exit and layout prompts) is VM state rendered inline; there are no imperative alert/dialog calls to port | **Satisfied**, with an explanation | Report: map every UI side effect to `ISystemShell`, and state that feedback is bindable VM state. A new port is warranted only if an imperative dialog is introduced |
| C8 | "Library/UI 双工程目录即抽象说明书" "(单向依赖)" | One-way layering enforced by `LayeringTests.cs`; Core/Application are UI-independent. VMs sit in the Avalonia project; the reference library is not framework-free either (`AvaloniaInfiniteScrolling.Collection` in `DailyPoetryA.Library`) | **Satisfied in substance**; residual risk only if literal placement is demanded | Report: reusable Core/Application versus presentation-specific VMs. L5 optional. No `Presentation` project by default |

**C5 equivalence map:**

| DailyPoetryA | Mindweave |
| --- | --- |
| `IRootNavigationService.NavigateTo(view)` | `IRootNavigator.Show(page)` |
| `IMenuNavigationService.NavigateTo(view, param)` | `IMenuNavigator.Navigate(NavigationRequest)` |
| `IContentNavigationService.NavigateTo(view, param)` | `IContentNavigator.Push/Replace(ContentRoute)` |
| `RootNavigationConstant` | *(none: instances)* |
| `MenuNavigationConstant` | `MainSection` |
| `ContentNavigationConstant` | `ContentRoute` types |
| `MainViewModel.ContentStack / PushContent / GoBack` | `Workbench` stacks / `Push` / `GoBack` (per workspace, slot and section) |

### 8.2 C3 port mapping (consumer → operations actually used)

The principle: each port is declared by what its consumers call, not by mirroring a class. Most current services are already grouped by use case, so several ports coincide with one service's VM-facing subset. Members VMs don't use stay off the ports.

| Port (proposed name) | Operations | Consumers | Implemented by |
| --- | --- | --- | --- |
| `IConversations` | `ListAsync`, `LoadAsync`, `GetContextUsageAsync`, `GetSourceAsync`, `ListBranchesAsync`, `GetLineageAsync`, `ArchivedAmongAsync`, `Observe`, `HasUnfinishedAnswer`, `StartAsync`, `AskAsync`, `RetryAsync`, `StopAsync`, `CreateBranchAsync`, `RenameAsync`, `ArchiveAsync`, `PlanRestoreAsync`, `RestoreAsync`, `DeleteAsync`; events `ConversationChanged`, `VisibilityChanged`, `ModelChanged`, `ActivityChanged` | `ConversationViewModel`, `NewConversationViewModel`, `ArchivedViewModel`, `NetworkHomeViewModel` (list and events only), `ConversationListViewModel` | `ConversationService` (it forwards `HasUnfinishedAnswer` and an activity event, removing `ConversationListViewModel`'s direct coordinator dependency) |
| `ISummaries` | `ForConversationAsync`, `LatestTaskAsync`, `GetAsync`, `SummarizeAsync`, `StopAsync`, `CheckAdoptAsync`, `AdoptAsync`, `DiscardAsync`, `SetCurrentAsync`, `DeleteVersionsAsync`, `SaveEditAsync`, `SetReadAsync`, `RecordProgressAsync`, `RenameAsync`, draft operations (via `SummaryDrafts`, §3); event `Changed` | `SummaryViewModel`, `ConversationViewModel`, `NetworkHomeViewModel` (event) | `SummaryService` (+ `SummaryDrafts`) |
| `IReadingState` | `GetPositionAsync`, `SetPosition`, `GetDraftAsync`, `SetDraft`, `FlushAsync` | `ConversationViewModel`, `ExitCoordinator` | `ReadingStateService` |
| `ISummaryExport` | `SuggestFileNameAsync`, `ExportAsync`, `WhenIdleAsync` | `SummaryViewModel`, `ExitCoordinator` | `SummaryExportService` |
| `IModelSettings` | `GetAsync`, `SaveAsync`, `TestAsync`, `DiscoverModelsAsync` | `ModelSettingsViewModel` | `ModelSettingsService` |
| `IWorkspaces` | `CreateAsync`, `RememberLocationAsync` (+ list at startup) | `ShellViewModel`, `Workbench`, `AppStartup` | `WorkspaceService` |
| `IAppearance` | Members used by `SettingsHomeViewModel` | `SettingsHomeViewModel` | `AppearanceService` |
| `INetworkLayout` | `ProjectAsync`, `GetLayoutAsync`, `PlaceAsync`, `SaveLayoutAsync` | `NetworkSession` | `NetworkService` |
| `IGenerationExit` | `UnfinishedUserWork`, `StopForExitAsync`, `SaveUnsavedAsync`, `Reopen` | `ExitCoordinator` | `GenerationCoordinator` |
| `IStorageStatus` | `IsBlocked`, `Changed` | `ConversationViewModel`, summary/archive pages | `StorageHealth` (F1) |

That is ten interfaces with no new implementation classes; each is implemented by an existing service.

**Bounded test.** Not the universal reflection rule (withdrawn): no constructor parameter of a type under `ViewModels/`, `Navigation/` or `Network/` may be a non-record class declared in `Mindweave.Application` or `Mindweave.Infrastructure`. Child VMs, records (`AppEnvironment`), options, `TimeProvider` and Desktop UI ports stay allowed.

### 8.3 The one point I keep for the user (partial disagreement with R6)

I accept Codex's point that a VM-facing use-case boundary *can* count as a "实际替换边界" under `docs/architecture.md:36`: VM tests would substitute these ports, so no main amendment is required by default.

**Counterpoint:** ten ports that each coincide with a service's VM-facing subset come close to what §36 calls "逐类接口". Whether a test-substitution boundary is a replacement boundary under §36 is an interpretation of shared architecture.

**Proposal:** record that interpretation in this track's `TRACK.md` as a finding, and leave `main` unchanged unless the user wants the wording clarified. This is information for the user, not a gate on A6.

### 8.4 Out of scope and evidence gaps

- **Out of scope:** page count, interface and class counts (≥ 70%), the source-only zip under 10 MB, the PDF report and template, platform acceptance, and overall grading. No overall course acceptance is claimed.
- **Residual risks:** literal-grading interpretation of C5 and C8. Ask the instructor only if the user wants certainty; the answer would change only the optional `RootTarget` or a VM project move.

---

## 9. Unified plan

### 9.1 Slices (Codex's sequence adopted; serial integration; none executed)

| Slice | Kind | Content | Depends on |
| --- | --- | --- | --- |
| A0 | Docs | Settle this round; the user approves the operation matrix (§2.3) and queue holding (§2.5) | — |
| A1 | Baseline reproduction | Deterministic tests for H1, H2, H3, L1 (termination), L3, and V-R1/R2 in their **current-code** form. Record each outcome as confirmed or refuted. A test that passes today documents a refuted hypothesis; failing tests land together with their fix, never red on the track | A0 |
| A2 | Local fixes | L1 (visible handling; reporting completed in A3), L2, L3 (as specified), L7 (with A5 if simpler) | A1 for each defect's reproduction |
| A3 | Architecture F1 | `StorageHealth` with registration, admission, obligations and teardown as one vertical slice; reporting from every row of §2.3 including both draft paths (`ReadingStateService` drafts; summary drafts through `SummaryService.SaveDraftAsync` until A4 moves them) | A2 (shared `GenerationCoordinator.cs`) |
| A4 | Architecture F2 | `SummaryDrafts`; remove `Workbench` retention and `IPageFlush`; re-target the exit tests | A3 (obligations, teardown) |
| A5 | Architecture F4a/b | Head DTO, version-identified excerpts, projection observer | A4 (shared `SummaryService.cs`) |
| A6 | Course C3 | Ports of §8.2, the bounded constructor test, the matrix recheck, the report explanations for C1/C2/C5–C8 | A3 (`IStorageStatus`), A4 (draft operations on `ISummaries`) |

These slices share files, so they are integrated one after another; parallel worktrees are unnecessary. Each slice runs in its own task worktree per `AGENTS.md` and merges the latest track head before merging back. The reverse check (undo the fix; the test must fail) is done only inside the implementer's own workspace.

### 9.2 Validation criteria (to be written; none run)

**Storage health (A3):**
- **V-R1:** a held probe; `Report` during the probe → stays blocked.
- **V-R2:** `Retain` between the pass's retries and its clear → stays blocked.
- **V-H3 / V-R3 (H3):** a gated store holds a dequeued terminal write in the exit flush while the loop probes → health does not clear. **Today: predicted to clear (hypothesis).**
- **V-R4:** a non-core failure (layout, preference, position, trace) leaves health unblocked; a message-draft or summary-draft failure blocks.
- **V-R5:** an answer queued while blocked does not start; it starts exactly once after recovery (the fake client counts requests); a compression request inside a running task is refused when blocked mid-task, and the task fails visibly without sending.
- **V-R6:** exit while blocked → retry, then return: admission reopens, health stays blocked, nothing revived.
- **V-R7:** every "refused while blocked" row in §2.3 is refused; reading, copying and export of saved versions work.
- **V-R8:** release with a held probe and a fake database that throws after close → the probe completes before close; no access after close.
- **V-R9:** concurrent `Report`, owner flush and exit flush complete without deadlock within a bounded timeout, repeated N times with fixed interleavings via gates (not sleeps).

**Drafts (A4):** every step uses explicit gates for the failing write and a controllable `TimeProvider` or direct `FlushAsync` calls; no reliance on timers.
- **V-H1:** the failed save completes before storage is restored. On reopen, `OpenAsync` returns the newer text. Committing without typing stores that text.
- **V-H2:** with an unsaved (failed) draft, set-current is refused with `HasDraft`; the draft survives to exit.
- **V-D1 cold read vs Set:** gate the store read, `Set` during it → the read result is discarded and the memory text is returned.
- **V-D2 captured write vs discard:** gate the draft write after capture, discard, release → no row resurrected; the tombstone suppresses the stale write.
- **V-D3 Set during commit:** gate `SaveEditAsync`; another page's `Set`; release → version stored with the captured text; the newer text kept as `Conflict`.
- **V-D4 stale page:** an older page's commit or discard after a newer generation → `StaleSession`, no data change.
- **V-D5 failed discard:** the delete fails; a new session `Set`s → the upsert wins; the delete is not retried over it.
- **V-D6:** a page disposed during a failing save; exit retries after the fault clears (the `ExitTests.cs:208` intent).
- **V-D7:** an older dropped leftover never overwrites a newer draft or a discard (the `ExitTests.cs:249` intent).

**Contracts (A5):** the type test, the projection observer test, the concurrent version-change naming test, and the non-BMP excerpt test (§5).

**Local fixes:** per §7.

**Course (A6):** the bounded constructor test; existing layering and `ViewLocator` registry tests; the matrix rechecked against code, with report text for each C row.

### 9.3 Decisions

| ID | Item | Owner | Recommendation |
| --- | --- | --- | --- |
| D1 | Operation matrix (§2.3): new refusals while blocked (version commits, archive/restore/delete, renames) | User approves the plan | Adopt. It applies §9.3's "依赖提交的操作" consistently. Draft-failure reporting is **not** a user choice; it follows the requirement |
| D2 | Hold accepted, never-sent queued work; per-request admission | User approves the plan | Adopt (§2.5) |
| D3 | Conflicted drafts are kept and shown, never silently dropped or rebased | Agreed with Codex | UX wording to be settled in A4 |
| D4 | Narrow VM-facing ports (§8.2) | User approves the plan | Adopt. Record the §36 interpretation in `TRACK.md`; no main change by default (§8.3) |
| D5 | Ask the instructor about C5/C8 literal grading | User, optional | Only if the user wants certainty; not a gate |

Storage, draft and query contracts (R2–R5) are reviewer/implementer responsibilities, not user decisions.

### 9.4 Risks

- **A3 touches every core write path.** A missed report keeps today's gap. **Mitigation:** V-R7, plus a checklist of every `catch (StorageWriteException)` and every new mutation entry point.
- **Draft failures now pause AI requests.** A flaky disk will pause asking more often than today. This is the requirement, but it is user-visible.
- **D2:** held work can wait indefinitely while blocked; the UI must say it is paused.
- **A4 timing:** the debounce moves from the page to the service. The existing exit/draft tests are re-targeted, not deleted.
- **A6 is about ten interfaces.** Risk of churn; strictly limited to §8.2.

### 9.5 Deferrals

- F3 (triggers in §4).
- F4c and the `Entry` N+1 queries (provisional, measured triggers).
- Multimodal and tools (F5).
- Write-queue auto-reporting.
- The optional `RootTarget` and L5/L6.

### 9.6 Completion criteria (separate)

**Architecture repair is complete when:**
- A1–A5 have merged;
- every V criterion above for A3–A5 passes, and fails when its fix is reverted (implementer's workspace);
- H1/H2/H3 and L1's termination outcome are recorded as confirmed or refuted;
- the full suite passes on both CI platforms;
- `TRACK.md` records F1–F5, L1–L7 and their outcomes;
- Codex has reviewed the implemented slices.

**Course architecture compliance is complete when:**
- A6 has merged with its bounded test;
- each C1–C8 row has been rechecked against the merged code and has report-ready text;
- residual risks (C5/C8 literal grading) are written into the report preparation notes.

Skipping A6 leaves C3 **partial**, so course compliance cannot be called complete. Neither set of criteria establishes overall course acceptance; counts, packaging, the PDF report, platform acceptance and grading stay separate.

---

## 10. Proposed changes to shared documents (user decision; none adopted)

1. `docs/architecture.md` §2: add a storage-health state row, and make the §406 paused-operation list explicit (D1).
2. `docs/requirements.md` §9.3: state that accepted, never-sent queued AI work waits during a storage pause (D2).
3. `docs/architecture.md:36`: **no change by default**. Optionally clarify that VM-facing use-case ports used for test substitution count as a replacement boundary (§8.3).
