# Performance and resource use — grade C

Repo root: /Users/meilingluo/Developer/Projects/mindweave.worktrees/claude-auto (all `file` values are relative to it). The storage read paths are well built and measured: one serialized writer, reads off the UI thread, throttled UI updates, and SQL-side aggregation for statistics. I timed the main queries with the sqlite3 CLI on a synthetic database (200k messages, 100k tasks, 100k requests, 577 MB) and the read side held up: each statistics query under 80 ms, quick_check 0.2 s warm, startup recovery 8 ms.

The grade is C because two problems hit at exactly the "heavy year of use" scale the project sets for itself (PerfShape.Full):

1. Deletes are quadratic. Three foreign keys that point at `message` have no index on the child column. Deleting one 100-message conversation took 2.5 s; 20 conversations took 49 s. With the three indexes the same deletes took 1.7 ms and 1.3 s for a whole 200-conversation workspace.
2. Neither the message list nor the sidebar is virtualized. Opening a conversation costs UI-thread time proportional to all the text in it. The project's own figure is about 0.6 s per 100,000 characters; the project's own seeded long conversation is 4 million characters, and its rendering was never measured.

Beyond those: several retention and reload inefficiencies (live answers never released, full first-question text in every sidebar reload, double full-body reads per page reload, a full database copy per cascading delete kept forever). The performance harness is opt-in, not in CI, has no write or delete benchmark, and has not been run on Windows, the slower target.

Limits of my evidence: this was a read-only audit with no build. Delete and query timings are measured on a synthetic database with the same key and index shape as the real schema; UI costs are extrapolated from the project's documented measurements, not measured by me.

**Strengths**

- Storage threading is sound: one dedicated writer thread with a transaction per work item (src/Mindweave.Infrastructure/Sqlite/SqliteWriteQueue.cs), and every read runs on the thread pool in one snapshot (SqliteDatabase.ReadAsync, SqliteDatabase.cs:263-289). I found no synchronous database call on the UI thread; startup (AppStartup.RunAsync) is fully async.
- UI update rhythm is deliberately decoupled from token arrival: UiThrottle (src/Mindweave.Desktop/Services/IUiDispatcher.cs:20) coalesces to 50 ms per answer, 100 ms for the sidebar and 30 ms for page reloads; SerialRefresh prevents overlapping reloads. LiveAnswer.MarkThinking raises Changed only on a phase change, so reasoning chunks cause no UI work.
- Streaming render cost was measured and documented honestly (docs/design/s2-branching.md, section 超长回答的渲染): incremental LiveMarkdown updates take 10-13 ms of UI thread per update at 100,000 characters, with budgets enforced in tests/Mindweave.Desktop.Tests/Performance/AnswerRenderPerformanceTests.cs.
- Statistics are aggregated in SQL in one snapshot and return only per-day rows (SqliteStatisticsStore.cs); milestones use one window-function pass rather than a query per threshold. On my synthetic database (100k requests, 200k messages) each query took 15-77 ms, and the project has its own 2,000-conversation / 20,000-request benchmark (StatisticsPerformanceTests).
- Headers-not-bodies is applied where it was thought about: the summary list reads version headers and fetches excerpts with substr(body, 1, $max) (SqliteSummaryStore.cs:55); message rows are separate from message_block bodies, so scans of `message` stay cheap.
- The hot read paths are indexed: message UNIQUE(conversation_id, seq), conversation_by_workspace, generation_task_by_conversation, branch_context_range_by_ancestor, ai_request_by_task, and a partial index for unfinished tasks. Ancestor context needs no recursion because ranges are frozen at branch creation.
- Several resources are explicitly bounded: logs roll daily and keep 14 files (FileLoggerProvider.cs:16,87), back history is capped at 50 pages per section with disposal (Workbench.cs:346-356), migration backups keep 5, stale .partial backup files are swept, checkpoints coalesce on a slow disk (AnswerRunner.cs:190), and the live-counter timer exists only while a shown page has a running answer.
- A deterministic generator that writes through the real store (PerfSeed) plus smoke variants that run on every test pass keep the benchmark paths from rotting.

**Not examined**

- I did not build or run the app or any dotnet test or benchmark (forbidden by the task). Every UI-cost figure is extrapolated from the project's documented measurements or from reading the code.
- The delete and query timings come from a synthetic sqlite3-CLI database that reproduces the relevant tables, foreign keys and indexes, not from the real migrated schema via Microsoft.Data.Sqlite. Absolute numbers will differ; the scaling behaviour will not.
- No memory profiling: the retention findings (live answers, stacked pages) are from code reading, not heap snapshots.
- Windows-specific costs (Defender on per-read connection opens, fsync latency under synchronous=FULL, 15.6 ms timer granularity) were not measured.
- LiveMarkdown.Avalonia and Markdig internals, and Avalonia's own layout and render cost for the non-virtualized lists.
- The network page (NetworkCanvas.cs, NetworkLayout.cs) only superficially: I noted an O(n^2) collision pass over summary nodes but did not analyse render cost at hundreds of nodes.
- The summary page and history list, export, credential stores, the workspace-name preparation timers, and ReadingStateService flush behaviour beyond confirming its maps are small and per-conversation.
- Cold-cache startup on a large database: quick_check took 0.2 s warm on 577 MB; cold-disk time was not measured.
- Migration run time on a large database (v3, v6 and v10 rebuild whole tables) was not measured.
- The baseline output files (build, test, vuln, outdated, format) were not needed for this dimension, apart from confirming the desktop test run passed.

### performance-1. Deleting conversations or a workspace is quadratic: three foreign keys into `message` have no child-column index

- Final: **high** (confirmed); auditor said high
- Where: `src/Mindweave.Infrastructure/Sqlite/Migrations/SchemaMigrations.cs:58`; effort S

**Evidence.** Three child columns reference message(id) with no index: `supersedes_id TEXT REFERENCES message (id) ON DELETE RESTRICT` (line 58), generation_task `target_message_id TEXT REFERENCES message (id) ON DELETE RESTRICT` (v2, recreated in v6 without an index), and reading_position `message_id ... REFERENCES message (id) ON DELETE CASCADE` (v4; primary key is (conversation_id, slot_key)). Only branch_source.source_message_id is indexed. For every deleted message row SQLite must scan each unindexed child table in full. The deletes are `DELETE FROM message WHERE conversation_id = $id` (SqliteArchiveStore.cs:85) and `DELETE FROM message WHERE conversation_id IN (...)` (SqliteWorkspaceStore.cs:120), both inside one transaction on the single writer. Measured with the sqlite3 CLI on a synthetic database with the same keys and indexes (2,000 conversations, 200k messages, 100k tasks): one 100-message conversation took 2.50 s; 20 conversations (2,000 messages) took 49.4 s. After adding the three indexes the single conversation took 1.7 ms and a whole 200-conversation workspace (20k messages) took 1.3 s.

**Failure scenario.** A long-time user with roughly 200k stored messages deletes a 200-conversation workspace. The transaction runs for about 8 minutes (extrapolated from 49 s per 20 conversations). It holds the only writer the whole time, so checkpoints and terminal writes of answers running in other workspaces, drafts, reading positions and the exit sequence all queue behind it while the dialog shows a spinner. At a tenth of that data the same delete is still tens of seconds. A user who force-quits loses nothing (the transaction rolls back) but concludes delete is broken.

**Recommendation.** Add migration v11 with `CREATE INDEX message_by_supersedes ON message (supersedes_id) WHERE supersedes_id IS NOT NULL`, `CREATE INDEX generation_task_by_target ON generation_task (target_message_id) WHERE target_message_id IS NOT NULL` and `CREATE INDEX reading_position_by_message ON reading_position (message_id)`. Add a delete benchmark to the Performance category. Also review summary_node.current_version_id / pending_version_id and summary_edit_draft.baseline_id, which reference summary_version without an index (small tables today).

**Reproduction attempt: confirmed, high.** I could not refute it; an independent reproduction came out about twice as slow as the auditor's numbers.

**Schema is as claimed.** Three child columns reference message(id) with no usable index: message.supersedes_id (RESTRICT), generation_task.target_message_id (RESTRICT, recreated in v6 with only the unfinished, by_conversation and later by_model indexes), and reading_position.message_id (CASCADE, primary key (conversation_id, slot_key), WITHOUT ROWID). The only FK-into-message columns that are covered are branch_source.source_message_id and message_block's primary key prefix. No later migration (through v10) adds the missing indexes.

**Delete paths are as claimed.** Both run `DELETE FROM message WHERE conversation_id ...` inside one write transaction with foreign keys on. The preceding steps (delete the conversation's tasks, null its supersedes_id) do not help: SQLite still scans each whole child table once per deleted message row, and the other conversations' rows are still there.

**No mitigating design decision or test.** TRACK.md has no finding or pending decision about this. The existing Performance tests cover listing, opening, statistics and rendering, not delete. The s6-wrapup report states the full-scale benchmark was not run for archive/delete.

**The scale is the project's own.** docs/design/evolution-seams.md:62 defines "full scale" as 1,000 conversations x 200 rounds, about 400k messages, so 200k messages is inside the project's stated target rather than a contrived extreme.

**Reproduction** (sqlite3 3.54 CLI, default cache, WAL, synchronous=FULL, same k … (cut; full text in the workflow journal)

*Corrections to the finding.* - Line number: the message.supersedes_id declaration is at SchemaMigrations.cs:56, not :58.
- The cited delete line numbers are slightly off: SqliteArchiveStore.cs:85 and SqliteWorkspaceStore.cs:120 are right for the DELETE, but each is preceded by an `UPDATE message SET supersedes_id = NULL ...` step the finding does not mention. It does not change the outcome.
- The auditor's timings are, if anything, conservative. With realistic row sizes (400-byte blocks, 370 MB database, default page cache) I measured 5.2 s for one conversation and 118 s for 20 at 200k messages, against the claimed 2.5 s and 49 s. The "about 8 minutes" extrapolation for a 200-conversation workspace would be nearer 15-20 minutes on my numbers.
- "At a tenth of that data still tens of seconds" holds for a whole-workspace delete (20 conversations at 20k messages took 8.3 s, so 200 conversations would be more), but a si … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Synthetic SQLite database built with the sqlite3 3.54.0 CLI: tables workspace, conversation, message, message_block, generation_task, branch_source, reading_position with the same foreign keys and indexes as migrations v2-v10 (STRICT, WITHOUT ROWID where the app uses it), WAL, synchronous=FULL. 100 messages per conversation, one 800-character text block per message, one 'answer' generation_task per assistant message, one reading_position per conversation.

Delete script per run (foreign_keys=ON, one BEGIN IMMEDIATE ... COMMIT), mirroring MemberSteps:
  DELETE FROM generation_task WHERE conversation_id = ?;
  UPDATE message SET supersedes_id = NULL WHERE conversation_id = ? AND supersedes_id IS NOT NULL;
  DELETE FROM message WHERE conversation_id = ?;
  DELETE FROM conversation WHERE id = ?;

Actual output (wall-clock `real`, seconds):
  size 370581504
  1 conv @200k
  real 5.21
  20 conv @200k
  real 117.78
  size 37023744
  1 conv @20k
  real 0.46
  20 conv @20k (10% of data, one workspace)
  real 8.27
  size 92504064
  1 conv @50k
  real 1.18

Then on the 50k-message database, after
  CREATE INDEX a ON message (supersedes_id) WHERE supersedes_id IS NOT NULL;
  CREATE INDEX b ON generation_task (target_message_id) WHERE target_message_id IS NOT NULL;
  CREATE INDEX c ON reading_position (message_id);
deleting 20 conversations (2,000 messages):
  0.041 total
  remaining messages: 47900

</details>

**Reproduction attempt: confirmed, medium.** Reproduced through the real stores (SqliteArchiveStore.DeleteAsync and SqliteWorkspaceStore.DeleteAsync) on a database built by the real migrations.

The cost of deleting the same 100 messages grows linearly with the total number of stored messages, and disappears once the three proposed indexes exist:
- 25k stored messages: one 100-message conversation takes 328 ms.
- 102k stored messages: the same delete takes 1,487 ms (4.5x for 4x the data).
- 102k stored messages: a 10-conversation workspace (1,000 messages) takes 16.9 s.
- After adding the three indexes: 2.1 ms and 9.0 ms (about 700x and 1,900x faster).

Those gaps are far too large to be load noise. The index listing from the migrated database confirms message, generation_task and reading_position have no index on supersedes_id, target_message_id or message_id. The per-row cost (about 15 ms per deleted message at 100k messages) is consistent with the auditor's CLI numbers, and extrapolates to roughly 10 minutes for a 20k-message workspace in a 200k-message database.

Why medium rather than high: the claimed scenario needs about 200k stored messages. The project's own budgeted "heavy year" shape (PerfShape.Full, docs/evolution.md E6) is about 2,800 messages. Extrapolating from my measurements (not measured at these sizes), deleting an entire workspace costs roughly 1 s at 2.8k messages, 15 s at 10k and 60 s at 20k. So it is a real scalability defect with a trivial fix and no data loss, but it only becomes painful at several times the documented design scale.

Not verified: I did not test the claim that other writes (ch … (cut; full text in the workflow journal)

*Corrections to the finding.* - Line reference: message.supersedes_id is at SchemaMigrations.cs:56, not 58.
- Severity is overstated relative to the project's documented scale. The 8-minute scenario needs about 200k stored messages, roughly 70x the project's own budgeted "heavy year" (about 2,800 messages, PerfShape.Full / evolution E6). At that budgeted size a whole-workspace delete extrapolates to about 1 s. "At a tenth of that data ... still tens of seconds" holds for a workspace-sized delete (about 2,000 messages out of 20k extrapolates to roughly 6 s; a full 20k wipe to roughly 60 s), not for a single conversation (about 0.3 s at 25k, measured).
- In my run the workspace delete was slower per message (about 17 ms per message at 102k) than the single-conversation delete (about 15 ms), so the auditor's 49 s per 2,000 messages at 200k is, if anything, conservative.
- The mechanism, the affected statements and the f … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Temporary test at tests/Mindweave.Infrastructure.Tests/Performance/ZzFkIndexReproTests.cs (deleted afterwards). Run with:
dotnet test tests/Mindweave.Infrastructure.Tests --filter "FullyQualifiedName~ZzFkIndexReproTests" --logger "console;verbosity=detailed"

Test, condensed (the seed helper bulk-inserts rows with prepared statements inside h.Database.WriteAsync; each conversation gets 100 messages, 100 message_block rows, 50 completed 'answer' generation_task rows with target_message_id set, and 1 reading_position row):

[Fact]
public async Task Delete_cost_grows_with_total_stored_messages_until_child_indexes_exist()
{
    TimeSpan small;
    await using (var h = await ChatHarness.StartAsync(configure: false))
    {
        var ids = await SeedAsync(h, h.Workspace, 250);              // 25k messages
        small = await DeleteConversationAsync(h, ids[10]);           // h.Archive.InspectDeleteAsync + DeleteAsync
    }
    await using var big = await ChatHarness.StartAsync(configure: false);
    var main = await SeedAsync(big, big.Workspace, 1000);            // 100k messages
    var wsB = await AddWorkspaceAsync(big, 10);                      // +1,000 messages
    var wsC = await AddWorkspaceAsync(big, 10);                      // +1,000 messages
    var unindexedOne = await DeleteConversationAsync(big, main[10]);
    var unindexedWs  = await DeleteWorkspaceAsync(big, wsB);         // big.Workspaces.DeleteAsync
    await big.Database.WriteAsync((c, t) => { SqliteConnections … (cut; full text in the workflow journal)

</details>

### performance-2. The message list is not virtualized: opening a long multi-turn conversation blocks the UI thread in proportion to its total text, and this case is not benchmarked

- Final: **medium** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Desktop/Views/Conversations/ConversationView.axaml:196`; effort L

**Evidence.** `<ItemsControl Name="MessageList" ... ItemsSource="{Binding Messages}">` sits inside a plain ScrollViewer with the default non-virtualizing panel. Every row instantiates an AnswerView (even question rows, hidden: AnswerView.axaml.cs:162 says the template hosts it for questions too), and every finished answer does a full `Markdig.Markdown.Parse` plus control build and layout on the UI thread (AnswerView.axaml.cs:282). The project's own numbers (docs/design/s2-branching.md, 超长回答的渲染): 100,000 characters is 2,829 text blocks and 577-624 ms to open on macOS, described as a linear cost, with text blocks not virtualized; Windows desktop tests run about 3.5x slower. The render benchmark opens a conversation with exactly one answer (AnswerRenderPerformanceTests asserts `page.Messages.Count == 2`). The storage benchmark seeds a 200-turn conversation with 20,000-character answers (PerfShape.Full, about 4 MB of text) but measures only the SQL read (25.8 ms). docs/design/evolution-seams.md:65 itself names layout and virtualization of long Markdown as the main remaining risk.

**Failure scenario.** A user reopens a 60-turn conversation with 2,500-character answers (150,000 characters): about 0.9 s of frozen UI on macOS and about 3 s on Windows, on every open and every back navigation, since the view is rebuilt each time. The project's own seeded long conversation (200 x 20,000 = 4 million characters) extrapolates to about 24 s on macOS and over a minute on Windows, with more than 100,000 live text controls. During that time the window does not repaint or accept input.

**Recommendation.** First add a benchmark that opens the PerfSeed long conversation through the real page, so the number is known. Then either realize answers incrementally (render the rows near the reading position first and build the rest in idle-priority batches, keeping anchors and reading position working on realized rows) or use a virtualizing panel with estimated heights. The design doc already notes that anchors and reading position assume every block is laid out, so that assumption must change with it.

**Skeptic: partially confirmed, medium.** The structural claims hold. I could not find a guard that makes the scenario impossible: the message list is a plain ItemsControl inside a ScrollViewer with no ItemsPanel override (so the default non-virtualizing StackPanel), the style for ItemsControl.messages sets only Margin and ClipToBounds, the item template instantiates an AnswerView for every row, and a finished answer does a synchronous Markdig parse plus a full document update on the UI thread. There is no paging or cap on Messages in the view model. ViewLocator builds a new ConversationView per view model, so an open rebuilds all rows. The only UI render benchmark opens a conversation with one answer (Messages.Count == 2), and the 200-turn PerfSeed conversation is only measured at the storage layer.

What is overstated:
1. The cost numbers are extrapolations, not measurements. The only measured figure is one 100,000-character answer: 577-624 ms from the sidebar on macOS. Nobody has measured a multi-turn open; linear scaling is a reasonable guess (the design doc calls the cost linear) but it is unverified in either direction.
2. The Windows multiplier is misapplied. The auditor multiplies by the "3.5x slower desktop tests" figure, but TRACK.md (2026-11-06) attributes that slowdown mainly to disk sync under WAL + synchronous=FULL (about 38 fsyncs per test), not to CPU or layout. Opening a conversation is a read path with parse and layout, so "about 3 s on Windows" for 150,000 characters is not supported. The design doc states the render benchmark was never run on Windows, so the Windows figure is simply unknown.
3. … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. "About 3 s on Windows" and "over a minute on Windows" are unsupported: the 3.5x figure is a whole-test-suite slowdown that TRACK.md attributes mainly to fsync cost, which does not apply to parse/layout on open. The render benchmark has never been run on Windows.
2. The 0.9 s and 24 s macOS figures are linear extrapolations from a single-answer measurement, not measured results; they should be presented as estimates.
3. The 4-million-character case is a synthetic storage seed, not a representative conversation.
4. The non-virtualized text blocks are a documented, deliberately deferred trade-off (s2-branching.md:96, TRACK.md:99), so the new information is the unmeasured multi-turn aggregate and the missing page-level benchmark, not the absence of virtualization itself.
5. The rebuild cost applies to opening/navigating to a conversation; in-page reloads update rows in place.
Severity sho … (cut; full text in the workflow journal)

**Reproduction attempt: confirmed, medium.** Reproduced with a headless-Avalonia xUnit test (real Skia layout, Release build, macOS) that seeds an N-turn conversation through ConversationService and opens it from the sidebar the same way the existing AnswerRenderPerformanceTests.OpenAsync does.

Findings from the run:
- The message list's items panel is a plain StackPanel (non-virtualizing), and every row is realized: 2N AnswerView instances for N turns, i.e. one per question row too (hidden), as claimed.
- Open time grows linearly with total answer text, independent of how it is split across answers: 25k chars -> 248 ms, 50k -> 629 ms, 100k -> 1,041 ms, 150k (the claimed 60 x 2,500 scenario) -> 1,314 ms with 4,080 live text blocks, 400k (40 x 10,000) -> 3,215 ms with 11,240 text blocks. Roughly 8-10 ms per 1,000 characters.
- The cost is paid on every open: each of the 3 measured re-opens (after navigating to another conversation) cost the same, so nothing is cached between opens.
- The existing render benchmark only covers a single answer (page.Messages.Count == 2) and no test opens a multi-turn conversation through the page; a grep for "Virtualiz" in src/ returns nothing.

The claimed 0.9 s for 150k characters on macOS is, if anything, optimistic: I measured 1.3 s (other agents were building concurrently, so absolute values may be inflated somewhat; the linear shape and the order of magnitude are not load artifacts, since the 100k multi-turn figure of ~1.0 s is consistent with the project's own 577-624 ms for a single 100k answer plus per-row overhead).

Severity adjusted to medium rather than high: this is a respo … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. The 60 x 2,500 scenario measured 1.3 s on macOS in my run, not 0.9 s; the finding's figure was an extrapolation from the single-answer benchmark and slightly understates the multi-turn cost (machine was under load, so treat my number as an upper-ish estimate).
2. The 4-million-character case (200 x 20,000, about 24 s) and all Windows figures (about 3 s, over a minute) were NOT measured by me and remain extrapolations; my per-character rate (about 8 ms per 1,000 characters) would put the 4M case nearer 30 s on macOS, and "more than 100,000 live text controls" is consistent with my block counts (about 28 blocks per 1,000 characters -> roughly 112,000). PerfShape.Full is a storage stress shape, not a realistic conversation, so that scenario is a worst case rather than an expected one.
3. "Blocks the UI thread" for the whole duration: my measurement is wall-clock from the open command to … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Test added temporarily as tests/Mindweave.Desktop.Tests/Performance/ReproLongConversationOpenTests.cs (removed afterwards; nothing committed, worktree clean). Core of the test:

```csharp
[AvaloniaTheory]
[InlineData(10, 2_500)] [InlineData(20, 2_500)] [InlineData(40, 2_500)] [InlineData(60, 2_500)] [InlineData(40, 10_000)]
public async Task Repro_open_multi_turn(int turns, int charactersPerAnswer)
{
    await using var app = DesktopHarness.Create();
    var window = app.ShowWindow();
    await app.StartWithWorkspaceAsync("性能");
    await app.ConfigureModelAsync();
    var conversations = app.Services.GetRequiredService<ConversationService>();
    var answer = LongMarkdown.Generate(charactersPerAnswer);
    app.Client.Reply(answer);
    var started = await conversations.StartAsync(app.Workbench.CurrentWorkspace!.Id, "问题 0", CancellationToken.None);
    await AnsweredAsync(conversations, started.Conversation, 2);
    for (var i = 1; i < turns; i++)
    {
        app.Client.Reply(answer);
        await conversations.AskAsync(started.Conversation, $"问题 {i}", CancellationToken.None);
        await AnsweredAsync(conversations, started.Conversation, (i + 1) * 2);
    }
    // ... seed a short second conversation, then 4 times: open the short one, GC, start Stopwatch,
    // open the long one from the sidebar (OpenCommand, await page.Loaded, await view.Restored, window.UpdateLayout()),
    // stop; first run discarded as warm-up.
    Assert.Equal(turns * 2, page.Messages.Count); … (cut; full text in the workflow journal)
```

</details>

### performance-3. The sidebar builds every conversation row eagerly, and its reload does O(n^2) list work on the UI thread

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/Views/ShellView.axaml:108`; effort M

**Evidence.** `<ItemsControl Name="ConversationList" ItemsSource="{Binding Conversations.Items}">` in a ScrollViewer, non-virtualizing. Each row's template holds a Button, a ContextMenu with five MenuItems and five PathIcons, an ActivitySpinner, a badge, an Ellipse, a check Border and a rename TextBox: roughly 30 controls per row, all created whether or not the row is visible. In ConversationListViewModel.ReloadAsync (lines 499-514) the reorder loop calls `Items.Contains(ordered[i])` then `Items.Move(Items.IndexOf(ordered[i]), i)` per row: two linear scans per moved row, each move raising CollectionChanged. `Ancestors()` (line 284) rebuilds a dictionary of all rows on every call and is called per busy row in UpdateMarks and per selected row in SelectionRoots. The only list benchmark (StoragePerformanceTests, 200 ms budget) measures the SQL query, not the rows.

**Failure scenario.** A workspace with 1,000 conversations (the project's own PerfShape.Full) builds about 30,000 controls on the UI thread at startup and on every workspace switch before the window responds. Every answer that finishes moves its tree to the top; the reload then runs linear scans per moved row and relayouts the full list. Select-all followed by archive calls Ancestors() once per selected row, each rebuilding a 1,000-entry dictionary.

**Recommendation.** Use a virtualizing list for the sidebar (ListBox or ItemsRepeater with fixed row height). Share one ContextMenu instance across rows. In ReloadAsync compute moves with an index dictionary, and build the parent map once per pass. Add a 1,000-row sidebar open benchmark to the Desktop Performance tests.

**Skeptic: partially confirmed, low.** The structural facts are accurate, but the cost is unmeasured and the algorithmic part is overstated.

What holds:
- The sidebar is a plain ItemsControl inside a ScrollViewer with no ItemsPanel override, so Avalonia 12.1.4 uses a non-virtualizing StackPanel and realizes every row.
- Each row's DataTemplate declares its own ContextMenu (5 MenuItems, 5 PathIcons, 2 Separators) plus Button, DockPanel, branch PathIcon, check Border and PathIcon, marker Panel, ActivitySpinner, badge Border and TextBlock, Ellipse, title TextBlock and rename TextBox. That is about 28 XAML-declared objects per row, so "roughly 30" is fair as an object count.
- ReloadAsync does call Items.Contains then Items.Move(Items.IndexOf(...), i) per mismatched row.
- Ancestors() rebuilds the parent dictionary on every call, and is called per busy row in UpdateMarks and per selected row in SelectionRoots / IsBelow.
- The only Desktop performance test is AnswerRenderPerformanceTests; nothing measures the sidebar. PerfShape.Full is 1,000 conversations, and the storage benchmark covers only the query.
- I found no decision or known finding in TRACK.md or docs/design that accepts a non-virtualized sidebar. The only virtualization note (s2-branching.md:96) is about answer text blocks.

What is overstated:
- **"O(n^2) on reload" is wrong for the scenario given.** When a finished answer moves its tree to the top, only that tree's k rows mismatch; after they move, the rest hit the `Items[i] == ordered[i]` fast path. That is O(k·n) reference comparisons, microseconds at n=1000.
- **The true quadratic case is negligible … (cut; full text in the workflow journal)

*Corrections to the finding.* - "O(n^2) list work on reload" is wrong for the answer-finishes case: it is O(k·n) for a moved tree of k rows. It is quadratic only on a full repopulation, and that is about 1 ms at n=1000.
- "Relayouts the full list" on each move and "before the window responds" are unmeasured assertions.
- "About 30,000 controls" counts XAML-instantiated objects. Per-row ContextMenu contents are not in the visual tree or templated until opened, and hidden elements skip layout, so visual-tree cost is roughly half that or less.
- The Ancestors() rebuild costs tens of milliseconds for a 1,000-row select-all and is negligible in UpdateMarks.
- The list shows only non-hidden conversations of the current workspace, so 1,000 rows requires 1,000 unarchived conversations in one workspace.
- Severity should be low, not medium: a scalability and benchmark-coverage gap, not a demonstrated regression.

### performance-4. The workspace list query returns the full text of every conversation's first question, and it is the only list API (about 11 callers, re-run on every generation event)

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteConversationStore.cs:20`; effort S

**Evidence.** ListAsync selects `(SELECT b.text FROM message m JOIN message_block b ... WHERE m.conversation_id = c.id AND m.role = 'user' ORDER BY m.seq LIMIT 1)` for every conversation, untruncated, even when the conversation has a title; the text is only used as a fallback title (UiText.ConversationTitle, UiText.Conversation.cs:51-53). Questions may be up to 100,000 characters (ConversationService.MaxQuestionLength). Callers include the sidebar reload (ConversationListViewModel.cs:480, triggered by ConversationChanged, ActivityChanged and marks changes), ConversationMarksService.UnreadByWorkspaceAsync (line 109, one full list per workspace whenever anything is unread), WorkspaceNewsViewModel.TitlesAsync (line 390, per workspace), ConversationArchiving.TitlesAsync (line 35, to name a few busy rows), SummaryExportService (line 124), WorkspaceNamePreparation (lines 353, 465), NetworkHomeViewModel (line 270) and ArchivedViewModel (line 247). Measured on the synthetic database: 1,800 conversations with 30,000-character first questions return 54 MB of text per call (52 ms in SQLite alone, before .NET transcodes it into about 108 MB of strings). PerfSeed's questions are about 15 characters, so the 29.7 ms list baseline does not see this.

**Failure scenario.** A user who habitually pastes documents as the first question (20-30k characters) and has 1,000-2,000 conversations: each question asked triggers several sidebar reloads (enqueue, streaming start, finish, title), each allocating tens to a hundred megabytes of strings that are immediately discarded. With unread marks in several workspaces, the per-workspace fan-out multiplies it. The result is GC pauses and visible sidebar lag during streaming.

**Recommendation.** Return `CASE WHEN c.title IS NULL THEN substr(b.text, 1, N) END` (N a little above the fallback-title length). Add narrow queries for the other needs: titles for a given set of ids, and unread ids grouped by workspace in one statement. Seed PerfSeed with some long first questions.

**Skeptic: partially confirmed, low.** The code facts hold. ListAsync selects the whole first user block (`b.text`) for every conversation in the workspace, with no `substr` and no `c.title IS NULL` guard. The value is only used as the third-choice fallback title, cut to 30 characters (ConversationRules.FallbackTitleLength), after the title and the branch quote. Questions may be up to 100,000 characters. It is the only conversation-list query, and all the cited callers exist. The read runs on a thread-pool thread over a fresh read-only connection, so the cost is allocation and GC, not a blocked UI thread.

Nothing in TRACK.md or decisions.md records this as known or intended. The A5 fix of 2026-11-01 applied exactly this principle to summary lists ("lists and network read only version headers", bounded `substr` excerpts) but did not touch the conversation list. PerfSeed questions are short strings, so the 29.7 ms baseline cannot see it.

Why the severity is overstated:
- **Reload frequency.** "Re-run on every generation event" is too strong. The sidebar reload goes through a 100 ms UiThrottle plus SerialRefresh, and ConversationChanged fires at enqueue, answer finish and title save, not per streamed chunk. I did not check how often ActivityChanged fires. A question costs a handful of reloads, not a stream of them.
- **Fan-out.** UnreadByWorkspaceAsync lists once per *other* workspace, and only when something is unread. It is called from WorkspaceNewsViewModel, not on every sidebar reload.
- **Scenario.** The claimed case needs 1,000-2,000 conversations in a single workspace, nearly all opening with a 20-30k-char … (cut; full text in the workflow journal)

*Corrections to the finding.* - "Re-run on every generation event" is overstated: reloads are coalesced by a 100 ms throttle and a serial refresh, and ConversationChanged is raised at enqueue, finish and title save, not per streaming chunk.
- The unread fan-out covers only workspaces other than the current one, only while something is unread, and is driven by WorkspaceNewsViewModel rather than by every sidebar reload.
- "About 11 callers" is right in order of magnitude: I count 11 call sites outside the store, including two the finding did not list (WorkspaceDeletionViewModel.cs:268, WorkspaceNameSuggestions.cs:104).
- The 54 MB / 52 ms / 108 MB figures were not reproduced; they are arithmetically plausible only for the extreme profile where every one of 1,800 conversations opens with a 30k-character question.
- "Visible sidebar lag" is not demonstrated: the query and string materialisation run off the UI thread, so … (cut; full text in the workflow journal)

### performance-5. Each conversation page reload reads the whole conversation twice and the ancestor chain twice over about 15 fresh connections; hidden pages in the back stack reload too

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/Conversations/ConversationViewModel.cs:377`; effort M

**Evidence.** LoadAsync calls _conversations.LoadAsync (GetAsync, GetMessagesAsync with all bodies, CompressedAnswersAsync), then GetLineageAsync, ListBranchesAsync, ArchivedAmongAsync, PlanRestoreAsync, then GetContextUsageAsync (line 421). GetContextUsageAsync (ConversationService.cs:180-204) calls GetMessagesAsync again (line 188), BudgetForAsync (RecentTokenSamplesAsync: a join over every task of that model, sorted, LIMIT; 37 ms at 100k tasks on the synthetic database), GetAncestorContextAsync (line 190, full ancestor bodies) and then PlanAsync, which calls GetAncestorContextAsync a second time (line 595). Each call opens its own unpooled connection (`Pooling = false`, SqliteConnections.cs:37) and its own snapshot. The reload fires on every ConversationChanged for the page or a related conversation (send, finish, title, branch, archive) and on model changes. Pages stay subscribed while in the back stack: Workbench keeps up to 50 per section per workspace (Workbench.cs:356); a sidebar click always pushes a new page (ConversationListViewModel.cs:129) and PushCore only de-duplicates against the top of the stack (Workbench.cs:340), so A, B, A, B... creates many live instances of the same conversation, each holding all message bodies.

**Failure scenario.** A user alternates between two long conversations 20 times and then asks in one of them. Send, finish and title each make every stacked instance of that conversation re-read all its bodies twice, its ancestors twice, and re-estimate tokens over everything: about 10 hidden pages x 3 events x megabytes of text. For the seeded 4 MB conversation that is hundreds of megabytes of transient allocation per question, and up to 50 x the conversation text retained per section.

**Recommendation.** Pass the already-loaded messages and ancestor context into the context-usage calculation (one read per reload). Cache token calibration per (base_url, model) and invalidate when a request is recorded. Do the page load in one ReadAsync snapshot. Have hidden pages mark themselves stale and reload when shown again. When pushing a route already in the stack, move the existing page to the top instead of creating a duplicate.

**Skeptic: partially confirmed, low.** Every mechanical claim checks out by reading the code; nothing was run or measured. I found no guard, design decision or TRACK.md entry that makes it intended or impossible.

What is true:
- **Double read of the conversation.** The page's LoadAsync reads all messages via the service's LoadAsync, then GetContextUsageAsync reads them again.
- **Double read of the ancestor chain.** GetContextUsageAsync calls GetAncestorContextAsync, then PlanAsync calls it again; each call loads full ancestor message bodies.
- **Uncached calibration.** BudgetForAsync queries RecentTokenSamplesAsync on every call.
- **One connection per call.** Each store call goes through ReadAsync, which opens its own unpooled read-only connection and BEGIN snapshot. A reload makes roughly 13-16 such calls, plus Summary.LoadAsync.
- **Hidden pages reload.** The handlers for ConversationChanged, ModelChanged and SummaryChanged check only `_disposed`. `_isShown` gates only the elapsed-time tick. Pages unsubscribe only in Dispose, which runs when they leave the stack.
- **Duplicate instances.** PushCore de-duplicates only against the top of the stack, with a cap of 50 per section. A sidebar click goes through Navigate to PushCore, so A, B, A, B creates a new page each time.

What is overstated:
- **Root conversations pay less.** With no ancestors, both GetAncestorContextAsync calls return an empty chain cheaply and GetSourceAsync is skipped. The "ancestors twice" cost applies only to branch conversations.
- **The headline numbers need an extreme input.** "Hundreds of MB per question" depends on the 4 MB syntheti … (cut; full text in the workflow journal)

*Corrections to the finding.* - The double ancestor read costs something only for branch conversations; for a root conversation both calls return an empty chain.
- "About 15 connections" is roughly right (13-16 plus the summary load) but varies with ancestors and configuration.
- "Hundreds of MB per question" and "50 x retained" hold only for the synthetic 4 MB conversation with many stacked duplicates; realistic conversations cost a few MB of transient allocation.
- The reads run off the UI thread and are coalesced, so no jank from them is demonstrated.
- The 37 ms calibration query at 100k tasks was not re-measured here.
- Severity should be low: an efficiency and cleanliness defect, not a likely user-visible problem.

### performance-6. The performance harness is opt-in, not in CI, has no write or delete benchmark, and has never run on Windows

- Final: **low** (confirmed in part); auditor said medium
- Where: `scripts/perf.sh:10`; effort M

**Evidence.** perf.sh runs two `dotnet test --filter Category=Performance` commands with MINDWEAVE_PERF=1. .github/workflows contains only ci.yml and stress.yml, and neither mentions perf. What is covered: three storage reads (list, load long conversation, deepest-branch context), the statistics load, and showing, opening and streaming one single-answer conversation. What is not covered: any write path (checkpoint, terminal write, delete conversation, delete workspace, backup via VACUUM INTO), opening a multi-turn conversation in the UI, a 1,000-row sidebar, startup (quick_check plus recovery), the network page, memory. docs/design/s2-branching.md states the budgets are 2-2.5x the local macOS measurements, are only meaningful on that machine, and that this benchmark set has not been run on Windows, where desktop tests are about 3.5x slower. The recorded storage baseline in docs/design/evolution-seams.md:65 is dated 2026-10-27, before the archive subqueries (v8) were added to the list query.

**Failure scenario.** The two high findings in this report (quadratic delete, long-conversation open) pass every existing benchmark, because no benchmark exercises them. A future regression of 5x on Windows, or in any write path, would ship unnoticed: nothing runs these tests unless someone remembers to run the script on a Mac.

**Recommendation.** Add benchmarks for delete conversation, delete workspace, open the PerfSeed long conversation through the page, and a 1,000-row sidebar. Run the Performance category on a schedule in CI on both operating systems, with per-OS budgets or relative-regression thresholds, and record the numbers per commit.

**Skeptic: partially confirmed, low.** Every factual claim I could check is accurate; the severity is overstated.

**What holds**
- The full-size benchmarks are opt-in. `scripts/perf.sh` runs two `dotnet test --filter Category=Performance` commands with `MINDWEAVE_PERF=1`, and each full-size test skips or downsizes unless that variable is set.
- CI never runs them with budgets. `ci.yml` runs an unfiltered `dotnet test` without `MINDWEAVE_PERF`, so the Performance-category tests skip or run at smoke size. `stress.yml` accepts a filter but cannot set the variable. Neither workflow has a schedule.
- Coverage is exactly as listed. The storage test times only list, load of the long conversation, and deepest-branch context. The statistics test times one load. The desktop test times show, open and stream for one long answer; the open case seeds one long answer plus one short conversation, so the sidebar has 2 rows.
- No write, delete, backup, startup, network-page or memory benchmark exists. The only other Stopwatch uses in tests are in fault, lifecycle and file-writer tests, not benchmarks.
- The Windows statement is quoted correctly from `docs/design/s2-branching.md:97`.
- The storage baseline is stale. It is dated 2026-10-27, while migration v8 later added the `conversation_archive` subqueries to the list query. `PerfSeed` and the storage test were last touched on 2026-10-27, apart from the statistics addition on 2026-11-12.

**Why the severity drops to low**
- The opt-in design is deliberate and documented: E6 says the full size "需显式运行 scripts/perf.sh" and the budgets exist "用来发现数量级退化".
- The Windows gap is self-di … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity should be low, not medium: the opt-in design and the missing Windows run are both documented in the design docs, and the impact is indirect.
- "Nothing runs these tests" needs a qualifier: smoke-size variants of all three benchmark classes run in every CI job on Ubuntu and Windows, so the code paths are exercised there, just not timed and not at full size.
- "Before the archive subqueries were added" applies to the recorded baseline number only. The 200 ms list budget is enforced against the current query whenever `scripts/perf.sh` is run.
- The failure scenario leans on two other "high" findings (quadratic delete, long-conversation open) that I did not verify; it should not inherit their severity.
- The cited location `scripts/perf.sh:10` is correct; the second command is on line 11.

### performance-7. Every cascading delete copies the whole database on the writer thread, and those copies are never pruned

- Final: **low** (confirmed in part); auditor said medium; already known in TRACK.md
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteBackupFiles.cs:164`; effort M

**Evidence.** Prune's doc comment: "Keeps the newest keep upgrade backups; backups of any other reason are never pruned." It filters on `b.Reason == BackupReason.Migration` (line 171). ConversationDeletionService.DeleteAsync (ConversationDeletion.cs:70-74) makes a BeforeConversationDelete backup whenever the scope has branches or summaries; WorkspaceDeletion.cs:81 and BackupDialogs.cs:206 make others. BackupAsync runs `VACUUM INTO` followed by quick_check of the copy inside `writer.EnqueueWithoutTransactionAsync` (SqliteDatabase.cs:213-217), i.e. on the single writer, so all other writes wait for the full copy and verification. Keeping these backups is a recorded design decision (round 4 review: a pre-delete backup may be the only copy), and item 5 added a manual cleanup page with sizes.

**Failure scenario.** A user with a 500 MB database tidies up by deleting ten conversations that have branches or summaries, one at a time. Each delete first writes and verifies a 500 MB copy (seconds, during which running answers cannot checkpoint), and 5 GB of backups accumulate with no cap and no warning until the user finds the storage page. On a nearly full disk the eleventh backup fails and deletes are refused.

**Recommendation.** Keep the safety property but bound the cost: reuse a pre-delete backup made within the last few minutes for consecutive deletes, show total backup size and a warning above a threshold, and offer age-based pruning of delete backups (for example keep the newest N or 30 days) as a user-visible setting. Consider the online backup API from a separate connection so the writer is not held for the whole copy.

**Skeptic: partially confirmed, low.** The code facts are accurate, but the behaviour is a recorded, deliberate design choice and the failure scenario depends on an unrealistic database size, so medium is overstated.

**What holds:**
- `Prune` only rotates `BackupReason.Migration` backups, and its only caller is the post-migration cleanup. Pre-delete and pre-restore backups are never removed automatically.
- `BackupAsync` runs `CreateVerified` (`VACUUM INTO`, then `quick_check` and header checks on the copy) inside `EnqueueWithoutTransactionAsync` on the single writer, so every other write queues behind the full copy and verification.
- A cascading conversation delete makes one such backup per delete operation; workspace delete and restore make their own.
- There is no cap, no age-based pruning, no reuse of a recent backup and no size-threshold warning.

**What weakens it:**
- **Intended design.** Never pruning is an explicit round-4 review fix: a pre-delete backup is the only copy of the deleted content and must not be rotated out. D-005 limits rotation to migration backups, and "backup fails, so delete nothing" is also a stated rule. The "eleventh delete is refused on a full disk" outcome is the designed fail-safe, not a defect.
- **Accumulation was already known and addressed.** TRACK.md recorded "pre-delete backups accumulate, no cleanup entry" and item 5 (merged 2026-11-12) answered it with a storage page offering single and multi-select delete with sizes.
- **Part of the recommendation already exists.** The storage page already shows the total size of all backups (`TotalText`), so "show total backup size" … (cut; full text in the workflow journal)

*Corrections to the finding.* - "Every cascading delete" is one backup per delete operation, not per conversation: a multi-select delete of ten trees makes one backup, and a delete refused in the transaction discards its backup.
- "No warning until the user finds the storage page" is partly wrong: the cascade-delete dialog itself says the backup will be listed under Data & Storage, and that page already shows the total backup size. The recommendation to "show total backup size" is already implemented; only a threshold warning, age or count retention, and reuse of a recent backup are missing.
- The 500 MB / 5 GB scenario is not realistic for a text-only chat database; at plausible sizes the writer stall is sub-second.
- "Running answers cannot checkpoint" overstates the effect: their writes are queued behind the copy and complete afterwards; nothing is lost.
- "Deletes are refused when the backup fails" is the designe … (cut; full text in the workflow journal)

### performance-8. Live answers are never released: every answer generated in a session keeps its full text in memory until exit

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Conversations/GenerationCoordinator.cs:197`; effort S

**Evidence.** `_live[answer.Live.MessageId] = answer.Live;` is the only mutation of `private readonly ConcurrentDictionary<MessageId, LiveAnswer> _live` (line 27). The only other use is `Observe` (line 140). There is no TryRemove or Clear anywhere in the file. Each LiveAnswer owns a StringBuilder holding the whole answer (LiveAnswer.cs:27). Related: `Snapshot()` (LiveAnswer.cs:53) does `_text.ToString()` on every call, and is used merely to read State in `IsLive` (GenerationCoordinator.cs:182, called from HasUnfinishedAnswer, UnfinishedUserWork and AnswerWritesSettledAsync) and at 20 Hz per bound row in MessageItemViewModel.ApplyLive (line 208). At 100,000 characters each call is a 200 KB large-object-heap allocation.

**Failure scenario.** The app is left running for weeks, which is normal for a desktop tool. Every answer ever streamed in that process stays reachable from the singleton coordinator: unbounded growth of roughly 2 bytes per generated character. While a very long answer streams, each 50 ms UI tick and each sidebar mark update allocates a full copy of the text, driving gen-2 collections.

**Recommendation.** Remove the entry once the answer's terminal write has landed and the page has been told to reload (after AnswerFinished). Add a cheap `State` / `IsRunning` accessor that does not copy the text. Let the UI pull only the appended tail (length plus delta) instead of a full snapshot per tick.

### performance-9. Streaming checkpoints rewrite the whole answer every second, so bytes written grow quadratically with answer length

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteConversationStore.cs:313`; effort M

**Evidence.** CheckpointLoopAsync (AnswerRunner.cs:176-200) fires every `CheckpointInterval` (1 s, GenerationWork.cs:14), takes a full snapshot and calls CheckpointAsync, which calls ReplaceBlocks: `DELETE FROM message_block WHERE message_id = $id` followed by an INSERT of the entire text (lines 313-327). Each is its own transaction under `PRAGMA synchronous = FULL` (SqliteConnections.cs:28), so one fsync per second per running answer (two slots). TRACK (2026-11-06) already records that FULL sync dominates on Windows; the quadratic volume is not recorded.

**Failure scenario.** A 100,000-character Chinese answer (about 300 KB UTF-8) streamed over 10 minutes produces about 600 checkpoints averaging 150 KB: roughly 90 MB of WAL writes and 600 fsyncs for a 300 KB result, all on the single writer that sidebar renames, drafts and other answers' terminal writes also wait on. Typical 5-20k-character answers are unaffected.

**Recommendation.** Append-only checkpoints (store text chunks as additional ordinals or a side table and compact at finish), or lengthen the interval as the answer grows (for example checkpoint when N new characters or T seconds have passed, with T scaling with size).

### performance-10. message_block stores large Markdown bodies in a WITHOUT ROWID table, forcing nearly every answer onto overflow pages

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Sqlite/Migrations/SchemaMigrations.cs:124`; effort S

**Evidence.** `CREATE TABLE message_block (... text TEXT NOT NULL, data TEXT, PRIMARY KEY (message_id, ordinal)) STRICT, WITHOUT ROWID;`. SQLite's guidance is that WITHOUT ROWID suits small rows: an index b-tree keeps only about 1 KB of a row locally on a 4 KB page, where a rowid table keeps about 4 KB. Measured with the sqlite3 CLI on 20,000 rows (half 80-character questions, half 2,400-character answers): the WITHOUT ROWID table took 46.9 MB with 10,000 overflow pages (one per answer); the equivalent rowid table plus its primary-key index took 40.9 MB with no overflow pages.

**Failure scenario.** Every answer longer than about 1 KB costs an extra overflow-page read on load and about 15% more file size for mid-length answers. That size carries into every VACUUM INTO backup and every quick_check. The effect is modest and constant-factor, not a scaling failure.

**Recommendation.** If the table is rebuilt for another reason, make it a rowid table with a UNIQUE(message_id, ordinal) index. Not worth a migration on its own.

