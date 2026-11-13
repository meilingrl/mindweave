# Summary lifecycle (draft, candidate, adopt, versions, read state, export) and the summary network persistence — grade B

The summary core is carefully built: every version operation re-checks its preconditions inside one transaction, a generated version and its task completion commit together, and the draft owner (generation + revision sessions, per-node gate, obligation tracking) survives the debounce / page-switch / adopt / discard / exit-flush interleavings I traced by reading. Message seq numbers are never reused (messages are only deleted with their conversation), so ToSeq coverage and the 'later content' marker stay correct. What keeps it from an A: one deterministic, plain-input crash (saving an edit with an empty title or body hits a CHECK constraint that nothing catches), read state that leaks from one version to the next on adopt / set-current, an edit-session invalidation gap that ends in an unsaveable draft and a blocked exit, and a tail of smaller export, naming and network-placement defects. Nothing was executed; all findings come from code reading.

**Strengths**

- Store operations are single transactions that re-check state and refuse without partial effect: SqliteSummaryStore.AdoptAsync/SetCurrentAsync/DeleteVersionsAsync/SaveEditAsync (lines 107-256) check pending/current/draft/task inside the write; SaveGeneratedAsync (67-105) flips the task to 'completed' and inserts the version in the same transaction (MW-C06), so a stopped task can never leave a version.
- Draft ownership is in the application layer with a real concurrency design: SummaryDrafts binds each edit session to a generation and a revision, so a stale page can neither overwrite, commit nor discard newer input (Set 116-148, CommitAsync 179-222, DiscardAsync 229-254); a write that lands after a commit or discard is a no-op (WriteAsync 338-356); FlushAsync covers drafts still inside the 1 s delay and is wired into ExitCoordinator.SaveEverythingAsync (line 332).
- Coverage is stable: message seq is max+1 per conversation (SqliteConversationStore.cs:288) and messages are deleted only with their whole conversation (SqliteArchiveStore.cs:85, SqliteWorkspaceStore.cs:120), so ToSeq, HasLaterContent and the export's 'covered up to message N' cannot drift.
- Schema backs the main invariants: UNIQUE(conversation_id) on summary_node, UNIQUE(node_id, seq) on versions, CHECK(pending <> current), deferred node<->version keys, RESTRICT from draft to its baseline version, CASCADE for read state and node_layout (SchemaMigrations.cs:296-363).
- Export re-reads the version by ID at write time and refuses candidates and deleted versions (SummaryExportService.cs:101-115); the atomic path writes a same-folder temp file with FileMode.CreateNew, flushes to disk, renames, and removes the temp on every failure (AtomicFileWriter.cs:46-70). ExportFileName handles forbidden characters, device names with extensions, trailing dots/spaces, and a UTF-8 byte cap on grapheme boundaries.
- Network is a pure projection with no second graph store (NetworkService.ProjectAsync), layout saves filter on node existence inside SQL (SqliteNetworkStore.cs:28-32, 44-48), and the layout algorithm is deterministic (estimated label widths, no measured fonts).
- Substantial dedicated tests exist: SummaryFlowTests, SummaryDraftTests, DraftLossTests, ExitSavingTests, AtomicFileWriterTests, SummaryExportFlowTests, LayoutDiskFullTests, NetworkLayoutTests (about 2,500 lines for this subsystem alone).

**Not examined**

- Nothing was built or run; every finding is from reading code. The crash in finding 1 rests on the constraint-to-exception path as written (StorageFailures.Classify returns null for SQLITE_CONSTRAINT) and on the uncaught-command behaviour other auditors already reported.
- NetworkCanvas rendering and pointer handling, and most of NetworkHomeViewModel beyond its load and reload wiring.
- GenerationCoordinator, the scheduler and RequestRunner internals; I only read TaskRunners.RunSummaryAsync.
- ContextPlanner / ContextCompressor behaviour for summary requests (compression of long conversations before summarizing).
- Windows-specific behaviour of File.Move over locked, read-only or ACL-protected targets, and Avalonia's save picker on either platform (whether TryGetLocalPath can ever return null there).
- The summary test files themselves beyond names and a few greps; I did not assess their assertion quality.
- Statistics counting of summaries and read versions, and the workspace-name suggestion feature that reads summary titles.
- In-app restore interaction with a dirty network layout or an open summary edit, beyond confirming the container (and so NetworkSessions and SummaryDrafts) is rebuilt per session.

### gap-summary-and-network-subsystem-1. Saving a summary edit with an empty title or body hits a CHECK constraint that nothing catches; the app terminates and the empty draft survives the restart

- Final: **high** (confirmed); auditor said high
- Where: `src/Mindweave.Application/Summaries/SummaryService.cs:295`; effort S

**Evidence.** SummaryService.SaveEditAsync passes title.Trim() and body.Trim() straight to the store with no validation (line 295). SqliteSummaryStore.SaveEditAsync (lines 231-256) inserts the version without checking either. The schema requires both: `title TEXT NOT NULL CHECK (length(title) > 0)`, `body TEXT NOT NULL CHECK (length(body) > 0)` (SchemaMigrations.cs:317-318). StorageFailures.Classify (StorageFailures.cs:12-20) maps only codes 3,5,6,8,10,11,13,14,15,26; its own comment says a constraint violation 'stays SqliteException'. SummaryService.CommitAsync catches only StorageWriteException (333-345). SummaryViewModel.RunAsync catches only SummaryRefusedException and StorageWriteException (482-500), and SaveEditCommand has no CanExecute. The draft table has no such CHECK (SchemaMigrations.cs:340-346), so the autosaved empty draft is stored. No test sets an empty EditTitle or EditBody.

**Failure scenario.** User opens a summary, clicks Edit, selects all in the body (or clears the title) and clicks 'Save as new version'. The INSERT fails with SQLITE_CONSTRAINT, the SqliteException leaves the async command unhandled, and the app goes down (the uncaught-command crash already reported elsewhere). The transaction rolled back, so the stored draft still has the empty body: after restart, Edit reloads it and Save crashes again until the user thinks to type something or discard.

**Recommendation.** Validate in SummaryService.SaveEditAsync (and in the store as the last line) and refuse with a dedicated SummaryRefusal (e.g. Empty) that the page shows as a notice; disable the Save button while title or body is blank. Add a catch for non-storage exceptions in SummaryViewModel.RunAsync as the export command already has. Add tests for blank and whitespace-only title and body.

**Skeptic: confirmed, high.** I could not refute it; every link in the chain exists as claimed. This is confirmed by reading the code only: I did not run it, because building in the worktree was forbidden.

1. **No validation in the service.** `SummaryService.SaveEditAsync` passes `title.Trim()` and `body.Trim()` to the store with no emptiness check, so whitespace-only input also becomes an empty string.
2. **No validation in the store.** `SqliteSummaryStore.SaveEditAsync` checks only that the baseline is still current and that the text differs from the current version, then calls `InsertVersion`.
3. **The schema rejects it.** `summary_version` has `CHECK (length(title) > 0)` and `CHECK (length(body) > 0)`, so the insert fails with SQLITE_CONSTRAINT (code 19).
4. **The exception is not translated.** `StorageFailures.Classify` returns null for code 19, and `ForCaller` then returns the raw `SqliteException`; the doc comment says this is deliberate for constraint violations.
5. **Nothing catches it.** `SummaryService.CommitAsync` catches only `StorageWriteException`. `SummaryViewModel.RunAsync` catches only `SummaryRefusedException` and `StorageWriteException`. By contrast, the export command in the same file has a general `catch (Exception ex) when (ex is not OperationCanceledException)`.
6. **The UI does not block it.** `SaveEditCommand` is a plain `[RelayCommand]` with no `CanExecute`, and the Save button binds to it unconditionally. The only blank guard in the view model is on rename.
7. **The app terminates.** CommunityToolkit.Mvvm 8.4.2's `AsyncRelayCommand.Execute` rethrows a faulted task on the UI … (cut; full text in the workflow journal)

*Corrections to the finding.* - The CHECK constraints are at `SchemaMigrations.cs:318-319`, not 317-318.
- "The app goes down" is inferred from CommunityToolkit's `AsyncRelayCommand` rethrow behaviour and the absence of any dispatcher-level handler; it was not observed in a run.
- The empty draft surviving the restart depends on the autosave delay elapsing or the exit flush in `AppHost.Run` succeeding. Both are likely, but the finding states it as certain.
- The repeat crash after restart needs the user to press Save again on the reloaded empty draft. The draft is visible and can be edited or discarded, so it is not an unavoidable crash loop.
- No committed data is lost: the previous version stays current.

**Reproduction attempt: confirmed, high.** Reproduced with two tests against the committed tip of track/claude-auto (1ba95c9), in my own worktree.

1. Service level: for an empty body, a whitespace-only body, an empty title and a whitespace-only title, SummaryService.SaveEditAsync throws a raw Microsoft.Data.Sqlite.SqliteException (SQLite Error 19, CHECK constraint failed) instead of a typed refusal. The transaction rolls back, so the current version is unchanged, and the blank draft that was autosaved beforehand is still stored afterwards. Storage health is not blocked.

2. View-model level: with EditBody set to "", SaveEditCommand.CanExecute returns True, and SaveEditCommand.ExecuteAsync rethrows the same SqliteException out of SummaryViewModel.RunAsync. Notice stays empty and IsEditing stays True, so the user gets no message.

Process termination itself was not reproduced; the headless test awaits ExecuteAsync directly. It follows from code reading: the Save button binds to a CommunityToolkit.Mvvm 8.4.2 AsyncRelayCommand with no FlowExceptionsToTaskScheduler option, so a button click rethrows the exception on the UI thread. The only handlers in src are AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException in Program.cs, and both only log. There is no Dispatcher.UIThread.UnhandledException handler, so the exception leaves the Avalonia main loop and the process exits.

The crash-again-after-restart loop also follows from code reading: BeginEdit reloads the stored draft, which the test shows still holds the blank text. The user escapes by typing something or discarding.

High stands: a plausible, or … (cut; full text in the workflow journal)

*Corrections to the finding.* - The file paths in the finding are wrong. The store and migrations are under src/Mindweave.Infrastructure/Sqlite/ (SqliteSummaryStore.cs, StorageFailures.cs, Migrations/SchemaMigrations.cs), not Storage/. The CHECK lines are 318-319, not 317-318.
- "The app terminates" was not directly observed. It is inferred from the AsyncRelayCommand rethrow on the UI thread and the absence of any dispatcher-level handler. The exception escaping the command is observed.
- The blank draft only survives a restart if the autosave or exit flush wrote it before the crash. The debounced autosave makes that likely but timing-dependent. My test forced the flush before saving.
- Whitespace-only input also fails, because the service trims to an empty string before the insert. For a title, the stored draft keeps the untrimmed whitespace.

<details><summary>Reproduction</summary>

Both test files were temporary. They were deleted afterwards and the worktree was restored to its original branch with a clean status; nothing was committed.

=== tests/Mindweave.Infrastructure.Tests/Chat/ReproEmptyEditTests.cs ===
using Mindweave.Application;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
namespace Mindweave.Infrastructure.Tests.Chat;
public class ReproEmptyEditTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("标题", "")]
    [InlineData("标题", "   \n ")]
    [InlineData("", "正文")]
    [InlineData("  ", "正文")]
    public async Task Repro_blank_edit_escapes_as_raw_exception_and_draft_survives(string title, string body)
    {
        await using var h = await ChatHarness.StartAsync();
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.True((await h.SummaryService.SummarizeAsync(sent.Conversation, Ct)).Accepted);
        SummaryEntry? entry = null;
        await ChatHarness.UntilAsync(async () => (entry = await h.SummaryService.ForConversationAsync(sent.Conversation, Ct)) is not null);
        var node = entry!.Node.Id;
        var current = entry.Node.Current;

        var session = await h.SummaryService.OpenDraftAsync(node, current, Ct);
        var revision = h.SummaryService.SetDraft(node, session.Generation, current, title, bod … (cut; full text in the workflow journal)

</details>

### gap-summary-and-network-subsystem-2. Adopting a candidate or setting a history version current without viewing it first marks it read within about 300 ms, using the previous version's reading time and progress

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/Pages/SummaryViewModel.cs:570`; effort S

**Evidence.** Show() resets `_shownSince`, `_reportedProgress`, `_readCheckScheduled` and `ShownLocator` only `if (_shownId != id)` (570-578). AdoptAsync, SetCurrentAsync and DeleteSelected assign `_shownId` to the new version before the reload (lines 251, 282, 311), so when LoadAsync calls Show(new id) the guard is false and nothing is reset. The view then renders the new version and reports after 300 ms (SummaryView.axaml.cs:28, 90, 103-109). ReportProgressAsync takes `Math.Max(_reportedProgress, progress)` (175) and the elapsed time since the old `_shownSince` (182), and RecordProgressAsync marks read when progress >= 0.9 and visible >= 8 s (SummaryService.cs:316). The Adopt button is available while the current version is on screen (SummaryView.axaml:235-240), and 'Set current' sits on history rows that are not shown (294-295).

**Failure scenario.** User reads version 1 to the end (progress 1.0, more than 8 s). A regenerated candidate arrives; without clicking 'View candidate' the user clicks Adopt (or clicks 'Set current' on an unviewed history row). The new current version is stored as read with max_progress 1.0 almost immediately, although none of it was read. Lists and the network never show it as unread, contrary to U2 / MW-C30 ('opening alone never marks it'). The scroll position restored is also the old version's locator.

**Recommendation.** Do not pre-assign `_shownId`; pass the wanted version to the reload (e.g. a separate `_showNext` field) so Show() sees a change and resets the per-version reading state. Add a test: read v1, adopt unseen candidate, assert the new current version is unread.

**Skeptic: confirmed, medium.** I could not refute the finding; the code path exists exactly as described and nothing guards it. Verified by reading only, not by running a test.

Trace for Adopt while the current version v1 is on screen:
1. AdoptAsync runs the service call and then sets `_shownId = candidate` inside the RunAsync operation. RunAsync then awaits `_load.Request()`.
2. LoadAsync ends with `Show(_shownId ...)`, so it calls Show(candidate). The guard `if (_shownId != id)` is false, so `_shownSince`, `_reportedProgress`, `_readCheckScheduled` and `ShownLocator` all keep v1's values.
3. Show still sets `ShownIsRead = detail.ReadState(id).IsRead` (false for an unread candidate) and `Shown = version`. The view's PropertyChanged handler calls Render(), which starts the 300 ms report timer.
4. Report() calls ReportProgressAsync(Progress, ...). `_reportedProgress = Math.Max(old, progress)` stays at v1's value. With ShownIsRead false and progress >= 0.9, it computes `visible` from the stale `_shownSince` and calls RecordProgressAsync(Node, candidate.Id, _reportedProgress, locator, visible).
5. SummaryService.RecordProgressAsync has no per-version guard: `markRead = progress >= 0.9 && visible >= 8 s`. It trusts the view model's numbers, so the new current version is stored as read with v1's progress.

SetCurrentAsync has the identical pre-assignment, and its button sits on history rows gated by CanSetCurrent, not by being shown. DeleteSelected does the same when the shown version is among those deleted.

This contradicts the requirements directly: docs/requirements.md line 183 says viewing an old versio … (cut; full text in the workflow journal)

*Corrections to the finding.* The finding is accurate; three refinements.

1. The precondition is about the current page opening, not v1's stored read state: `_reportedProgress` must have reached 0.9 and the page must have been showing v1 for at least 8 s in this view-model instance. For a body that fits on one screen this is met just by having the page open 8 s.
2. In the DeleteSelected variant, only the case where the shown version is among the deleted ones is affected (the `selected.Contains(shown)` branch).
3. SaveEditAsync (line 420) has the same pre-assignment, so a newly saved edit also inherits the previous version's time and progress. That is arguably harmless since the user wrote it, but a fix should cover all four call sites; resetting the per-version state on any change of shown version would do it.

"About 300 ms" is correct for the timer, plus the reload time.

### gap-summary-and-network-subsystem-3. Version operations do not invalidate an open edit session; the next keystroke becomes an unsaveable 'conflict' draft that blocks exit with a generic message

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Application/Summaries/SummaryDrafts.cs:261`; effort M

**Evidence.** RunVersionOperationAsync (261-291) refuses only when the record is Dirty/Failed/Conflict and never bumps `record.Generation`; the class comment says the generation is 'bumped by every commit and discard' only. An edit session opened with no input yet (status None) therefore stays valid after adopt / set current. Set() accepts its next input because the generation matches (125-135); WriteAsync then gets SummaryRefusal.Changed from SqliteSummaryStore.SaveDraftAsync (line 215: `Current != draft.Baseline`) and sets Conflict (372-386). A Conflict can never be written (WriteAsync returns false at 354) and FlushAsync returns false while any record is Conflict (170), which ExitCoordinator.SaveEverythingAsync (332-336) reports as the generic ExitSaveFailedBody ('草稿或回答的结束状态没能写入…写入成功前不会退出', UiText.cs:96). The same end state is reached when a node is deleted while its draft is Dirty: SaveDraftAsync's RequireNode throws Changed and nothing ever clears the record (SummaryDrafts is not told about cascade deletes; only SummaryService references it).

**Failure scenario.** User clicks Edit on a summary (no typing yet), navigates away (the page stays in the back stack, IsEditing true), opens the same summary again from the network and adopts the candidate or sets another version current. Going Back, the editor still shows the old version's text under 'editing version N'. Typing is accepted and the page says the draft is autosaved, but the write is refused in the background. 'Save as new version' is refused with 'the summary has changed'; the text can only be copied out and discarded. If the user leaves instead, quitting shows 'part of the content could not be saved' and Retry fails forever, with nothing naming the summary. In the deleted-node variant the page is closed by ConversationRemoval, so there is no place left to discard and the app can only be force-quit.

**Recommendation.** Bump the node's draft generation in RunVersionOperationAsync after a successful operation that changes current, so open sessions get DraftStale on their next input. Give SummaryDrafts a 'node removed' entry point called from conversation and workspace deletion that drops the record. At exit, name the summaries holding conflicted drafts and offer 'discard and quit'.

**Skeptic: partially confirmed, low.** The mechanism is real, but the trigger is narrower than the finding describes, so medium is overstated. This is from static reading only; nothing was run (builds and tests are prohibited in this audit).

**What holds up**
- `RunVersionOperationAsync` refuses only Dirty/Failed/Conflict and never bumps `Generation`; only `CommitAsync` and `DiscardAsync` do.
- A session opened with no input (status None) therefore survives an adopt or set-current. Its next `Set()` passes the generation check and goes Dirty.
- The background write is then refused by `SaveDraftAsync` (baseline no longer current) and the record becomes Conflict. `Set()` already returned a revision, so the page shows no notice and keeps the static "draft autosaved" note.
- `LoadAsync` never touches `IsEditing`, `EditingHeading`, `EditTitle` or `EditBody`, so the stale editor persists after the reload.
- Save-as-version is refused with Changed; Discard works because generation and revision still match.
- `FlushAsync` returns false while any record is Conflict, and the exit dialog shows the generic body with only Return and Retry. Nothing names the summary.
- Only `SummaryService` references `SummaryDrafts`; conversation and workspace deletion clear the database row but not the in-memory record.
- No guard, test or TRACK.md entry covers a zero-input session surviving a version operation. The existing test `Unsaved_input_in_memory_keeps_the_current_version_from_being_switched` covers only the Dirty case.

**Why the severity drops**
1. **The stated path is not reachable as written.** Network home is stack index 0, rea … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. "Opens the same summary again from the network" is not a working path. Network home is reachable only via Back, which disposes the editing page, and same-route navigation onto the stack top is deduplicated. A second page for the node requires a different summary page pushed in between, via conversation summary panels in Explore.
2. Only a session with zero input is exposed. After any stored keystroke, `ThrowIfDraft` refuses adopt and set-current.
3. "Retry fails forever" holds only until the user presses Return, opens that summary's Edit and discards. The real gap is that nothing says which summary.
4. The deleted-node variant is not a simple "delete while Dirty". It needs the delete to land inside the 1-second autosave window, or a pre-existing Conflict record. Clean records are harmless. When it occurs, the force-quit dead end is accurate and also blocks in-app restore.
5. The exit … (cut; full text in the workflow journal)

### gap-summary-and-network-subsystem-4. Ticks in the version history are wiped by any reload, including the page's own 'marked read' event about 8 seconds after opening

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/ViewModels/Pages/SummaryViewModel.cs:533`; effort S

**Evidence.** LoadAsync does `History.Clear()` and rebuilds every SummaryVersionItem with IsSelected false (533-548). Reloads are triggered by every ISummaries.Changed for the conversation (502-508): a read mark from RecordProgressAsync (SummaryService.cs:318-321), a naming result, a summary task finishing, a rename. The delete confirmation built in DeleteSelected reads the ticks at click time (293).

**Failure scenario.** User opens a short unread summary, expands the history and ticks two old versions. At about 8 s the version on screen is marked read, Changed fires, the list is rebuilt, the ticks disappear and 'Delete selected' disables itself. The same happens if a same-name naming task returns while the user is selecting.

**Recommendation.** Carry the selected IDs across the rebuild (restore IsSelected for IDs that still exist and are selectable), or update rows in place instead of clearing.

### gap-summary-and-network-subsystem-5. Exported Markdown does not escape the title or the source-conversation name, so titles with <, *, _ or backticks render differently from what the app shows

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Export/SummaryMarkdown.cs:32`; effort S

**Evidence.** Format writes `"# " + OneLine(document.Title)` (32) and the source line with `OneLine(source.ConversationName)` (54); OneLine only folds line breaks (61). The app itself shows titles as plain text. SummaryMarkdownTests has no case with Markdown-significant characters in a title.

**Failure scenario.** A summary titled 'List<T> 与数组的区别' exports as `# List<T> 与数组的区别`; any HTML-producing Markdown renderer treats <T> as a tag and shows 'List 与数组的区别'. A title '__init__ 的作用' renders as bold 'init'. The source note, built from the user's first question, has the same problem (e.g. a question containing `*` or a leading `>`).

**Recommendation.** Backslash-escape Markdown punctuation and '<' / '&' in the heading and in the conversation name of the source note; leave the body verbatim as designed. Add test cases for generics, underscores and backticks.

### gap-summary-and-network-subsystem-6. A summary operation that fails in its second step reports 'nothing was stored' although the version change committed, and skips the change notification

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Summaries/SummaryService.cs:427`; effort S

**Evidence.** AfterCurrentChangeAsync runs the store operation and then `_naming.CheckNameAsync` inside the same delegate that NotifyAsync wraps (413-435); CheckNameAsync does its own read and UpdateBaseTitleAsync write (SummaryNaming.cs:79-91). SaveEditAsync does the same after `_drafts.CommitAsync` (293-304). CommitAsync's contract comment says 'a StorageWriteException reaches the caller and nothing was stored' (329-332), and the page shows UiText.StoragePaused with the comment 'Nothing was stored' (SummaryViewModel.cs:493-497). `Changed` is raised only after the delegate returns (417-420, 299-302).

**Failure scenario.** The disk fills or the database is briefly locked between the adopt (or save-edit) transaction and the base-title update. The adopt is durable, but the user is told storage is paused and nothing was stored; `_shownId` is not moved; the conversation panel, lists and the network get no Changed event and keep showing the old state until something else reloads them. After a save-edit, the editor stays open on a session whose generation was already bumped, so the next keystroke shows 'saved or discarded elsewhere'.

**Recommendation.** Treat the name check as best effort after the commit: catch and log its failure (report to StorageHealth), always raise Changed once the first step committed, and return success for the user's operation.

### gap-summary-and-network-subsystem-7. Network first load writes one transaction per node before showing anything, and places computed against an unsaved layout stay saved after 'Don't save'

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Network/NetworkSession.cs:93`; effort M

**Evidence.** LoadAsync awaits `_network.PlaceAsync` once per newly placed node inside the loop (93-108) and assigns Projection / _positions only afterwards (110-113). When nothing is placed yet, PlaceNew returns a full Arrange (NetworkLayout.cs:148-151), so every node goes through its own write. Separately, while the layout is dirty the reference for new places is the in-memory layout (line 81), yet each new place is still saved immediately with ON CONFLICT DO NOTHING (SqliteNetworkStore.cs:28-32); DiscardChanges (182-188) then reloads the saved positions of the old nodes.

**Failure scenario.** (1) A workspace collects 150 summaries before the network page is first opened (or after a restore of a backup made before the page was ever opened): the page stays empty for 150 sequential write transactions; TRACK already records slow disk sync on the Windows test machine as the main cause of slowness. (2) User clicks Arrange (unsaved), a new summary appears and is placed beside its parent's arranged position and saved; the user leaves with 'Don't save'. Old nodes return to their saved places, the new node keeps a place computed for a layout that no longer exists and can sit on top of another node or far from its source; it is never re-placed because it now has a row.

**Recommendation.** Save all automatic places of one load in a single transaction (a PlaceManyAsync with the same DO NOTHING rule) and publish the projection before or independent of that write. While dirty, keep new places in memory only (or compute and save them against the saved layout) so 'Don't save' leaves a consistent saved state.

### gap-summary-and-network-subsystem-8. Export writer: contract and comments do not match behaviour on the direct path, permission errors are always retried as 'in use', and the reserved-name list is incomplete

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Export/AtomicFileWriter.cs:100`; effort S

**Evidence.** (a) ExportWriteException is documented as 'the target is as it was before and no temporary file is left' (IExportFileWriter.cs:31) and ExportAsync repeats it (SummaryExportService.cs:82), but WriteDirectAsync truncates with SetLength(0) and then writes (107-113); for a non-seekable stream it does not truncate at all. Reached only when the picker returns no local path (ISystemShell.cs:77-80). (b) IsTransient treats every UnauthorizedAccessException as transient (122), so a read-only target waits the full 1.5 s RetryWindow, and Classify reports any access denial on an existing non-read-only target as InUse (134), including an ACL denial. (c) The rename replaces the directory entry: a symlink target is replaced rather than written through, and the new file gets default permissions; the containing directory is not synced after the rename. (d) SummaryExportService.cs:117-121 still says 'a conversation with a summary cannot be deleted', false since the v0.12 cascade delete (behaviour is still right because the summary goes with it). (e) ExportFileName.ReservedNames (SummaryMarkdown.cs:74-79) lacks COM0, LPT0, the superscript forms COM¹-³ / LPT¹-³ and CONIN$ / CONOUT$, which current Windows documentation lists as reserved.

**Failure scenario.** A user on Windows exports over a file they have no delete permission on: after 1.5 s the app says another program is using the file, which sends them looking for the wrong cause. On a platform or location where the picker gives only a stream, a failure midway leaves the user's previous file truncated while the message and the logged contract say it is untouched. A summary titled 'COM0' suggests a file name Windows may refuse.

**Recommendation.** Narrow the ExportWriteException contract to the atomic path and surface ExportWriteMode.Direct failures as 'the file may be incomplete'. Retry UnauthorizedAccessException only on Windows and only when the target exists and is not read-only; classify the rest as NotWritable at once. Fix the stale comment, extend the reserved list, and decide deliberately whether symlinked targets should be resolved first.

### gap-summary-and-network-subsystem-9. Same-name disambiguation is never re-evaluated: late members stay unnamed, identical terms are accepted, and a term outlives its group

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Summaries/SummaryNaming.cs:151`; effort M

**Evidence.** ScheduleNamingAsync returns silently when the group key is already queued (`!_namingQueued.TryAdd(...)`, 151); nothing re-runs it when that task finishes (OnTaskFinished 67-73 only removes the marks). The apply step builds names from whatever terms came back (176) and ApplyNamesAsync only checks each node's own expectation (SqliteSummaryStore.cs:310-313), not that the resulting names differ. Naming is triggered only from SaveAsync for a new node and from CheckNameAsync when a node's own title changes (79-91); deleting a node or its conversation does not revisit the remaining members.

**Failure scenario.** Two summaries titled '蒸汽机' are being named when a third with the same title is saved: the third is skipped and stays plain '蒸汽机' beside '蒸汽机－原理' and '蒸汽机－历史' until some later change happens to trigger the group again. If the model returns the same term for two members, both become '蒸汽机－原理'. If one of two members is deleted, the survivor keeps its now pointless suffix indefinitely.

**Recommendation.** When a naming task finishes, re-check the group if its membership differs from what the task saw. Reject or blank duplicate terms within a result. On node deletion, reset a single remaining 'auto' member to its base title.

### gap-summary-and-network-subsystem-10. Generated titles and short names are cut by UTF-16 index and can end on half a surrogate pair

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Core/Summaries/SummaryRules.cs:51`; effort S

**Evidence.** `title[..MaxTitleLength]` (line 51) and `s[..MaxShortTitleLength]` (line 41) cut at a char index. The same class's ShortTitleOf (89-90) cuts by runes, and TRACK's 2026-11-01 finding records fixing exactly this class of bug for naming excerpts ('原来按 UTF-16 截断可能切开代理对').

**Failure scenario.** A model returns a title longer than 40 UTF-16 units with an emoji or a supplementary-plane CJK character straddling position 40 (or position 8 of the short name). The stored title ends in a lone surrogate, which SQLite stores as U+FFFD: the summary title, base name, network label and suggested export file name all end in a replacement character.

**Recommendation.** Cut by runes or text elements in ParseGenerated, as ShortTitleOf and ExportFileName.Cut already do; add a test with a surrogate pair at each boundary.

