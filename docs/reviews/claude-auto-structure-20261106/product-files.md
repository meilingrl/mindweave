## Large-file analysis: the other big product files (track/claude-auto @ 8769333)

The repo has 183 commits. Commit counts come from `git log --name-only`. I judged each file by what it does and how often it changes, not by length.

### Summary ranking

| # | File | Lines | Commits | Verdict | Priority | Effort |
|---|---|---|---|---|---|---|
| 1 | Desktop/ViewModels/Conversations/ConversationViewModel.cs | 782 | 19 | **Split** | high | M |
| 2 | Application/Conversations/ConversationService.cs | 701 | 19 | **Split** (titling) | high | S |
| 3 | Desktop/App.axaml | 1208 | 26 | **Split** (organized by delivery round, not by component) | high | S–M |
| 4 | Application/Summaries/SummaryService.cs | 580 | 10 | **Split** (naming) | medium | S |
| 5 | Infrastructure/Sqlite/SqliteDatabase.cs | 768 | 12 | **Split** (backup files) | medium | S–M |
| 6 | Desktop/UiText.cs | 752 | 33 | Cohesive catalog; split the file only, to cut merge conflicts | medium | S |
| 7 | Desktop/Views/Pages/NetworkCanvas.cs | 1083 | 6 | Mixed but stable; cheap pure extractions | low–medium | S (M for the full split) |
| 8 | Desktop/ViewModels/Pages/SummaryViewModel.cs | 642 | 12 | Optional child view model for editing | low–medium | M |
| 9 | Desktop/ViewModels/Conversations/ConversationListViewModel.cs | 512 | 10 | Move one type out; selection is borderline | low | S |
| 10 | Infrastructure/Sqlite/SqliteSummaryStore.cs | 527 | 4 | **Acceptable** | – | – |
| 11 | Application/Summaries/SummaryDrafts.cs | 502 | 1 | **Acceptable**, keep together | – | – |

---

### 1. ConversationViewModel.cs: split (high, M)

**What it does now:**
- **Page state and properties** (29–240): about 25 observable properties covering the title, lineage, context usage, summary, archive, confirmation, rename and branches.
- **Reading position and branch creation** (242–263).
- **Storage pause handling** (271–304): `OnIsStorageBlockedChanged` clears notices that belong to the branch and summary parts.
- **Archive, restore, delete and the confirm flow** (347–507, about 160 lines): `ArchiveAsync`, `RestoreThenAsync`, `DeleteAsync`, `DeletePlainAsync`, `BusyNoticeAsync`, `Confirm`/`CancelConfirmation`, `TitlesAsync`.
- **Summary panel** (508–537 and 597–630): `SummarizeAsync`, `StopSummaryAsync`, `OpenSummary`, `LoadSummaryAsync`, plus 7 properties.
- **Message list reconciliation** (632–725): `LoadAsync` does an in-place diff of `Messages`, computes branch entries and lineage, and sets `_related`.
- **Running answer and timer** (727–782): `UpdateRunning`, `UpdateTicking`, `RefreshElapsed`.

**Evidence:**
- The constructor takes 10 dependencies, 4 of them optional (`ISummaries?`, `IConversationDeletion?`, `IConversationDeletionPrompt?`, `TimeProvider?`).
- Recent commits each touch a different part:
  - 1816650 and 08ba1a2 (delete)
  - 7e488d0 (timer)
  - 0aab3ee (marks)
  - 67a3b44 (workspace delete)
- There is duplicated logic:
  - `TitlesAsync` (502) re-implements `ConversationArchiving.TitlesAsync` in ConversationDeletionViewModel.cs:58.
  - `ArchiveAsync` does not reuse `ConversationArchiving.ArchiveAllAsync`.
  - The `PendingConfirmation` pattern is repeated in SummaryViewModel.

**State the parts share:**
- `IsArchived` is read by the load, `UpdateRunning`, archive and composer code.
- `_workspace` is set by `LoadAsync` and used by delete and titles.
- `_load` is requested after archive, restore and delete.
- `_disposed` is checked everywhere.
- `ArchiveNotice` and `Confirmation` belong to archive/delete only.

**Risk:**
- Every feature round edits this one file, so review diffs mix concerns.
- The archive/delete paths can only be tested by building the full page (all 10 dependencies).

**Proposed split:**
1. **`ConversationLifecycleActions`** (Desktop/ViewModels/Conversations), an `ObservableObject` child exposed as `Lifecycle`.
   - Moves: `ArchiveNotice`, `Confirmation`, the archive/restore/delete/confirm commands, `BusyNoticeAsync`, `RestoreThenAsync`.
   - Its constructor takes `IConversations`, deletion, prompt, navigator, a `Func<WorkspaceId>`, a `Func<Task> reload` and a `Func<Task> summarize` (for restore-and-summarize).
   - `TitlesAsync` is replaced by `ConversationArchiving.TitlesAsync`.
2. **`ConversationSummaryPanel`** child (`Summary`).
   - Moves: `SummaryNode`, `SummaryEntryText`, `SummaryMarkersText`, `RunningSummary`, `SummaryProblem`, `SummaryNotice`, `HasSummary`, `SummarizeLabel`, the Summarize/Stop/Open commands and `LoadSummaryAsync`.
   - Exposes `OnStorageResumed()`. The page's `OnIsStorageBlockedChanged` calls it.
3. **`MessageListReconciler`**: a pure static `Reconcile(ObservableCollection<MessageItemViewModel>, ConversationContent, branches, archived, superseded, Func<Message, MessageItemViewModel> create)` taken from 666–712. It can then be unit-tested without a page.
4. Keep these in the page: identity, lineage, title/rename, composer, the running answer and timer, storage pause, and the `_disposed`/`_load` ownership.

**Migration order:**
1. Reconciler (no binding changes).
2. Remove the duplicated `TitlesAsync`.
3. Summary panel. Update the `ConversationView.axaml` bindings to `Summary.*`, or keep forwarding properties for one step.
4. Lifecycle actions.

**Tests that protect it:**
- ConversationUiTests, ConversationPolishTests, ArchiveUiTests, StoragePauseUiTests, ContextUiTests, BranchUiTests
- ConversationActionsTests.LateReload
- SummaryUiTests (summarize from the page)
- WorkspaceDeleteUiTests and MultiSelectUiTests (delete prompt)
- GalleryTests (renders bindings)

---

### 2. ConversationService.cs: extract titling (high, S)

**What it does now:**
- **Reads** (151–205).
- **Send, ask and retry** with checks, planning and enqueueing (207–352, 534–616).
- **Branch** (354–394).
- **Archive, restore and visibility events** (396–497).
- **Rename** (499).
- **Automatic titles** (78–86 constants, 97 `_titleRequested`, constructor event wiring 119–123, `OnAnswerFinished`/`ScheduleTitleAsync` 618–691, about 100 lines including the Chinese prompt).

**Evidence:** `_titleRequested` is changed from four places:
- `ArchiveAsync` (428)
- `NotifyDeleted` (461)
- the `TitleAbandoned` handler (122)
- `ScheduleTitleAsync` (4 removal paths)

That ad-hoc ownership is where bugs hide: 1d1e4f0 "no late title slips in".

**Proposal:**
- New `internal sealed class ConversationTitler` in Application/Conversations. It takes the store, branches, the generation coordinator, health, models, clock and a logger.
- It owns `_titleRequested`, the prompt constants, `OnAnswerFinished`'s title part, `ScheduleTitleAsync`, and `Forget(IEnumerable<ConversationId>)`. It subscribes to `TitleAbandoned` itself.
- `ConversationService` keeps raising `ConversationChanged` on `AnswerFinished` and `TitleSaved`, and calls `_titler.Forget(...)` from archive and delete.
- Optional later step: move archive/restore/`NotifyDeleted`/`NotifyVisibility` (about 100 lines) into `ConversationVisibility`. It would raise the service's events through a callback. Lower value, because `IConversations` exposes those events.

**Tests that protect it:** ConversationFlowTests and ArchiveFlowTests (titles cancelled on archive), ExitStopTests (title abandoned on exit), ConversationDeleteFlowTests, RealApiTests (title budget).

---

### 3. App.axaml: split into resource and style files (high, S–M)

**What it holds:**
- Light and Dark theme dictionaries (about 20–265).
- Icons (268–310).
- Fluent setup (300–311).
- About 174 `<Style>`s (312–1208).

**The real problem** is that it is organized by **delivery round**, not by component. The markers include "▼ S7 round 3 · settings ▲", "Round 4 · topic-delete", "Round 4 · sidebar-status" and "Multi-select". Settings styles alone sit in 3 separate blocks (139–146, 256–263, 842–992). With 26 commits it is the #2 hotspot, and every parallel worktree (AGENTS.md "多个 Agent 并行", i.e. several agents working in parallel) appends to it, so it is a merge-conflict magnet.

**Proposal:**
- `Themes/Colors.axaml`: a ResourceDictionary holding `ThemeDictionaries` Light/Dark. Keep one file so each key is defined once per theme, side by side.
- `Themes/Icons.axaml`.
- `Styles/Controls.axaml`: buttons, notices, badges, disabled states (422–603).
- `Styles/Sidebar.axaml` (369–411, 683–699, 715–760, 1043–1208 multi-select and status).
- `Styles/Conversation.axaml` (312–355 answer typography, 604–714, 993–1042).
- `Styles/Dialogs.axaml` (779–841).
- `Styles/Settings.axaml` (842–992).
- App.axaml merges them through `<ResourceInclude>` and `<StyleInclude Source="avares://Mindweave.Desktop/Styles/...">`.

**Steps:** icons first, then colors, then one style group per commit. Keep the order: selector precedence depends on declaration order across includes.

**Tests that protect it:**
- ThemeTests:107–110 asserts the light and dark key sets are equal.
- GalleryTests renders every page.
- SourceHighlightThemeTests, BranchMarksTests, SettingsPolishTests and NetworkCanvasTests resolve resources.

---

### 4. SummaryService.cs: extract naming (medium, S)

**What it does now:**
- Summarize: `SummarizeAsync` (148–230), `SaveAsync`, `BackgroundAsync`, `PlanAsync` (379–430).
- Version operations and pass-through to drafts (234–349).
- **Same-name disambiguation** ("naming"): constants 62–77, `_namingQueued` and `_namingTasks` (92–93), the constructor handler (112–120), `CheckNameAsync`, `ScheduleNaming*`, `ParseNaming` (465–575). About 160 lines with its own prompt, regex and dedup state.

This mirrors ConversationService's titling: a best-effort background generation with dedup state cleaned up from the task-finished event.

**Proposal:**
- `internal sealed class SummaryNaming` in Application/Summaries. It owns the constants, `NamingLine`, both dictionaries, `CheckNameAsync` (as `AfterCurrentChangedAsync(node)`), `ScheduleNaming*`, `ParseNaming`, and an `OnTaskFinished(task)` cleanup.
- It raises changes through a callback, `Action<ConversationId>`.
- `SummaryService.AfterCurrentChangeAsync` calls it.
- Keep `ParseNaming` `internal static` so its tests keep working.

**Tests that protect it:** SummaryFlowTests, SummaryStoreTests (`ApplyNames`), SummaryReadTests, and the `ParseNaming` unit tests.

---

### 5. SqliteDatabase.cs: extract backup file handling (medium, S–M)

**What it does now:**
- **Lifecycle:** open, create, migrate and close (64–100, 284–576). The migration path uses `CreateVerifiedBackup` and `PruneBackups`.
- **Runtime gateway:** `WriteAsync`, `ReadAsync`, `ProbeWriteAsync`/`ProbeDisk`, `DrainAsync` (175–330).
- **Backup files:** the naming scheme and regex, `ListBackups`, `Discard`, `CreateVerifiedBackup`, `PruneBackups`, `ReplaceWithBackup` with rollback, `TryDelete`, `DeleteDatabaseFiles` (103–168, 577–720).
- **19 `LoggerMessage` declarations** (725–767).

**Evidence:** backup work changed separately from the rest: 0c7d9cb, 736459f and 08ba1a2 (pre-delete backups, suffixes, keep rules). Those changes needed no lifecycle state, only `_paths`, `_time` and `_logger`.

**What must stay together:** `_lifecycleGate` and `_writer`. `RestoreAsync` (close → replace → open) and `BackupAsync` (enqueue on the writer) remain in SqliteDatabase.

**Proposal:**
- `internal sealed class SqliteBackupFiles(AppPaths, TimeProvider, ILogger)` with these members:
  - `List()`, `Discard(path)` and `Prune(keep)`
  - `CreateVerified(SqliteConnection, int version, BackupReason)`, which keeps the `BeforeBackupForTests` hook passed in
  - `ReplaceDatabaseWith(BackupInfo)`
  - the suffix/reason mapping and the regex
- SqliteDatabase delegates to it and keeps implementing `IDatabaseBackup`.
- Optionally move the probe (199–253) into `SqliteStorageProbe`. It only needs the writer and the paths.

**Tests that protect it:** SqliteDatabaseTests, Faults/BackupKillTests, RestoreFailureTests, StorageProbeTests, DiskFullTests, and the SchemaV8/V9 migration tests. TestHost/Program.cs uses the hooks.

---

### 6. UiText.cs: acceptable catalog; split the file only (medium, S)

It has 287 public members, all of them wording or wording-plus-formatting. It already has feature sections:
- 34 shell
- 84 network
- 95 appearance
- 102 settings
- 126 summaries
- 160 export
- 169 archive/delete
- 679 workspace delete
- 711 multi-select

Its single purpose is real. The problem is that it is the **#1 hotspot (33 commits)**: almost every feature commit touches it, including acb09eb, 1816650, 08ba1a2 and 929d1ff. Real logic is mixed in, such as `ContextUsage` (268–290), `ExitWithUnfinished`, `DeleteScope…` (727+) and the time formatters (546–578).

**Proposal:**
- Change it to `public static partial class UiText`, with files `UiText.Shell.cs`, `.Conversation.cs`, `.Summaries.cs`, `.Network.cs`, `.Settings.cs`, `.ArchiveDelete.cs`, `.Formatting.cs` (FormatTime, MessageTime, Elapsed, AnswerDuration, file size).
- No call-site or XAML `x:Static` changes (13 XAML files use them).
- Do not split it into separate classes; that would churn hundreds of call sites for no gain.
- Tests: full build, plus the UI tests that assert text.

---

### 7. NetworkCanvas.cs: mixed but stable (low–medium)

**What it does now:**
- Paint records and enums (15–80).
- Constants (84–145).
- Lifecycle and data-context wiring (146–200).
- Coordinates and hit-testing (203–275).
- Building the paint model: `PaintNodes`, `PaintEdges`, `PaintCaptions`, `LabelOrigin`, `Crosses` (277–443).
- Drawing: `Render`, `DrawEdge`, `DrawGrid`, `DrawNode` (444–602).
- Pointer input: pan, press, drag, wheel (603–800).
- Zoom and pointer handling plus the decorative motion (`_offsets`, `_motion`, `Attraction`, `StepMotion`) (800–898).
- Fit (903–938).
- The nested `Palette` (943–1083).

It mixes six concerns, but it has only 6 commits, and the pure parts are already `internal static` and tested directly (`NetworkCanvas.HitBox` ×5, `Palette` ×5, `Crosses`, `Attraction`, `FitTo`, `LabelSize`). The drag, hover and motion state (`_pressed`, `_dragAt`, `Dragging`, `_offsets`, `_pointer`) is shared by input, paint and motion, so it must stay in one owner.

**Proposal (cheap and safe):**
- Move `Palette` to `Views/Pages/NetworkPalette.cs`.
- Move the paint records and enums to `NetworkPaint.cs`.
- Move the pure statics (`HitBox`, `LabelOrigin`, `Crosses`, `EdgeLabelOrigin`, `FitTo`, `Attraction`, `LabelSize`) to `static class NetworkGeometry`. Leave forwarding members, or update the about 20 test references.

That brings the control to about 650 lines. Do **not** split the input, drag and motion state across classes. Tests: NetworkCanvasTests (700), NetworkDragTests, NetworkUiTests.

---

### 8. SummaryViewModel.cs (low–medium, M)

**What it does now:**
- `SummaryVersionItem` (15–60) and `PendingConfirmation` (62) are declared here.
- Version history and selection (269–365).
- Reading progress (`_shownSince`, `_reportedProgress`, `_readCheckScheduled`; 218–255).
- Export (366–411).
- Confirmation (413–424).
- The **edit session** (`_draftGeneration`, `_draftRevision`, `_draftBaseline`, `_loadingEdit`, `EditTitle`/`EditBody`, `BeginEditAsync`, `SaveEditAsync`, `DiscardEditAsync`, `RequestDraftSave`; 426–530).
- Rename.

The draft logic was already moved into the application layer (89d6fe1), so the leftover page logic is thin.

**Proposal:**
- Move `SummaryVersionItem` and `PendingConfirmation` to their own files (S). `PendingConfirmation` is shared with ConversationViewModel.
- Optionally extract `SummaryEditSession` (the draft fields plus the 4 methods; the page passes `Show`, `Notice` and `_load`).
- Tests: SummaryUiTests, SummaryDraftTests, ExportUiTests.

---

### 9. ConversationListViewModel.cs (low, S)

- `ConversationItemViewModel` (14–78) and `ConversationMark` (79–91) live in this file; move them to their own files.
- The rest covers loading the tree (`AsTree` 459), marks, navigation, and multi-select (215–346: `Toggle`, `SelectRange`, `ClearSelection`, `ArchiveSelectionAsync`, `SelectionRoots`, `Ancestors`).
- Multi-select shares `Items` and could become a `SidebarSelection` helper later. It is not needed now, since the commits touching it (4f4bb36, 1816650) are feature-local.
- Tests: MultiSelectUiTests, SidebarStatusTests, ArchiveUiTests.

---

### 10–11. Acceptable as they are
- **SqliteSummaryStore.cs:** one summary-aggregate store. It has public operations (21–356) plus private SQL helpers (358–527) shared across them (`RequireNode`, `ThrowIfDraft`, `InsertVersion`, `UpsertRead`). Splitting it would scatter the transaction helpers. 4 commits.
- **SummaryDrafts.cs:** one state machine (per-node `Record` and `SemaphoreSlim` gate, generation/revision, autosave timer, `WriteAsync`/`DeleteAsync`/`Failed`). Every method shares the gates and records; **keep it together**. 1 commit.

### 400-line band, quick scan
- **Fine:**
  - `SchemaMigrations` (389): a migration catalog.
  - `StorageHealth` (365): one recovery loop.
  - `SourceAnchors` (370): static anchor capture and restore.
  - `SqliteConversationStore` (431).
  - `SqliteArchiveStore` (358).
  - `NetworkHomeViewModel` (389): its 6 view records at 20–60 could move out (trivial).
  - `AnswerView.axaml.cs` (389): view-only selection and branch UI.
- **Minor:** `ConversationDeletionViewModel.cs` holds three types (`ConversationDeletionRequests`, the `ConversationArchiving` helper, the view model). Give `ConversationArchiving` its own file and make it the single home for `TitlesAsync`, which also removes the duplicate in item 1.

### Suggested order
Each step runs the full tests and merges on its own:
1. UiText split into partial-class files.
2. App.axaml split.
3. ConversationTitler.
4. SummaryNaming.
5. ConversationViewModel, steps 1–4.
6. SqliteBackupFiles.
7. NetworkCanvas pure extractions.
8. The single-type file moves (items 8 and 9, and the deletion file).

Steps 1–2 and 3–4 are independent and can go in parallel worktrees. Step 5 should come after 1 because it touches UiText call sites.

I did not edit, build or run any tests; everything above comes from reading the source and the git history.