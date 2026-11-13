# Desktop UI and ViewModel correctness — grade B

The Desktop layer is well above course-project quality. Compiled bindings are on by default, pages are disposed when they leave the back stack and unsubscribe from long-lived services, stale async results are guarded in most places, lists update in place, and focus handling is deliberate. I found no systematic leaks and no XAML binding defects.

It is not an A because several cross-cutting gaps remain, concentrated in the newest code:
- **Crash path:** any exception an async command does not catch ends the process, and at least one deterministic path exists (renaming from a conversation page while storage is paused).
- **Stale per-page state:** the back stack allows duplicate pages of one conversation, each holding its own copy of the shared draft; app-global settings are loaded once into per-workspace page view models.
- **Dead refresh:** the Workspaces page's recount on return never runs because its flag lives in a view that is rebuilt on every navigation.
- **Smaller items:** inconsistent settings navigation targets, pointer-state edge cases on the network canvas, and no load-failure state on most pages.

All findings come from reading the code; nothing was executed. The baseline Desktop test run in the scratchpad shows 381 passed, 58 skipped, 0 failed, and none of these scenarios is covered by a test.

**Strengths**

- Compiled bindings are the default (Mindweave.Desktop.csproj: AvaloniaUseCompiledBindingsByDefault=true) and every view declares x:DataType, so a wrong binding path fails the build. ViewLocator uses an explicit registry rather than name reflection.
- Page lifetime is handled consistently: Workbench.Release disposes every page leaving a stack (GoBack, Replace, Forget, RemoveWorkspace, ReleaseAll, MaxHistory trimming). ConversationViewModel, SummaryViewModel, NetworkHomeViewModel, ArchivedViewModel, StorageDetailsViewModel, WorkspacesViewModel and SettingsHomeViewModel all unsubscribe from long-lived services in Dispose and guard late callbacks with a _disposed flag.
- Stale async results are handled deliberately: SerialRefresh guarantees the last load to finish started after the last request; UsageStatisticsViewModel uses a generation counter plus a dispose token; WorkspaceRowViewModel.Owns(request) drops late name suggestions; WorkspaceDeletionViewModel and ConversationDeletionViewModel re-check Target/IsOpen after each await.
- Lists update in place: MessageListReconciler keeps valid message controls so selections and highlights survive reloads; ConversationListViewModel.ReloadAsync moves and inserts rows rather than rebuilding; WorkspacesViewModel.Sync keeps rows by identity across a rename.
- Views that subscribe to view-model events unsubscribe when the DataContext changes (ShellView.WatchList, WorkspacesView.Watch, SidebarHeader.Watch, NetworkCanvas.OnDataContextChanged, MainWindow.OnDataContextChanged). Window-level handlers and key bindings are removed in OnDetachedFromVisualTree (ShellView, SettingsHomeView, ConversationView).
- NetworkCanvas uses one canvas-to-screen transform for drawing, hit-testing and dragging; the drag is placed in canvas coordinates so a wheel pan or zoom mid-drag keeps the node under the pointer; a drag threshold separates click from drag; capture loss during a node drag reverts cleanly; the motion timer stops itself once settled and on detach.
- Write ordering is explicit where it matters: sidebar layout, news settings, currency and price saves are chained so the last choice wins, and UsageStatisticsViewModel.LoadAsync awaits Saving before reading prices.
- Keyboard and focus handling is thorough: roving arrow keys on segmented pickers, focus returned to the originating button after inline rename or price edit, Esc precedence (stop suggestion before cancel rename), Shift+F10 row menu, keyboard navigation and automation item status on UsageChart, and GhostTextBox refusing to accept an offer that is not visibly drawn.

**Not examined**

- ExitCoordinator.cs, BackupDialogs.cs (BackupRestoreViewModel/BackupDeletionViewModel), RecoveryViewModel.cs, StartupViewModel.cs and AppStartup.cs were not read in full; exit, restore and recovery flows are not assessed here.
- Markdown/ (SourceAnchors, SelectionCarry, SourceHighlightMark) and the lower two thirds of AnswerView.axaml.cs (selection carry-over, floating branch action placement) were not read.
- BranchMarks.cs, TreeIndent.cs, Motion.cs, ActivitySpinner.cs, WorkspaceSwitcherButton.cs, NetworkPalette.cs, NetworkPaint.cs, NetworkLayout.cs and Converters.cs were not read.
- Most .axaml files were not read line by line (only WorkspacesView.axaml in full, plus targeted greps of ShellView, ConversationView and NetworkHomeView); styles and theme resources were not reviewed.
- Nothing was executed: no build, tests or app run, per the read-only rule. Every failure scenario is derived from the code and is not reproduced.
- Windows-specific pointer, capture and focus behaviour, IME composition and screen-reader behaviour were not verified.
- Rendering performance of NetworkCanvas, long conversations and the non-virtualised sidebar list was not measured. The conversation message list being non-virtualised is a documented design limit (docs/design/s2-branching.md) and was not re-audited.
- Dialog overlay modality in MainWindow.axaml (whether background controls are reachable while a dialog is open) was not verified.

### ui-vm-1. An uncaught exception in an async command terminates the app; renaming a conversation while storage is paused does this deterministically

- Final: **high** (confirmed); auditor said high
- Where: `src/Mindweave.Desktop/ViewModels/Conversations/ConversationViewModel.cs:296`; effort S

**Evidence.** ConversationViewModel.CommitRenameAsync (lines 295-300) is a [RelayCommand] that does `IsRenaming = false; await _conversations.RenameAsync(Id, RenameText, CancellationToken.None);` with no try/catch. ConversationService.RenameAsync goes through CommitAsync, whose first line is `_health.ThrowIfBlocked()`, which throws StorageWriteException while storage is paused. The sidebar's equivalent path, ConversationListViewModel.RenameAsync (lines 375-385), catches StorageWriteException, so the exception is expected. ConversationView.axaml lines 14 and 76 bind the rename and save buttons with no storage-pause condition. CommunityToolkit's AsyncRelayCommand.Execute rethrows a faulted task on the UI SynchronizationContext unless FlowExceptionsToTaskScheduler is set, and it is set nowhere. Program.cs lines 36-37 only log AppDomain.UnhandledException and UnobservedTaskException; no Dispatcher.UIThread.UnhandledException handler exists anywhere in src. A second instance: ModelSettingsViewModel.RunAsync (lines 271-295) catches only ModelSettingsException, ChatClientException and CredentialUnavailableException, while Save writes through SqliteModelSettingsStore.SaveActiveAsync, which can surface StorageWriteException or SqliteException.

**Failure scenario.** Storage is paused (disk full or database locked; the page shows its storage notice and is meant to stay readable). The user clicks Rename on the conversation page toolbar, types a name and presses Save. RenameAsync throws StorageWriteException, AsyncRelayCommand rethrows it on the dispatcher, the UI loop unwinds and the app exits. The same happens when saving model settings while the database write fails. AppHost.Run's finally block still closes the database, so stored data is safe, but the session ends without warning.

**Recommendation.** Catch StorageWriteException in ConversationViewModel.CommitRenameAsync and show a notice, keeping the typed name. Widen ModelSettingsViewModel.RunAsync to report storage failures. Add a Dispatcher.UIThread.UnhandledException handler that logs and shows a non-fatal notice (or set FlowExceptionsToTaskScheduler with a central reporter), and add a test that runs each page's write commands under a blocked StorageHealth.

**Skeptic: confirmed, high.** I could not refute this; every cited fact checks out in the code. It was verified by reading only (no build or run permitted), so the final step — the dispatcher exception ending the process — rests on library behaviour and the project's own docs, not a reproduction.

**Rename path**
- `ConversationViewModel.CommitRenameAsync` is a `[RelayCommand]` with no try/catch around `_conversations.RenameAsync`.
- `ConversationService.RenameAsync` calls `CommitAsync`, whose first statement is `_health.ThrowIfBlocked()`. Its doc comment says a `StorageWriteException` reaches the caller.
- So while storage is blocked, any non-blank rename throws every time. Blank input returns early and is the only escape.
- The view does not gate this. `RenameButton` and `SaveRenameButton` bind only to `IsRenaming` / `IsMissing`; the view model pushes the storage-paused state only to the Composer.
- The sidebar equivalent (`ConversationListViewModel.RenameAsync`) and about ten other view models do catch `StorageWriteException`, so this one is an omission, not a design choice.

**Why it ends the session**
- CommunityToolkit.Mvvm is pinned at 8.4.2; the DLL contains `AsyncRelayCommand.AwaitAndThrowIfFailed`, the async-void rethrow used when `FlowExceptionsToTaskScheduler` is not set.
- Grep of `src` finds no `FlowExceptionsToTaskScheduler`, no `AsyncRelayCommandOptions`, and no `Dispatcher.UIThread.UnhandledException` handler.
- `Program.cs` only logs `AppDomain.UnhandledException` and `UnobservedTaskException`; neither prevents termination.
- `AppHost.Run`'s finally closes the database "also when the l … (cut; full text in the workflow journal)

*Corrections to the finding.* - The model-settings instance is conditional, not deterministic. `ModelSettingsService` never calls `ThrowIfBlocked`, so Save crashes only if the write itself fails at that moment; the write queue normally surfaces that as `StorageWriteException` (per s6-wrapup.md:173).
- The file is at src/Mindweave.Desktop/ViewModels/Pages/ModelSettingsViewModel.cs; the finding gave no path.
- A blank or whitespace rename returns before `CommitAsync` and does not throw.
- The claim that the app exits was not reproduced at runtime (read-only audit). It rests on CommunityToolkit's documented async-void rethrow, the absence of any dispatcher handler, and the project's own docs.

**Reproduction attempt: confirmed, high.** Reproduced through the real window in a headless Avalonia test at track/claude-auto 1ba95c9. With StorageHealth blocked (Full), the conversation page's RenameButton and SaveRenameButton were both clickable. Clicking Save ran CommitRenameAsync, ConversationService.RenameAsync threw StorageWriteException from StorageHealth.ThrowIfBlocked, and CommunityToolkit's AsyncRelayCommand.AwaitAndThrowIfFailed rethrew it onto the Avalonia dispatcher, where it reached Dispatcher.UIThread.UnhandledException. The test only survived because it attached its own handler with Handled = true.

Production has no such handler. Program.cs:36-37 hooks only AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException, both log-only, and a grep of src for FlowExceptionsToTaskScheduler finds nothing. AppHost.Run (AppHost.cs:36-48) is try/finally with no catch, so the exception unwinds StartWithClassicDesktopLifetime and the process exits after the finally closes the database. That last step (actual process exit) is inferred from code, not observed: I did not launch the real app.

The path is deterministic, not timing-dependent: ThrowIfBlocked is the first statement of CommitAsync. The sidebar's equivalent (ConversationListViewModel.RenameAsync, lines 375-385) catches StorageWriteException, which confirms the page path is an oversight. High stands: a documented degraded state that is meant to stay readable turns an ordinary toolbar action into an abrupt exit, though without data loss.

The second instance (ModelSettingsViewModel) was checked by code reading only. RunAsync catches just ModelS … (cut; full text in the workflow journal)

*Corrections to the finding.* - ConversationListViewModel is at src/Mindweave.Desktop/ViewModels/Conversations/ConversationListViewModel.cs (the line numbers 375-385 are right).
- The ModelSettings instance is overstated as "the same happens": that save path has no ThrowIfBlocked that I found, so it needs an actual failing database write, not just the paused state. It was verified by reading only.
- "The app exits" is inferred from the missing dispatcher handler and AppHost.Run's try/finally; the test shows the exception reaching the dispatcher's unhandled-exception path, not a real process exit.
- The typed name is not lost from the view model (RenameText stays), but IsRenaming is set to false before the await, so the edit box would already be closed if the app survived.

<details><summary>Reproduction</summary>

Temporary test tests/Mindweave.Desktop.Tests/ReproRenamePausedTests.cs (deleted afterwards). Setup follows the existing RowMenuUiTests pattern:

[AvaloniaFact]
public async Task Page_rename_while_storage_is_paused_faults_the_command()
{
    await using var app = DesktopHarness.Create(s => s.AddSingleton(new StorageHealthOptions { RetryInterval = TimeSpan.FromHours(1) }));
    var window = app.ShowWindow(1100, 720);
    await NetworkScenario.SeedAsync(app);
    var list = app.ShellPage.Conversations;
    await UiWait.UntilAsync(() => list.Items.Count == 4);
    app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(list.Items[3].Id)));
    var page = (ConversationViewModel)app.Workbench.CurrentPage!;
    await page.Loaded;
    app.Services.GetRequiredService<StorageHealth>().Report(new StorageWriteException(StorageFailureKind.Full, "disk full", null));
    await UiWait.UntilAsync(() => page.IsStorageBlocked);
    window.UpdateLayout();
    UiInput.Click(window, Find<Button>(window, "RenameButton"));
    await UiWait.UntilAsync(() => page.IsRenaming);
    window.UpdateLayout();
    var save = Find<Button>(window, "SaveRenameButton");
    page.RenameText = "paused rename";
    var unhandled = new List<Exception>();
    void OnUnhandled(object? s, DispatcherUnhandledExceptionEventArgs e) { unhandled.Add(e.Exception); e.Handled = true; }
    Dispatcher.UIThread.UnhandledException += OnUnhandled;
    Exception? escaped = null;
    try
    { … (cut; full text in the workflow journal)

</details>

### ui-vm-2. Duplicate pages of one conversation in the back stack each hold their own copy of the shared draft, so going Back can hide or overwrite an unsent draft

- Final: **high** (confirmed); auditor said high
- Where: `src/Mindweave.Desktop/Navigation/Workbench.cs:340`; effort M

**Evidence.** PushCore (lines 337-353) only skips a push when the top of the stack has the same route (`stack[^1].Route == route`). Opening A, then B, then A again from the sidebar (ConversationListViewModel.Open, line 129) yields [Home, A1, B, A2]. The draft is stored per (conversation, slot) in ReadingStateService, so A1 and A2 share one draft, but ConversationViewModel reads it exactly once in LoadFirstAsync (line 333) and only writes it afterwards via OnComposerChanged (lines 343-349). A grep for GetDraftAsync shows this single caller, and nothing re-reads the draft when a page is shown again (the IsShown setter only starts the elapsed-time tick).

**Failure scenario.** Draft appears lost, then is overwritten: open conversation A (A1 loads an empty draft), click B, click A again (A2). Type a long question in A2 without sending. Press Back twice. A1 is shown with an empty composer although the stored draft is the long question. Typing one character calls SetDraft with A1's stale text and the stored draft is gone. Duplicate send: with a draft already in A1, repeat the path, send it from A2 (the draft is cleared), go Back twice. A1 still shows the already-sent question, and Enter sends it again.

**Recommendation.** Either keep one live page per conversation per slot (on push, move the existing instance to the top or drop the older one), or have the composer re-read the draft when its page is shown again, or have ReadingStateService raise a draft-changed notification that pages of the same (conversation, slot) follow. Add a test for A → B → A → type → Back → Back.

**Skeptic: confirmed, medium.** I could not refute the finding; the code does what it claims. This is from reading the code only: nothing was run (read-only audit).

**What the code does**
- `PushCore` only skips a push when the top of the stack has the same route, so Home → A → B → A leaves two live `ConversationViewModel` instances for A in one slot. The sidebar `Open` goes through `Navigate` → `PushCore` with a plain `ConversationRoute(item.Id)`.
- Each page builds its own `Composer`. It reads the draft once in `LoadFirstAsync`, and afterwards only writes it through `OnComposerChanged` → `SetDraft`.
- `GetDraftAsync` has exactly one caller in `src`. The `IsShown` setter only calls `UpdateTicking`. `ReadingStateService` has no draft-changed event, and `SetDraft` is last-writer-wins on the `(conversation, slot)` key.
- `GoBack` pops and disposes the top page and reveals the older instance without reloading it.
- `Composer.SendAsync` clears only its own `Text`, so the older page keeps the already-sent text, and `CanSend` is true once no answer is running.

**No guard or design decision covers it**
- The 2026-11-01 architecture fix (TRACK.md line 111) moved "latest input" out of pages for summary drafts only (`SummaryDrafts` with generation and revision). The conversation composer kept the read-once model.
- `docs/design/s2-branching.md` lines 49 and 66 only say the draft is keyed by (conversation, slot) and updated as the user types; duplicate pages are not addressed.
- I found no test for A → B → A → Back → Back by grep; I did not open `NavigationTests.cs` or `DraftLossTests.cs`.

**A more common trigger … (cut; full text in the workflow journal)

*Corrections to the finding.* - The stored draft is not lost by Back alone. It is overwritten only when the user types in the stale page; until then it survives in `ReadingStateService` and the database and reappears on restart or on a freshly created page.
- Clicking A in the sidebar while the stale A page is on top does not help: `PushCore` skips the push because the route matches.
- The duplicate-send case needs the user to press Enter on a page whose message list should already show the sent question, so it is less likely than the finding implies.
- The finding misses a more common trigger: parent → branch → return to source also puts two pages of the parent in the stack, because `ReturnToSource` always opens a new page and its route (with `SourceOf`) differs from the plain one.
- Severity is better rated medium than high.

**Reproduction attempt: confirmed, high.** Both claimed failure scenarios reproduce deterministically at the ViewModel level against track/claude-auto @ 1ba95c9 (two headless Avalonia tests, both fail on logic, not timing).

Mechanism, as the finding states: Workbench.PushCore only de-duplicates against the top of the stack, so A -> B -> A leaves two live ConversationViewModel instances for the same conversation in one slot. Each reads the shared (conversation, slot) draft once in LoadFirstAsync and afterwards only writes it. GoBack disposes the leaving page and re-shows the older instance without any re-read.

Scenario 1 (draft hidden, then overwritten): after typing a long draft in A2 and pressing Back twice, A1's composer was empty while the stored draft was the long text. One keystroke in A1 replaced the stored draft with just "x". The long draft is gone from ReadingStateService's cache and will be persisted as "x".

Scenario 2 (duplicate send): with a draft loaded by both A1 and A2, sending from A2 cleared A2 and the stored draft, but after Back twice A1 still showed the sent question with SendCommand enabled. Executing it sent the question a second time (6 messages instead of 4), which also means a second, paid model request.

Severity stays high: silent loss of user-typed content, which requirements §9.3 / architecture §12.1 treat as core content, plus a duplicate send, reached by ordinary sidebar navigation and the Back button. It is not critical because it needs the specific A -> B -> A -> Back -> Back path, and in scenario 2 the stale text is visible before the user presses Enter.

TRACK.md does not list t … (cut; full text in the workflow journal)

*Corrections to the finding.* No substantive errors; line references and mechanism match the code.

- The finding's stack "[Home, A1, B, A2]" is right in spirit. In scenario 2 my repro reached the same state via A1 -> (open B) -> (open A), giving [Home, A1, B2, A2]; the outcome is identical.
- "Draft appears lost, then is overwritten" is slightly understated: the overwrite happens in ReadingStateService's in-memory cache on the first keystroke and is then persisted by the scheduled save, so the loss becomes durable, not just visual.
- "Enter sends it again" was confirmed through SendCommand (CanExecute true, then executed), not through a real key press in the view.
- The same exposure exists for any route that re-pushes an already-stacked conversation (e.g. ReturnToSource navigation, which by design always opens a new page), not only sidebar clicks. I did not test those paths separately.

<details><summary>Reproduction</summary>

Temporary file tests/Mindweave.Desktop.Tests/ReproDuplicateDraftTests.cs (deleted afterwards). Run with:
dotnet test tests/Mindweave.Desktop.Tests --filter "FullyQualifiedName~ReproDuplicateDraftTests"

```csharp
public class ReproDuplicateDraftTests
{
    private static async Task<(ConversationViewModel A1, ConversationViewModel B)> OpenAThenBAsync(DesktopHarness app)
    {
        var a1 = await app.StartWithConversationAsync(waitForTitle: false);
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "第二个对话";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var b = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await b.Loaded;
        await UiWait.UntilAsync(() => b.Messages.Count == 2 && b.Messages[1].CanBranch);
        Assert.NotEqual(a1.Id, b.Id);
        return (a1, b);
    }

    private static async Task<ConversationViewModel> OpenAsync(DesktopHarness app, ConversationViewModel existing)
    {
        // What ConversationListViewModel.Open does for a sidebar click.
        app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(existing.Id)));
        var again = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        Assert.NotSame(existing, again);
        Assert.Equal(existing.Id, again.Id);
        await again.Loaded;
        return again;
    } … (cut; full text in the workflow journal)
```

</details>

### ui-vm-3. App-global settings are loaded once into per-workspace Settings pages, so another workspace's Settings page shows and can re-save stale appearance and model values

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/Pages/SectionPages.cs:70`; effort M

**Evidence.** PageFactory.cs line 53 creates `new SettingsHomeViewModel(environment, new ModelSettingsViewModel(models), ...)` per call, and Workbench.EnsureHome (lines 328-335) creates one section home per workspace runtime that is never popped. SettingsHomeViewModel loads appearance and the three news toggles once in its constructor (`Loaded = LoadAsync()`, line 70); ModelSettingsViewModel does the same (ModelSettingsViewModel.cs line 31). SettingsHomeView has no refresh on attach (unlike StorageDetailsView and UsageStatisticsView), and ModelSettingsViewModel does not subscribe to IModelSettings.Changed, although ConversationService does.

**Failure scenario.** Open Settings in workspace A. Switch to workspace B, open Settings, choose Dark and change the model from model-1 to model-2 on the same service, then save. Switch back to A and open Settings: the appearance picker still shows the old choice although the app is dark, and the model form shows model-1. Editing only the context window there and pressing Save writes model-1 back, silently reverting the active model. If the host was changed in B, A's key placeholder still says the saved key applies, and Save fails with a missing-key error.

**Recommendation.** Make the settings state session-wide (one shared ModelSettingsViewModel and settings state, or re-read on attach as the Statistics and Storage pages do), and have ModelSettingsViewModel reload on IModelSettings.Changed when the form is not dirty.

**Skeptic: confirmed, medium.** I could not refute it; the code does what the finding says and no guard, refresh, or documented decision covers it. Not reproduced by running (read-only audit, no builds allowed); confirmed by reading the code.

**Why the scenario holds**
- Each workspace gets its own Settings page instance. `Workbench` keeps one `WorkspaceRuntime` per visited workspace, and `EnsureHome` creates the section home only when the stack is empty. `Replace` and the forget loop skip index 0, so the home lives until the workspace is deleted or `ReleaseAll` runs on an in-app restore.
- Each instance gets a fresh `ModelSettingsViewModel`, built by `PageFactory` on every `SettingsHomeRoute` creation.
- Both view models load only in their constructors. `ModelSettingsViewModel.LoadAsync` runs again only after its own Save.
- Nothing refreshes them afterwards. `SettingsHomeView.OnAttachedToVisualTree` only registers a pointer handler, and nothing in `Mindweave.Desktop` subscribes to a model-settings change event.
- The settings are app-global. `ModelSettingsService` reads and writes a single active configuration via `store.GetActiveAsync` / `SaveActiveAsync`; appearance and the news toggles live in the preference store (`app_state`), not per workspace.
- `SwitchWorkspace` restores the section the workspace was left on, so returning to A shows the same stale instance.

**Consequences verified**
- **Model revert:** A's form still holds model-1 after B saved model-2. Save in A sends the form's Name, BaseUrl, Model and ContextWindow, so the active model goes back to model-1. Because `existing.Model != model` … (cut; full text in the workflow journal)

*Corrections to the finding.* - `PageFactory` line is 50, not 53.
- `Changed` is declared on `ModelSettingsService`, not on the `IModelSettings` interface (`IModelSettings.cs` has only Get/Save/Discover/Test). The recommended "reload on `IModelSettings.Changed`" needs the event added to the interface or a different notification path.
- "Can re-save stale appearance" is overstated. Appearance and the three toggles are written only when the user changes them on that page. What actually happens is a stale display, plus the shown-but-not-actual option being impossible to select directly.
- "Silently reverting the active model" is partly overstated. The form visibly shows model-1 when the user presses Save; the surprise is that the form does not reflect the current state. Save also overwrites name, address and context window with the stale values and resets declared capabilities — the finding did not mention this.
- The m … (cut; full text in the workflow journal)

### ui-vm-4. The Workspaces page never recounts when shown again, because the 'shown once' flag lives in a view that is rebuilt on every navigation

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Desktop/Views/Pages/WorkspacesView.axaml.cs:54`; effort S

**Evidence.** WorkspacesView keeps `private bool _shownOnce;` and OnAttachedToVisualTree (lines 51-60) calls `_page?.Refresh()` only when it is already true. The page host is a plain ContentControl (ShellView.axaml line 226) and ViewLocator implements IDataTemplate, not IRecyclingDataTemplate, so each time a page becomes current a new view is built. UsageStatisticsUiTests lines 60-70 confirm this by re-querying for a new UsageStatisticsView after returning. A fresh WorkspacesView therefore always has _shownOnce == false at its only attach, and WorkspacesViewModel.Refresh (lines 106-112) is effectively unreachable in the app. UsageStatisticsViewModel keeps the equivalent flag (_shown) in the view model, which works. No test covers a recount on return in WorkspacesPageTests.

**Failure scenario.** Open Settings › Workspaces while a new workspace is empty: its row shows zero conversations and HasContent becomes false. Go to Explore, hold several conversations, then click Settings again (the Workspaces page is still on top of that stack). The row still shows the old counts. Starting a rename leaves the suggest-names button disabled with the empty-workspace tooltip, because CanSuggest (line 703) requires HasContent != false. This persists until the page is popped and re-created.

**Recommendation.** Move the first-show flag into WorkspacesViewModel as UsageStatisticsViewModel does (the view calls Refresh on every attach and the view model skips the first), and add a UI test that leaves the section and returns.

**Skeptic: confirmed, medium.** I could not refute it. The cited code exists and reads as claimed, and I found no other path that recounts existing rows.

- **View flag is per-view-instance.** `WorkspacesView._shownOnce` is a field of the view; `Watch` resets it to false on every DataContext change, and `OnAttachedToVisualTree` calls `_page?.Refresh()` only if it was already true.
- **A new view is built on every return.** `PageHost` is a plain `ContentControl` bound to `CurrentPage`. `ViewLocator` implements `IDataTemplate` only (not `IRecyclingDataTemplate`), and its registry entry is `() => new WorkspacesView()`. A fresh view gets its DataContext, then attaches once with `_shownOnce == false`, so `Refresh` is never called.
- **The view model survives the round trip.** `UsageStatisticsUiTests` asserts `Assert.Same(page, app.Workbench.CurrentPage)` after Explore → Settings, then re-queries the visual tree for the view. So the same view model is paired with a new view, which is exactly the case the flag misses.
- **No other recount path.** `RefreshCountsAsync` is called only from the constructor and `Refresh()`. `OnWorkspacesChanged` counts only newly added rows. `OnDirectoryChanged` only updates `IsCurrent`. There is no subscription to conversation or summary changes.
- **The sibling page does it correctly.** `UsageStatisticsViewModel` keeps `_shown` in the view model and its view calls `Refresh()` on every attach. The Workspaces page predates it (commit f4d709e vs 420dc90) and was not brought in line.
- **Consequence is real.** `SetCounts` sets `HasContent = conversations + summaries > 0`. `CanSuggest` … (cut; full text in the workflow journal)

*Corrections to the finding.* - "Effectively unreachable" is accurate for the running app; `Refresh()` is still public and callable from tests.
- The stale state ends not only when the page is popped and re-created. A successful count for a newly added row does not help existing rows, but anything that re-creates the view model (popping the page, and possibly a workspace switch if that resets the stacks — not checked) clears it.
- The cited test proves the view model is reused and the statistics page re-reads. It does not by itself prove a new view instance is built (`.Single()` would also pass with a reused view); that part rests on `ViewLocator` being a non-recycling `IDataTemplate`.
- Severity is at the low end of medium: cosmetic stale counts plus one wrongly disabled convenience feature, with a simple workaround.

### ui-vm-5. 'Go to settings' links land on whichever Settings sub-page was last open, and the Workspaces page's link pushes a second Settings home onto the stack

- Final: **low** (confirmed); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/Conversations/ConversationViewModel.cs:306`; effort S

**Evidence.** ConversationViewModel.OpenSettings (line 306) and NewConversationViewModel.OpenSettings (line 37) call `Navigate(new NavigationRequest(MainSection.Settings))` with no content. Workbench.Navigate (lines 106-118) then only switches the section and shows the top of the Settings stack. WorkspacesViewModel.OpenModelSettings (line 340) instead navigates with SettingsHomeRoute.Instance; because PushCore only compares against the top page (WorkspacesRoute), a second SettingsHomeViewModel is pushed, giving [SettingsHome, Workspaces, SettingsHome]. A third approach is UsageStatisticsViewModel.GoToModelSettings (line 247), which calls GoBack().

**Failure scenario.** (1) The user last left Settings on the Statistics or Storage page. In a conversation the composer says no model is configured (or the context is too small) and offers the settings link; clicking it shows the Statistics or Storage page, not the model form. (2) On Settings › Workspaces a name suggestion fails with 'not configured'; clicking the settings link shows a Settings page with a Back button that returns to Workspaces, while the original home with its own separate form state stays underneath. ShellViewModel.ManageWorkspaces can likewise stack several WorkspacesViewModel instances, e.g. [Home, Workspaces, Home, Workspaces].

**Recommendation.** Add one 'show model settings' navigation that pops the Settings stack to its home (or a NavigationRequest flag meaning 'section home'), use it from all four call sites, and make PushCore reuse an existing singleton-route page (Settings home, Workspaces, Statistics, Storage) instead of creating a duplicate.

**Skeptic: confirmed, low.** Every cited line exists and behaves as claimed; I found no guard, test or design decision that prevents either scenario. I did not run anything (read-only audit), so this rests on reading the code.

Scenario 1 (link lands on the last-open Settings sub-page) holds:
- Both composer links navigate with a section only and no content.
- `Workbench.Navigate` then sets the section, calls `EnsureHome` (which only creates a home when the stack is empty) and pushes nothing, so the page shown is whatever is on top of the Settings stack.
- The Settings stack keeps its sub-pages when the user leaves the section: Statistics, Storage and Workspaces are pushed onto it from the Settings home and nothing pops them on a section switch.
- The only test of this link starts from a fresh Settings stack, so the stale-stack case is uncovered.

Scenario 2 (duplicate Settings home) holds:
- `WorkspacesViewModel.OpenModelSettings` navigates with `SettingsHomeRoute.Instance`.
- `PushCore` only skips the push when the top of the stack has the same route. With Workspaces on top it builds a second `SettingsHomeViewModel`, giving [Home, Workspaces, Home].
- `PageFactory` gives that second home its own new `ModelSettingsViewModel`, so its form state is separate from the original home's.
- From the duplicate home, either `ShellViewModel.ManageWorkspaces` or the home's own `ShowWorkspaces` pushes another Workspaces page, so [Home, Workspaces, Home, Workspaces] is reachable. Growth is bounded only by `MaxHistory` = 50.
- The existing test for this link only asserts the current page type, not the stack shape. … (cut; full text in the workflow journal)

*Corrections to the finding.* - Severity is overstated: low, not medium. Both scenarios are navigation and back-history flaws with no data impact.
- [Home, Workspaces, Home, Workspaces] needs the duplicate home from the Workspaces link to exist first. `ManageWorkspaces` alone does not stack duplicates when Workspaces is already on top. The duplicate home's own "管理工作区" entry (`ShowWorkspaces`) reaches the same state, not only the shell menu.
- `UsageStatisticsViewModel.GoToModelSettings` using `GoBack()` is inconsistent but works today, because Statistics is only ever pushed from a Settings home, so Back always lands on one.
- The "separate form state" in the duplicate home is real (a new `ModelSettingsViewModel` per home), but I did not verify any user-visible stale-value consequence.

### ui-vm-6. NetworkCanvas keeps panning after capture is lost mid-pan, and a second mouse button during a node drag corrupts and ends the drag

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/Views/Pages/NetworkCanvas.cs:558`; effort S

**Evidence.** OnPointerCaptureLost (lines 558-568) resets only the node-drag state (`if (_pressed is not null) {...}`); `_panFrom` is cleared only in OnPointerReleased (line 550). OnPointerMoved (lines 493-510) pans whenever `_panFrom` is set, without checking that a button is still down. Separately, OnPointerPressed (lines 448-473) has no guard for a drag already in progress: a second press hit-tests the dragged node under the pointer, and since IsLeftButtonPressed is still true it reassigns `_pressed`, `_pressedAt` and `_grab = pointer − node.X/Y` using the node's layout position rather than its dragged position. OnPointerReleased (lines 526-542) commits the move on any button's release.

**Failure scenario.** (a) The user starts panning the empty canvas and capture is taken away (Alt/Cmd-Tab, or an overlay such as the exit or layout prompt opening). The release is never delivered, so on return every hover move pans the viewport with no button pressed until the next click. (b) While left-dragging a node the user presses the right or middle button: the grab offset is recomputed from the layout position so the node jumps back toward its origin, and releasing that second button commits the wrong position and marks the layout unsaved while the left button is still held.

**Recommendation.** Clear `_panFrom` and `_dragged` in OnPointerCaptureLost. Ignore presses while `_pressed` or `_panFrom` is set, and end a drag or pan only on release of the button that started it (check e.InitialPressMouseButton). Add headless tests for capture loss during a pan and for a second button during a drag.

**Reproduction attempt: partially confirmed, low.** Part (a), the pan that survives capture loss, is real; part (b), the second mouse button, cannot happen with a mouse on the pinned Avalonia version.

**(a) Confirmed by code reading, not reproduced.**
- `OnPointerCaptureLost` resets only `_pressed`/`Dragging`. `_panFrom` is cleared in exactly one place, the pan branch of `OnPointerReleased`.
- `OnPointerMoved` pans whenever `_panFrom` is set and never checks button state.
- So if capture is lost mid-pan and the release never reaches the canvas, later hover moves keep rewriting `Session.Viewport` relative to the stale `_panStart`.
- Nothing in the design docs makes this intended. The doc comment on `OnPointerCaptureLost` itself names "a window switch, a dialog" as realistic triggers, and `docs/design/s7-ui-polish.md:247` records a capture-loss test for the node drag only. The pan path was simply missed.
- Recovery is slightly worse than claimed: a click on a node does not clear it, because the `_pressed` branch of `OnPointerReleased` returns before `_panFrom = null`. Only a click on empty canvas does.
- Limits on the trigger: if the button comes up while the pointer is still over the canvas, Avalonia routes the release by hit test and the state clears. The stuck state needs the release to land elsewhere, such as another window after Alt-Tab on Windows, or an overlay that now covers the canvas.
- Impact is viewport-only. No layout change, no unsaved state, no data touched.

**(b) Refuted for mouse input.**
- Decompiled `Avalonia.Input.MouseDevice.ProcessRawEvent` from Avalonia.Base 12.1.4 (the version pinned in `Directory.Pac … (cut; full text in the workflow journal)

*Corrections to the finding.* - **Scenario (b) is wrong for mouse input.** Avalonia 12.1.4 delivers a second mouse button press as PointerMoved and raises PointerReleased only when the last button goes up. The node does not jump, and no wrong position is committed. Drop the claims that "a second mouse button during a node drag corrupts and ends the drag" and that `OnPointerReleased` "commits the move on any button's release".
- **The `e.InitialPressMouseButton` recommendation is unnecessary for mouse.** A guard against re-entrant presses would only be defensive hardening for multi-touch.
- **Scenario (a) holds but is narrower than stated.** It needs the release to be delivered away from the canvas. If the button is released over the canvas after capture loss, the state clears normally. Whether macOS Cmd-Tab triggers it is unverified; Windows Alt-Tab with the button held is the plausible case.
- **Scenario (a) recover … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

No runtime reproduction: building and running tests in the worktree is prohibited for this audit. Verification was static — a source read plus decompiling Avalonia.Base 12.1.4 `MouseDevice` with ilspycmd.

</details>

### ui-vm-7. Most pages have no load-failure or loading state: a failed read leaves the network page saying 'no summaries' and a conversation page blank, with no retry

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/Pages/NetworkHomeViewModel.cs:267`; effort M

**Evidence.** NetworkHomeViewModel.LoadAsync (lines 267-291), ConversationViewModel.LoadAsync (377-423), SummaryViewModel.LoadAsync (510-550) and ArchivedViewModel.LoadAsync have no try/catch and no error property; only UsageStatisticsViewModel has LoadError and Retry. Reloads are started as `_ = _load.Request()` (e.g. NetworkHomeViewModel line 41), so a fault becomes an unobserved task. SerialRefresh.RunAsync (IUiDispatcher.cs lines 66-82) exits its loop on an exception, dropping a queued `_again` re-run. NetworkHomeViewModel.IsEmpty is `Items.Count == 0` (line 59), true before the first load finishes, and NetworkHomeView.axaml line 70 shows the empty-state text whenever IsEmpty. ConversationView.RestoreAsync awaits page.Loaded (line 150) inside a discarded task; if it faults, `_restoring` (set false only at line 171) stays true, so the ScrollChanged handler returns early and scroll-following and reading-position capture stay off for that view.

**Failure scenario.** A read fails once (I/O error, or a reload racing a database close). The network page permanently shows the 'no summaries' text and hides its toolbar although the summaries exist; nothing indicates an error, and it recovers only on the next unrelated summary change. A conversation page stays on the placeholder title with no messages. Even without a failure, a workspace with many summaries briefly shows the empty text on every first open because there is no loading state.

**Recommendation.** Give PageViewModel a common IsLoading/LoadError/Retry contract and wrap page loads in it. Make SerialRefresh keep its queued re-run when a refresh throws, and report rather than discard the fault. Distinguish 'not loaded yet' from 'empty' on the network page.

**Skeptic: partially confirmed, low.** Every cited code fact checks out, and I found no guard, design decision or TRACK.md entry that makes it intended. I ran nothing; this is from reading the code only.

What holds:
- The four page LoadAsync methods have no try/catch, and exceptions propagate from the store through the services (ConversationService.ListAsync/LoadAsync and NetworkSession.LoadAsync do not catch).
- LoadError exists only in UsageStatisticsViewModel and its view.
- `Loaded` is awaited in exactly one place in src (ConversationView.RestoreAsync); for the other pages a faulted first load is never observed.
- SerialRefresh.RunAsync leaves its do/while on an exception with only `_busy = false` in the finally, so a queued `_again` re-run is dropped.
- NetworkHomeViewModel.IsEmpty is `Items.Count == 0` and is true from construction; the view shows the empty text on it and hides the toolbar, detail panel and legend.
- ConversationView sets `_restoring = false` only at the end of RestoreAsync, which is started as a discarded task, so a faulted `page.Loaded` leaves scroll handling off for that view. The conversation page would also keep its placeholder title and an empty draft, because LoadFirstAsync fails before or at the load.

Why the severity is lower than claimed:
- The trigger is a failed local SQLite read, which is rare. The auditor offers "I/O error, or a reload racing a database close" without showing that either actually happens; I did not find evidence either way.
- The wrong 'no summaries' state only arises when the first load of a page instance fails. A failed later reload leaves the previous It … (cut; full text in the workflow journal)

*Corrections to the finding.* - ConversationViewModel.cs is in ViewModels/Conversations and ConversationView.axaml.cs is in Views/Conversations, not the Pages folders.
- "Permanently shows 'no summaries'" applies only when the first load of that page instance fails, and it clears on the next summary or visibility change or on reopening the page. A failed later reload leaves stale content, not the empty text.
- The fault is not silently discarded forever: Program.cs:37 logs unobserved task exceptions as critical, though only when the task is finalized, and nothing is shown to the user.
- No concrete path to a read failure was demonstrated; "reload racing a database close" is speculation.
- Severity should be low, not medium: rare trigger, self-healing on the next change or reopen, no data loss.

### ui-vm-8. A node moved or arranged while NetworkSession.LoadAsync is in flight is silently reverted, yet the layout stays marked unsaved

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Network/NetworkSession.cs:111`; effort S

**Evidence.** LoadAsync (lines 74-114) copies `_positions` into `reference` before its awaits (ProjectAsync, GetLayoutAsync, then PlaceAsync per new node) and ends with `_positions = reference;` at line 111. Move (lines 132-144) and Arrange replace `_positions` and increment `_revision` synchronously on the UI thread, which can run between those awaits. NetworkHomeViewModel reloads on every ISummaries.Changed and IConversations.VisibilityChanged event from any workspace (lines 42-43, 241-256).

**Failure scenario.** The user releases a dragged node (MoveNode → Session.Move) just as a reload triggered by a finishing summary is between its awaits. LoadAsync then assigns the pre-move copy: the node snaps back to its old place, but `_revision` was already incremented, so the page still says the layout is unsaved, and Save writes the old positions. The window is a few to tens of milliseconds per reload, so this is rare.

**Recommendation.** Capture `_revision` at the start of LoadAsync and, if it changed by the end, merge only the newly placed nodes into the current `_positions` instead of replacing it (or re-run the load). Filter Changed events to the session's own workspace.

### ui-vm-9. Every Statistics reload rebuilds the model rows, discarding an open price editor and its typed values; the view also accumulates subscriptions to replaced rows

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/ViewModels/Pages/UsageStatisticsViewModel.cs:377`; effort S

**Evidence.** Apply does `Models.Clear()` followed by `Models.Add(new ModelUsageRowViewModel(...))` for every model (lines 377-381). IsEditing, InputPrice, OutputPrice and PriceError live on the row. Reload is triggered by OnAllWorkspacesChanged, OnPeriodChanged, Retry and every view re-attach (Refresh). UsageStatisticsView._watched (UsageStatisticsView.axaml.cs lines 19 and 72-79) adds each row whose price button was clicked and removes them only on detach, so replaced rows stay referenced and subscribed. LoadAsync also reads prices (lines 313-319) before Apply, so a price save that starts between those awaits updates a row that Apply then replaces with the older price.

**Failure scenario.** The user opens a model's price editor and types the input price, then switches between daily and monthly to check a figure (or leaves the page and returns). The editor closes and the typed prices are gone with no message; keyboard focus, which was inside the removed editor, is dropped.

**Recommendation.** Reconcile Models by ModelKey (update counts and share in place, add and remove only what changed), as the conversation and workspace lists already do, or carry editing state over to the new row. Unsubscribe from rows when they leave the collection.

### ui-vm-10. Composer.SendAsync clears whatever is in the box when the send completes, including text typed while the send was in flight

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/ViewModels/Conversations/Composer.cs:56`; effort S

**Evidence.** SendAsync (lines 55-67): `var outcome = await _send(Text); ... if (outcome.Accepted) { Text = string.Empty; }`. The await covers a synchronous-durability SQLite commit (TRACK.md records WAL with synchronous=FULL and slow disk syncs on Windows) and, for a new conversation, navigation. The text box stays editable during the await; only the send command is disabled. The same pattern is in ConversationViewModel.CommitRenameAsync, which sets IsRenaming = false before the await.

**Failure scenario.** On a slow disk the commit takes a few hundred milliseconds. The user presses Enter and immediately starts typing the next question; when the commit returns, Text is set to empty and the characters typed meanwhile are lost, and the stored draft is cleared with them.

**Recommendation.** Capture the sent text, clear the box before awaiting and restore it on refusal; or after the await clear only if Text still equals the sent text.

