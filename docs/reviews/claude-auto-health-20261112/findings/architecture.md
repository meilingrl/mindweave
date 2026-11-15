# Architecture conformance — grade B

The four-project dependency direction in docs/architecture.md §2 is genuinely respected in source, not just claimed: Core has zero package/project references, Application references only Core + Logging.Abstractions and contains no file/HTTP/SQL code, no Avalonia type appears in any ViewModels/Navigation/Network file, and Desktop names Infrastructure only in Program.cs, CompositionRoot.cs and AppHost.cs. DI uses ValidateOnBuild/ValidateScopes and every test harness goes through the production registration methods. What keeps this from an A: (1) the architecture tests enforce a narrower rule than their names suggest (constructor parameters of three namespaces, substring scans), so the real drift that exists today passes them - view models holding storage ports, synchronous unguarded file I/O in a page constructor, concrete services in Desktop.Services; (2) business orchestration for backup restore and exit lives in Desktop view models; (3) roughly 25 optional constructor parameters on container-built singletons make ValidateOnBuild blind to missing registrations; (4) the in-app session swap has no failure path and leaks per-session resources. None of these is a data-loss bug I could demonstrate; they are structural gaps that will let regressions through.

**Strengths**

- Layering is clean in the actual source: src/Mindweave.Core/Mindweave.Core.csproj has no references at all; src/Mindweave.Application/Mindweave.Application.csproj references only Core and Microsoft.Extensions.Logging.Abstractions; a grep of Application and Core for sqlite/SELECT/HttpClient/File./Directory./Path./Environment. returns no real hits; `using` lines in Core are only System.Globalization/Text/Security.Cryptography.
- No UI-framework leakage into view models: grep for Avalonia|Dispatcher.|LiveMarkdown across src/Mindweave.Desktop/ViewModels, Navigation and Network returns nothing; UI-thread marshalling goes through IUiDispatcher and theming through IThemeApplier (src/Mindweave.Desktop/Services).
- Desktop mentions Mindweave.Infrastructure in exactly three files (Program.cs, CompositionRoot.cs, AppHost.cs), matching the allow-list in tests/Mindweave.Architecture.Tests/LayeringTests.cs; all Sqlite*Store classes and credential stores are `internal sealed`, so Desktop cannot bind to them even by accident.
- Composition is centralized and validated: CompositionRoot.Build ends with BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }) (src/Mindweave.Desktop/CompositionRoot.cs:76), and the test harnesses (tests/Mindweave.Desktop.Tests/Support/DesktopHarness.cs:138, tests/Mindweave.Infrastructure.Tests/Chat/ChatHarness.cs:342-343, tests/Mindweave.TestHost/Program.cs:131-132) build through the same AddMindweaveInfrastructure/AddMindweaveApplication/CompositionRoot.Build calls, using overrides + TryAdd rather than a parallel wiring.
- Each interface alias resolves to the same singleton as the concrete service (ApplicationServices.cs:62-82 use sp.GetRequiredService<Concrete>()), so there is no accidental double instance of stateful services such as GenerationCoordinator or StorageHealth.
- Process-lifetime vs session-lifetime is modelled explicitly (AppHost/AppSession in src/Mindweave.Desktop/AppHost.cs): the database is closed only after the UI loop returns, with the reason documented and a deterministic release order (generation stop -> storage health stop -> retry kept writes -> flush -> close -> dispose).
- The provider boundary is real: ChatRequest/ChatStreamEvent (src/Mindweave.Application/Ai/ChatContracts.cs) carry no SDK or HTTP types, and SeamTests forbids vendor names in Core/Application.
- ViewLocator is an explicit registry (src/Mindweave.Desktop/ViewLocator.cs) rather than name-based reflection, and navigation is expressed as typed routes with IRootNavigator/IMenuNavigator/IContentNavigator contracts, matching architecture §3.4.

**Not examined**

- Whether the SQLite store implementations contain business rules that architecture assigns to Core/Application (store interfaces are use-case shaped: FinishAnswerAsync, SetAutoTitleAsync, AdoptAsync, DeleteAsync(scope)); I read the interface surfaces, not the SQL bodies.
- The single-writer + read-connection model and migration ordering inside SqliteDatabase/SqliteWriteQueue (architecture §12 / MW-C19) - left to the data/storage dimension.
- Runtime-state ownership table in architecture §2.2 row by row (message drafts, reading positions, ContentStack, network layout revision) - only spot-checked SummaryDrafts and ReadingStateService registration with StorageHealth.
- View code-behind thickness in detail (ShellView.axaml.cs 353 lines, AnswerView.axaml.cs 435 lines, NetworkCanvas.cs 736 lines); I only confirmed they do not import Application services beyond value types.
- XAML files (compiled bindings, any code in .axaml referencing Application types beyond ShellView's Core.Workspaces xmlns).
- Whether the Architecture.Tests project actually passed in the baseline run: /private/tmp/claude-501/-Users-meilingluo-Developer-Projects-mindweave-worktrees-claude-auto/741bda69-6233-404f-b4a0-8bc89d0ed5bc/scratchpad/test.txt only contained the Desktop.Tests result (381 passed, 58 skipped) when I looked; build.txt showed 0 warnings / 0 errors.
- Internal thread-safety of the singletons (GenerationCoordinator, WorkspaceNamePreparer, StorageHealth) - concurrency dimension.
- Namespace-cycle analysis was done from `using` directives only; fully-qualified references and parent-namespace implicit visibility were not counted, so the cycle list is a lower bound.
- The track design docs under docs/design/ beyond a grep for A6; docs/reviews/claude-auto-architecture-20261101 proposal was not read in full.

### architecture-1. Storage page does synchronous, unguarded file I/O in a view-model constructor on the UI thread

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/Pages/StorageDetailsViewModel.cs:33`; effort M

**Evidence.** The constructor calls Refresh() (line 33), which calls `_files.ListBackups()` and `_files.ListLeftovers()` (lines 62, 69). IBackupFiles is a synchronous storage port (src/Mindweave.Application/Startup/DatabaseOpenResult.cs:151-164) implemented by SqliteDatabase -> SqliteBackupFiles.List (src/Mindweave.Infrastructure/Sqlite/SqliteBackupFiles.cs:28-52), which does Directory.EnumerateFiles, `int.Parse(version)`, `DateTimeOffset.ParseExact(time)` and `new FileInfo(file).Length` per file with no try/catch. The name regex (SqliteBackupFiles.cs:399) only requires `\d+` and `\d{8}T\d{9}Z`, so it accepts strings that are not valid ints/dates. Page creation is not guarded: Workbench.cs:333/345 call `_pages.Create(...)` with no try/catch, and there is no Dispatcher.UIThread.UnhandledException handler (only AppDomain/TaskScheduler handlers in Program.cs:36-37). The same view also re-calls Refresh() from code-behind (Views/Pages/StorageDetailsView.axaml.cs:17). Architecture §2.1 requires "网络和存储不阻塞 UI；取消、失败及保存结果有可观察行为".

**Failure scenario.** (a) A backup file disappears between EnumerateFiles and FileInfo.Length (the delete dialog's Task.Run in BackupDialogs.cs:330 runs concurrently with a Refresh triggered by re-showing the page, or an external cleaner/AV removes it) -> FileNotFoundException thrown inside a RelayCommand/navigation on the UI thread -> process terminates. (b) A file such as `mindweave-v10-20261399T000000000Z.db` or one with an 11-digit version in the backup folder -> FormatException/OverflowException every time Settings > Storage is opened, and also from StartupCoordinator.cs:67 (`database.ListBackups()`) exactly when the recovery page is needed. (c) A backup directory with many large files on a slow or network-redirected profile freezes the window while the page opens.

**Recommendation.** Put a small application use case (e.g. IBackups in Application) between the pages and the port: async listing off the UI thread, per-file exception handling that skips unreadable entries and reports them, TryParse for version/time. Have the page load in an async InitializeAsync with a visible loading/failed state instead of in the constructor.

**Skeptic: partially confirmed, low.** The cited code exists and says what the finding claims, but the failure scenarios are narrow, so medium is overstated.

What holds:
- The constructor calls Refresh(), which calls ListBackups() and ListLeftovers() synchronously. Refresh() is also called from the view's OnAttachedToVisualTree and from the prompts' Changed event. None of these call sites has a try/catch.
- SqliteBackupFiles.List() does int.Parse, DateTimeOffset.ParseExact and new FileInfo(file).Length per file with no exception handling. ListLeftovers() does the same ParseExact and FileInfo.Length.
- The name regex accepts any `\d+` version and any `\d{8}T\d{9}Z` stamp, so an overflowing version or an impossible date passes the regex and then throws.
- Workbench page creation is unguarded, and the only global handlers are AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException, which log but do not prevent termination. I found no Dispatcher-level handler anywhere in src.
- No design decision or TRACK.md entry accepts synchronous listing on the UI thread. docs/design/s7-ui-polish.md:648 specifies only that the list is re-read on re-show and after dialogs. Architecture §2.1 (docs/architecture.md:50) does say storage must not block the UI, so the conformance deviation is real.
- No test covers malformed backup names or a file vanishing mid-listing; every ListBackups test I found is a happy path.

Why the severity drops:
- Scenario (a), the in-app half, is weak. The delete dialog's Task.Run is awaited and Refresh fires via Changed afterwards, so the user would have to navigate away and back while the delete is in flight. The gap between EnumerateFiles and FileInfo.Length is microseconds per file. An external deleter hitting that gap is possible but very unlikely.
- Scenario (b) needs a file the app never writes. CreateVerified always formats the stamp with StampFormat and an int version, so only a hand-renamed or hand-crafted file triggers it. It fails closed (a crash, no data loss), but it is a robustness gap rather than a likely field failure.
- Scenario (c) is overstated. The listing is one directory enumeration plus one stat per file, with no content read. Migration backups are pruned to 5; before-delete and before-restore backups are not rotated, but even hundreds of stats take milliseconds on a local disk. Only a network-redirected profile would make it noticeable.
- StartupCoordinator.cs:67 runs after ConfigureAwait(false) awaits, so that call is not UI-blocking; only the unguarded parse applies there.

One thing the finding missed, which is arguably worse than the page crash: Prune() calls List() and catches only IOException and UnauthorizedAccessException. A single malformed-but-regex-matching filename therefore throws FormatException or OverflowException out of Prune, which is called from the migration path (SqliteDatabase.cs:519). The same file would also break the recovery listing at StartupCoordinator.cs:67. I did not trace what the callers above those two points do with the exception.

Net: a real architecture deviation and a real unguarded-parse defect, with low likelihood and a fail-closed outcome. The recommended fix (TryParse and skip, per-file IO catch, async load) is sound and cheap.

*Corrections to the finding.* - Severity is low, not medium. The triggers are a hand-crafted or hand-renamed filename, or a microsecond external-deletion race.
- Scenario (a): the in-app concurrency with the delete dialog's Task.Run is not a practical path. Refresh is triggered by Changed after the awaited delete completes; only navigating away and back mid-delete, or an external deleter, could hit the window.
- Scenario (c): the freeze claim is overstated. Listing is one stat per file with no content read, and migration backups are capped at 5. It would only be noticeable on a network-redirected profile.
- The StartupCoordinator.cs:67 call is off the UI thread, so only the unguarded-parse part of the finding applies there.
- Missed by the finding: Prune() (SqliteBackupFiles.cs:165-180) calls the same List() and does not catch FormatException or OverflowException, so a malformed-but-matching filename also throws out of the migration path's prune step at SqliteDatabase.cs:519.
- The claim that a UI-thread exception terminates the process rests on the absence of any Dispatcher-level handler in src; I did not reproduce it at runtime.

### architecture-2. ViewModelBoundaryTests (A6) only checks constructor parameter types in three namespaces; storage ports and concrete services already slip through

- Final: **low** (confirmed in part); auditor said medium
- Where: `tests/Mindweave.Architecture.Tests/ViewModelBoundaryTests.cs:86`; effort S

**Evidence.** IsConcreteService (lines 86-91) flags a type only if it is a non-abstract, non-record class from the Application or Infrastructure assembly, and the test inspects only constructor parameters of types whose namespace starts with Mindweave.Desktop.ViewModels/Navigation/Network (lines 16-21, 26-34). Consequences visible in the current code: (1) any interface passes, including persistence ports - BackupRestoreViewModel takes IBackupFiles + IDatabaseBackup (ViewModels/BackupDialogs.cs:81), BackupDeletionViewModel takes IBackupFiles (BackupDialogs.cs:236), StorageDetailsViewModel takes IBackupFiles (Pages/StorageDetailsViewModel.cs:24), ExitCoordinator takes IDatabaseLifecycle (ViewModels/ExitCoordinator.cs:54); nothing would stop a page from taking IConversationStore and bypassing StorageHealth gating and change events. (2) Mindweave.Desktop.Services is out of scope, so AppStartup takes six concrete services (Services/AppStartup.cs:28-40: StartupCoordinator, WorkspaceService, AppearanceService, StorageHealth, ConversationMarksService, WorkspaceNamePreparer) and passes itself to RecoveryViewModel as IRecoveryActions. (3) Fields, properties, method parameters, statics, IServiceProvider, and generic wrappers (Func<T>, Lazy<T>, IEnumerable<T>) are not inspected; a service declared as a `record` is exempt; UiText.Statistics.cs:52 already reads UsageStatisticsService.RecentDays statically. (4) Views are not covered at all.

**Failure scenario.** A future page injects ISummaryStore (or any store interface) directly and writes while storage is paused, skipping StorageHealth.ThrowIfBlocked and the service's Changed events; or a view model receives IServiceProvider/Func<ConversationService>. The architecture suite stays green while the rule TRACK.md states as enforced ("页面经接口取得应用服务") is violated.

**Recommendation.** Invert the rule to an allow-list: presentation types (include Mindweave.Desktop.Services and Views) may depend on Application types only from a named set of use-case interfaces + value records; explicitly forbid everything in Mindweave.Application.Storage / I*Store / IDatabase* / IChatClient / ICredentialStore / IServiceProvider, and unwrap generic arguments. Check fields as well as constructor parameters. Either move AppStartup's dependencies to interfaces or add it to a documented exception list in the test.

**Skeptic: partially confirmed, low.** The factual observations are accurate, but the finding presents a deliberately bounded, documented test as a defect and describes hypothetical future violations as things that "already slip through". No current presentation type violates the rule the test was written to enforce.

What holds:
- The test inspects only constructor parameters of types in the ViewModels, Navigation and Network namespaces, and flags only non-abstract, non-record, non-delegate classes from Application or Infrastructure.
- Interfaces of any kind pass, so a view model could take a store interface, IServiceProvider or Func<ConversationService> without the suite failing. Generic arguments are not unwrapped in IsConcreteService (they are in IsAvalonia).
- Fields, properties, method parameters and static references are not inspected; Views and Mindweave.Desktop.Services are out of scope.
- AppStartup (Services namespace) takes StartupCoordinator, WorkspaceService, AppearanceService, StorageHealth, ConversationMarksService and WorkspaceNamePreparer concretely, and implements IRecoveryActions.
- The cited view models do take IBackupFiles, IDatabaseBackup and IDatabaseLifecycle.

Why the severity is overstated:
- The narrow scope is an explicit, reviewed decision. proposal.md §8.2 says the universal reflection rule was withdrawn and replaced by exactly this bounded rule, and the test's own doc comment says "Bounded on purpose". It implements course requirement C3 (view models get interface references through constructors), which it does enforce. It was never specified as a storage-gating guard.
- No store interface, IServiceProvider or IChatClient appears anywhere in Desktop outside composition. The only Application ports found in presentation code are the three backup/lifecycle interfaces, which are use-case-level ports in Mindweave.Application.Startup, not persistence stores. Taking them through constructors is consistent with C3.
- The "bypass StorageHealth gating" scenario is purely prospective. A separate layering test already keeps Infrastructure mentions out of Desktop except the three composition files.
- AppStartup is a startup orchestrator, not a view model, so it is outside the rule as written. The one real inconsistency is small: proposal §8.2 lists AppStartup as a consumer of IWorkspaces, and IWorkspaces, IAppearance, IStorageStatus and IConversationMarks all exist, yet AppStartup still takes the concrete classes. I did not check whether it needs members those interfaces lack.
- UiText reading the constant UsageStatisticsService.RecentDays is a compile-time constant reference in a text helper, not a service dependency.

Net: this is a real but low-severity test-hardening opportunity (forbid store interfaces and IServiceProvider, unwrap generics, perhaps cover Services), plus a minor doc/code mismatch on AppStartup. The TRACK.md phrase "页面经接口取得应用服务" is satisfied by the current code.

*Corrections to the finding.* - "Storage ports and concrete services already slip through" is overstated. No store interface is injected into any presentation type. The interfaces that pass (IBackupFiles, IDatabaseBackup, IDatabaseLifecycle) are application-level ports, and passing interfaces through constructors is what rule C3/A6 asks for.
- The concrete services are in AppStartup, which is in Mindweave.Desktop.Services and is not a view model. The reviewed proposal scoped the rule to ViewModels/, Navigation/ and Network/ on purpose, so this is not a hole in an intended check. The fair residual point is that proposal §8.2 names AppStartup as an IWorkspaces consumer while the code takes WorkspaceService.
- The claim that TRACK.md's rule "is violated" while the suite stays green does not describe the current code; it describes a hypothetical future change.
- Line and path corrections: IsConcreteService is at lines 78-84, not 86-91; StorageDetailsViewModel is at ViewModels/Pages/StorageDetailsViewModel.cs; UiText.Statistics.cs is at the Desktop project root.
- The record exemption and the static RecentDays read are true but immaterial: no service is declared as a record to dodge the test, and RecentDays is a constant.
- The recommended allow-list inversion would reintroduce the universal rule that the architecture review explicitly withdrew. A narrower hardening (forbid store interfaces and IServiceProvider, unwrap generic arguments) fits the adopted decision better.

### architecture-3. In-app restore session swap has no failure path: an exception while ending the old session leaves the window on the startup page with no database

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/AppHost.cs:125`; effort M

**Evidence.** AppSession.RunAsync (lines ~125-140) does, without any try/catch: `var next = Build(); next...Show(new StartupViewModel()); Services = next; Replaced?.Invoke(...); old...ReleaseAll(); await EndAsync(old, TimeSpan.Zero); await next.GetRequiredService<AppStartup>().RestoreInPlaceAsync(backup, keptCopy);`. EndAsync (lines ~148-173) rethrows anything thrown by GenerationCoordinator.ShutdownAsync, StorageHealth.StopAsync/RetryKeptAsync, the two FlushAsync calls, SqliteDatabase.CloseAsync or ServiceProvider.DisposeAsync (which disposes ~10 IDisposable/IAsyncDisposable singletons). The caller chain is BackupRestoreViewModel.HandOverAsync (BackupDialogs.cs, `await _restore.RestoreAsync(backup, kept)`) -> ExitCoordinator.RestoreAsync, whose catch logs and then calls `generation.Reopen(); BackToIdle();` on the OLD, already-disposed session.

**Failure scenario.** During a restore, one of the old session's dispose/flush steps throws (e.g. an I/O error closing the database or a Dispose in a singleton). The window has already been switched to the new session's StartupViewModel, the old database is closed, and RestoreInPlaceAsync is never called on the new session: the user sees the startup spinner indefinitely, with neither the restored nor the previous data opened and no recovery page, while the old exit coordinator 'reopens' generation on disposed services. Data on disk is intact, but the app is unusable until restarted and the restore silently did not happen.

**Recommendation.** Make the swap total: wrap the old-session teardown so that any failure is logged but still proceeds to start the new session (RestoreInPlaceAsync, or a plain AppStartup.RunAsync on the untouched database plus an error notice), and never route post-swap failures back into the old session's ExitCoordinator. Add a test that injects a throwing disposable into the old session.

**Skeptic: partially confirmed, low.** The structural claim is true; the severity is overstated because no concrete trigger was identified, and I could not find one either.

**What holds.** `AppSession.RunAsync` has no try/catch. It builds the new provider, shows `StartupViewModel`, swaps `Services`, raises `Replaced`, releases the old pages, then awaits `EndAsync(old)` and only afterwards `RestoreInPlaceAsync` on the new session. `EndAsync` uses try/finally only, so any exception from its steps propagates and `RestoreInPlaceAsync` is skipped. Nothing else starts the new session's `AppStartup`, so the window stays on the startup page.

**Where the exception goes.** It travels through `BackupRestoreViewModel.HandOverAsync` (no catch around `_restore.RestoreAsync`) into the old session's `ExitCoordinator.RestoreAsync`. That catch logs, then calls `generation.Reopen()` and `BackToIdle()` on the old, disposed session. Its own comment says "Anything failing before the swap leaves the data as it was: back to work", so the code assumes failures are pre-swap only. That assumption is false for anything thrown inside `AppSession.RunAsync`.

**No mitigating decision or test.** The design note for the session swap in `s7-ui-polish.md` (lines 656-672, including its "未验证" list) does not mention teardown failure. The only tests touching `LastRestore` await the happy path; none injects a throwing teardown.

**Why low, not medium.**
- The teardown steps I read are defensive:
  - `GenerationCoordinator.ShutdownAsync` is idempotent and was already stopped by the exit steps.
  - `StorageHealth.StopAsync` swallows `OperationCanceledException`.
  - `SqliteDatabase.CloseAsync` is idempotent and only disposes the writer.
- A throw therefore needs an unexpected defect, such as a singleton `Dispose` throwing or an event handler throwing from `RaiseChanged`. I did not read the two `FlushAsync` bodies or every singleton's `Dispose`.
- Consequence is bounded: the live database is untouched because replacement happens only inside `RestoreInPlaceAsync`, the "before restore" backup exists, and the failure is logged at Error. A restart recovers.

So this is a real robustness gap in a rare, destructive-adjacent flow: a stuck startup page with no message. It is not a data-loss risk.

*Corrections to the finding.* - **No trigger shown.** The finding's examples ("I/O error closing the database or a Dispose in a singleton") are hypothetical. `CloseAsync` only disposes the writer and is idempotent, and the other steps I read are defensive.
- **"Unusable until restarted" is unverified.** Window close routes through the new session's root and exit coordinator. I did not confirm whether closing works cleanly from the stuck startup page.
- **"Silently" is slightly overstated.** The exception is logged at Error by `ExitCoordinator.LogStepFailed`, though the message text wrongly says the window stays open with retry and return. Nothing is shown to the user.
- **`Reopen()`/`BackToIdle()` on the old session looks harmless.** They act on objects that are no longer displayed.
- **The gap is wider than stated.** `AppStartup.RestoreInPlaceAsync` can also throw after the swap: in `ApplyPreferencesAsync`, `_shell.RestoreSidebarAsync()` and `_marks.LoadAsync()` are unguarded and run before `Apply(outcome)`. That would leave the same stuck startup page and the same misrouted catch.
- **The fix should cover the whole of `RunAsync`.** Guard both the old-session teardown and the new-session start, not only the teardown.

### architecture-4. About 25 optional (`= null`) constructor parameters on container-built singletons make ValidateOnBuild blind to missing registrations

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Application/Conversations/ConversationService.cs:439`; effort M

**Evidence.** Container-resolved types declare dependencies as optional: ConversationService(IContextSummaryStore? summaries = null, IConversationArchiveStore? archive = null); ReadingStateService(..., StorageHealth? health = null) with `health?.RegisterRetry(_ => FlushAsync())` (Reading/ReadingStateService.cs:40); GenerationRequests(ContextCompressor? compressor = null); SummaryService(IContextSummaryStore? contextSummaries = null); ShellViewModel (6 optional: LayoutPromptViewModel, NetworkSessions, ISidebarLayout, IWorkspaceDeletionPrompt, WorkspaceNewsViewModel, WorkspaceSwitchRequests); WorkspaceNewsViewModel (INewsAlerts, IAlertSound, WorkspaceNewsOptions, IConversations); ConversationListViewModel (IConversationMarks, ConversationDeletionRequests, ISummaries, IStorageStatus). Microsoft.Extensions.DependencyInjection silently substitutes the default for an unregistered optional parameter, and ValidateOnBuild (CompositionRoot.cs:76) does not report it. The null branches change behavior rather than fail: ConversationService.cs:439 returns an empty RestorePlan and line 474 reports nothing as archived when `_archive is null`; line 163/597 drop compression information when `_summaries is null`. Several of the affected registrations are TryAdd-based and order-sensitive (InfrastructureServices.cs:42-45, CompositionRoot.cs:43-44, 53, 61, 64, 71-72).

**Failure scenario.** A refactor renames or drops `services.TryAddSingleton<IConversationArchiveStore, SqliteArchiveStore>()` (or a test-oriented override ends up registering a different service type). The app builds, ValidateOnBuild passes, and at runtime archived conversations are treated as not archived, or reading-state writes that failed are never retried after storage recovers (ReadingStateService without StorageHealth) - with no error anywhere.

**Recommendation.** Make production dependencies required in the constructor the container uses (keep convenience overloads for tests as separate internal constructors or test builders), or add an architecture/composition test that resolves every registered singleton from CompositionRoot.Build and asserts via reflection that no reference-type constructor parameter of a Mindweave type was satisfied by its default.

**Skeptic: partially confirmed, low.** The structural claim is true; the severity is overstated because it is a latent robustness gap, not a present defect.

What holds:
- The cited optional constructor parameters exist on types the container builds by type (`AddSingleton<T>()`, no factory), so Microsoft.Extensions.DependencyInjection would substitute the default for an unregistered one and `ValidateOnBuild` would not report it.
- The null branches degrade silently as described: an empty `RestorePlan`, nothing reported as archived, compression information dropped, and no retry registration in `ReadingStateService`.
- No guard exists. The only reflection over constructors in tests is `ViewModelBoundaryTests`, which checks layer boundaries, not defaults. Neither `TRACK.md` nor `docs/design/decisions.md` records the optional parameters as an intended pattern.

Why it is low rather than medium:
- Every one of these dependencies is registered today, so nothing is currently broken. The failure needs a future refactor to drop a registration.
- The suite makes the headline scenario unlikely to go unnoticed. 50 test files use `DesktopHarness`, which calls the real `CompositionRoot.Build` with real SQLite infrastructure. `ArchiveUiTests`, `RowMenuUiTests` and `MultiSelectUiTests` exercise archiving, and `ContextUiTests`, `LayoutDiskFullTests` and `SidebarStatusTests` touch compression and storage health. I did not run them or confirm they would fail for each specific dependency; the `ReadingStateService` retry path is the least certain to be covered.
- Not every null path is silent: archive mutations throw `InvalidOperationException("No archive store is configured.")`.
- The "test override registers a different service type" scenario does not affect the shipped app, since production passes no overrides.

The recommendation (required parameters, or a composition test asserting no Mindweave-typed parameter fell back to its default) is sound and cheap, but this is hardening.

*Corrections to the finding.* - Severity should be low: all the dependencies are registered today and the real composition root is exercised by 50 harness-based test files.
- "With no error anywhere" is too strong for the archive store: mutation paths throw at ConversationService.cs:480. Only the read paths (restore plan, archived-among, compression flags) degrade silently.
- The order-sensitive TryAdd citation is slightly off. The relevant lines in InfrastructureServices.cs are 40-45, and line 42 (`IContextSummaryStore`) is a plain `AddSingleton`, not TryAdd.
- "About 25" is roughly right for container-built types: I count about 21 reference-type optional parameters, or 22 with the `TimeProvider?` on `ConversationDeletionViewModel`. The auditor omitted `IWorkspaceNameCache?` on `WorkspaceService` and `WorkspaceDeletionService`.
- Other optional parameters in the grep (`ConversationViewModel`, `ArchivedViewModel`, `WorkspacesViewModel`, `SectionPages`) belong to page view models that are not registered in `CompositionRoot`. I did not inspect how `PageFactory` constructs them, so they are not counted.

### architecture-5. Backup-restore and exit use cases are orchestrated inside Desktop view models instead of Application

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/ViewModels/BackupDialogs.cs:164`; effort M

**Evidence.** BackupRestoreViewModel.RunAsync/HandOverAsync (lines 164-222) implements the restore procedure: check the backup via IBackupFiles.Check, run the exit sequence, call IDatabaseBackup.BackupAsync(BackupReason.BeforeRestore), then IInAppRestore.RestoreAsync. ExitCoordinator (ViewModels/ExitCoordinator.cs:306-316) defines the ordered save list (exports.WhenIdleAsync, workbench.FlushAsync, summaries.FlushDraftsAsync, reading.FlushAsync, generation.SaveUnsavedAsync, database.DrainAsync), and AppSession.EndAsync (AppHost.cs:148-173) holds a second, different hand-maintained list resolved by service location of concrete types (GenerationCoordinator, StorageHealth, ReadingStateService, ConversationMarksService, SqliteDatabase). Architecture §2 says "ViewModel 通过用例与服务接口工作" and §2.2 gives each state a single owner reached through use cases. Size reflects this: Desktop is 17.6k lines (52% of src), Core 1.3k (4%).

**Failure scenario.** A new owner of deferred writes (the next debounced store, e.g. for the v0.13 recommended-nodes cache) must be added by hand to two lists in the UI project; if it is added only to one, it is either not flushed on normal exit or not on the last-resort path. The ordering rules that protect data (back up current data before swapping, stop generation before close) can only be tested through headless-UI tests, and a second front end or a CLI maintenance tool could not reuse them.

**Recommendation.** Move the non-visual parts into Application: a RestoreBackup use case (check -> keep copy -> hand-over callback) and a single 'flush everything' participant registry (StorageHealth.RegisterRetry already is one; extend it or add IFlushOnExit) that both ExitCoordinator and AppSession.EndAsync iterate. Keep prompts and stage display in the view models.

### architecture-6. LayeringTests' Desktop->Infrastructure rule is a substring scan with known holes; the assembly-name allow-lists accept any `System.*` package

- Final: **low** (not verified); auditor said low
- Where: `tests/Mindweave.Architecture.Tests/LayeringTests.cs:34`; effort S

**Evidence.** Desktop_mentions_infrastructure_only_in_the_composition_files (lines 32-46) passes when a file does not contain the text "Mindweave.Infrastructure". Infrastructure types exposed through members of the three allowed files are usable elsewhere without that text: App.axaml.cs:46 already calls `_host.Instance.StartListening(...)` where Instance is Mindweave.Infrastructure.Hosting.SingleInstance, and AppSession.Services / CompositionRoot.Build(AppPaths, ...) are public. AppHost.cs is on the allow-list but is not just composition: it contains the public AppSession class (session lifetime logic). The csproj hard-references Infrastructure (Mindweave.Desktop.csproj), so the compiler gives no help, and a global using or <Using> item in the csproj would also evade the scan. Separately, IsRuntime (line 51-52) treats every assembly whose name starts with "System." as runtime, so Core/Application could reference NuGet packages such as System.Data.SQLite, System.ClientModel (the base of vendor AI SDKs), System.Reactive or System.Net.ServerSentEvents without failing Core_depends_on_nothing_but_the_runtime / Application_depends_only_on_core_and_logging_abstractions. Infrastructure has no test restricting what it may reference beyond 'not Avalonia / not Mindweave'.

**Failure scenario.** A view model receives `AppHost`/`AppSession` or a value returned from CompositionRoot and calls SqliteDatabase or SingleInstance members through `var`; or someone adds System.ClientModel to Application for an SDK type. Both violate architecture §2 ("只在启动组装处引用 Infrastructure", "不引用...供应商 SDK") and the suite stays green.

**Recommendation.** Check by metadata instead of text: for every Desktop type outside an explicit composition set, assert no field/parameter/return/local-variable type (via MethodBody.LocalVariables) and no member reference comes from the Infrastructure assembly - or move composition (Program, CompositionRoot, AppHost/AppSession) into its own small project so the compiler enforces it. Replace the `System.` prefix test with a comparison against the shared-framework reference set (e.g. assemblies located in the runtime directory).

### architecture-7. Per-session resources are created outside container ownership and never disposed (file logger, HttpClient)

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/CompositionRoot.cs:30`; effort S

**Evidence.** CompositionRoot.cs:27-31: `builder.AddProvider(logging ?? new FileLoggerProvider(paths.LogDirectory, TimeProvider.System))` registers a pre-built instance; the container does not dispose externally supplied instances and LoggerFactory only disposes providers added through its own AddProvider, so FileLoggerProvider.Dispose (Logging/FileLoggerProvider.cs:36-43, which closes the StreamWriter) is never called. InfrastructureServices.cs:47-49 creates `new HttpClient(ChatCompletionsClient.CreateHandler())` inside a factory and hands it to ChatCompletionsClient, which is not IDisposable (Ai/ChatCompletionsClient.cs:28), so the SocketsHttpHandler and its connection pool are never disposed. CompositionRoot.Build runs once per AppSession (Program.cs:35; AppSession.Build on every in-app restore, AppHost.cs:142). Also `services.AddSingleton(shell ?? new SystemShell())` and the options instances follow the same pattern (harmless today).

**Failure scenario.** Each in-app restore leaves one open handle on today's log file and one live HTTP handler/connection pool from the previous session for the rest of the process; two FileLoggerProvider instances can also write the same file concurrently during the hand-over (interleaved lines). Bounded and small per restore, but it contradicts the stated design that a restored session is 'exactly as after a fresh start'.

**Recommendation.** Register these through factories so the container owns them (`services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(...))`), make ChatCompletionsClient IDisposable (or register HttpClient as its own singleton), or hoist genuinely process-wide resources (logger, HttpClient) out of the per-session container and pass them in.

### architecture-8. A6 'per-caller' interfaces have grown from 10 to 19 one-to-one service aliases, and the main ones are per-service rather than per-caller

- Final: **low** (not verified); auditor said low; already known in TRACK.md
- Where: `src/Mindweave.Infrastructure/ApplicationServices.cs:62`; effort M

**Evidence.** ApplicationServices.cs:31-82 registers 19 interface aliases, each forwarding to exactly one concrete singleton (IStorageStatus, IWorkspaceNameCache, IConversations, IConversationMarks, IGenerationExit, ISummaries, IReadingState, ISummaryExport, IModelSettings, IWorkspaces, IWorkspaceNameSuggestions, IWorkspaceDeletion, IConversationDeletion, IAppearance, ISidebarLayout, INewsAlerts, INetworkLayout, IUsageStatistics, IUsagePrices). TRACK.md line 89 records the pending decision as "A6 加了十个按调用方裁剪的接口...数量接近'逐类接口'"; architecture §2 says "不引入通用命令总线、逐类接口". IConversations (Application/Conversations/IConversations.cs:12-63) has 4 events + 20 methods and is consumed by 10 Desktop files (ConversationViewModel, NewConversationViewModel, ArchivedViewModel, NetworkHomeViewModel, WorkspaceNewsViewModel, ConversationListViewModel, the two deletion view models, ConversationArchiving, ConversationLifecycleActions), so e.g. NewConversationViewModel, which needs StartAsync, can also archive, rename and restore. ISummaries has 21 members including exit-only FlushDraftsAsync.

**Failure scenario.** Checkable deficiency rather than a runtime bug: the count recorded for the user's pending decision is stale by almost 2x, every new service keeps adding a mirror interface that must be kept in sync by hand (signatures are duplicated, docs via inheritdoc), and test doubles for a page must stub a 24-member interface. The interfaces give substitution but not the narrowing that 'per-caller' implies.

**Recommendation.** Update the TRACK.md pending-decision entry with the current count so the user decides on real numbers. If the interfaces stay, either split the two large ones by consumer role (reading/listing, sending, lifecycle, exit) or drop the 'per-caller' description; consider generating nothing new unless a page test actually substitutes it (only IConversations, IWorkspaceNameSuggestions and IUsageStatistics are registered with TryAdd for that purpose).

### architecture-9. Concentration points and namespace cycles that no test watches: PageFactory (26 constructor parameters), SqliteDatabase (five roles), Desktop namespace cycles

- Final: **info** (not verified); auditor said info
- Where: `src/Mindweave.Desktop/Services/PageFactory.cs:14`; effort M

**Evidence.** PageFactory's primary constructor takes 26 dependencies (lines 14-40) and is a hand-written service locator for all pages; each new page adds parameters and it is the only place where page wiring mistakes can hide (e.g. NewConversationViewModel is passed `context.Navigator` twice, line 48). SqliteDatabase (src/Mindweave.Infrastructure/Sqlite/SqliteDatabase.cs:24, 622 lines) implements IDatabaseLifecycle, IDatabaseBackup, IBackupFiles and IStorageProbe in addition to owning connections and the write queue, and is the target of four interface aliases (InfrastructureServices.cs:27-31). A using-directive scan shows mutual dependencies Desktop.ViewModels <-> Desktop.Services (5/2 files), Desktop.ViewModels <-> Desktop.Navigation (7/4 files), ViewModels.Conversations <-> ViewModels.Pages, ViewModels.* <-> Services (through PageFactory), and Application.Statistics <-> Application.Storage (IStatisticsStore.cs imports Statistics types while Statistics imports Storage). Mindweave.Application.Storage (14 files) mixes store contracts with StorageHealth behavior. The architecture test project has no namespace-level rule. Largest classes: WorkspacesViewModel.cs 785 lines, ConversationService.cs 634, ConversationListViewModel.cs 625, UsageStatisticsViewModel.cs 624.

**Failure scenario.** No wrong behavior today. The practical cost is the one TRACK.md already observed for App.axaml/UiText: every parallel feature branch edits PageFactory and SqliteDatabase, so they are merge-conflict and review hot spots, and the Navigation layer cannot be reused or tested without the whole ViewModels namespace.

**Recommendation.** Let pages be created from per-page factories or ActivatorUtilities with route/context as explicit arguments so PageFactory stops growing; split backup-file management out of SqliteDatabase as its own registered service (SqliteBackupFiles already exists internally); move record types shared by Statistics and its store into one namespace. Optionally add a cheap namespace-dependency test (Navigation must not reference ViewModels.Pages, Application.Storage must not reference feature namespaces).

