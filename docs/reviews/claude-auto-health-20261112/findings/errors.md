# Error handling and resilience — grade B

The storage-failure core is designed well above typical desktop-app standard: classified write failures, a dedicated StorageHealth with epoch/obligation-based recovery, kept terminal writes, atomic export and restore with undo, crash recovery of unfinished work, and fault-injection tests. The grade is held down by what sits around that core. There is no last line of defence on the UI thread, so any exception a command does not explicitly catch terminates the app, and two such paths are reachable exactly while storage is paused. Startup has no failure path after the database opens (a read-only database file crashes every launch; verified with a probe). Generation runners use closed catch lists with a log-only fallback, so an unexpected exception leaves an answer permanently "generating" (two concrete triggers verified). Read failures have no policy at all. These are plausible production scenarios, not theoretical ones. Nothing here was run against the real app; the findings come from reading the code plus three small stand-alone probes of runtime behaviour.

**Strengths**

- Write failures are classified once at the single writer (src/Mindweave.Infrastructure/Sqlite/StorageFailures.cs:12-26, SqliteWriteQueue.cs:100-105) into StorageWriteException kinds (Full/Busy/ReadOnly/IoError/Corrupt); request errors such as constraint violations deliberately stay SqliteException, so defects are not mistaken for storage conditions.
- StorageHealth (src/Mindweave.Application/Storage/StorageHealth.cs) is a carefully reasoned recovery state machine: an epoch counter prevents a recovery pass from clearing a failure that happened during it (lines 226-248, 310-318), kept content holds explicit obligations, the lock is a documented leaf lock, and recovery requires both a rolled-back DB write and a flushed 64 KB file probe (SqliteDatabase.cs:247-260) because a rolled-back WAL write never touches the disk.
- Terminal answer writes are never dropped: TerminalWrites.cs:46-78 keeps storage-refused writes with an obligation and separates them from defects (which are reported at exit but do not pause the app); retries are serialized against the exit flush by a gate (lines 85-103).
- Accepted questions are durable before they are queued (ConversationService.cs:227-240), unfinished work from a previous process is marked interrupted and never re-sent (StartupCoordinator.cs:70-76), and write cancellation is honoured only before a transaction starts (SqliteWriteQueue.cs:79-84).
- Database open is defensive: handle leak on failed open closed (SqliteConnections.cs:47-60), new database built under a temporary name and renamed (SqliteDatabase.cs:523-546), verified pre-migration backup required before migrating with a distinct BackupFailed status (493-504), single-transaction migration with foreign_key_check (559-582), transient open errors retried for 3 s after a crash (394-415).
- In-app restore re-checks the backup, moves files with full undo on failure, refuses to create an empty database after a failed undo, and reports each failure mode distinctly (SqliteDatabase.cs:128-188, SqliteBackupFiles.cs:248-322).
- The exit sequence cannot get stuck in 'stopping': every step is wrapped and ends in a Failed state with retry/return (ExitCoordinator.cs:207-285), matching architecture section 12.3; AppHost releases the single-instance lock in a finally whatever fails (AppHost.cs:59-73). A probe confirmed that on .NET 10/macOS the finally in AppHost.Run does run after an unhandled UI-loop exception.
- The SSE client distinguishes first-byte timeout, idle stall, user stop and stream-ended-without-finish, and scrubs the API key from every ChatClientException (ChatCompletionsClient.cs:40-63, 185-230, 303-304); cut-off or empty output is never stored for titles, summaries or names (GenerationRequests.cs:118-128).
- Export uses write-temp, fsync, rename with a bounded retry for sharing violations and classifies in-use versus not-writable (AtomicFileWriter.cs:46-142).
- Fault paths are tested, not just written: tests/Mindweave.Infrastructure.Tests/Faults, StoragePauseUiTests, LayoutDiskFullTests, DraftLossTests, ExitSavingTests, RecoveryTests, kill-process tests; the baseline run shows Desktop tests 381 passed, 0 failed.

**Not examined**

- Windows-specific behaviour: the Windows credential store, whether the finally in AppHost.Run executes after an unhandled exception on Windows (verified only on macOS), rename-while-open semantics during restore.
- MacKeychainCredentialStore and credential error paths beyond a skim of the catch sites.
- View code-behind (src/Mindweave.Desktop/Views/**), NetworkCanvas/NetworkSession persistence, Markdown rendering error paths.
- WorkspaceNamePreparation.cs, WorkspaceNameSuggestions.cs, WorkspaceDeletion.cs, SummaryService.cs and SqliteSummaryStore.cs were only grepped, not read in full.
- SchemaMigrations.cs content and per-migration failure behaviour (covered by another dimension).
- No tests were run and the application was not launched; the UI-crash and stuck-answer conclusions come from reading call chains, plus three stand-alone probes outside the repo (JsonNode indexer exceptions, UPDATE on a read-only SQLite file, finally-after-unhandled-exception).
- Real disk-full and ejected-volume behaviour was not simulated.
- Whether VACUUM INTO output is fsynced before a pre-delete backup is relied on.

### errors-1. Unhandled exceptions in UI commands terminate the app; two such paths are reachable while storage is paused, losing kept answers

- Final: **high** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Desktop/ViewModels/Conversations/ConversationViewModel.cs:296`; effort M

**Evidence.** Program.cs:36-37 installs only AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException, and both only log. There is no Dispatcher.UIThread.UnhandledException handler and no AsyncRelayCommandOptions.FlowExceptionsToTaskScheduler anywhere in src (grep). CommunityToolkit's AsyncRelayCommand rethrows a faulted command on the UI synchronization context, so any exception a command does not catch leaves the dispatcher loop and ends the process.

Concrete uncaught paths:
(a) ConversationViewModel.CommitRenameAsync (lines 295-300) awaits _conversations.RenameAsync with no try/catch. ConversationService.RenameAsync (504-508) goes through CommitAsync, which calls _health.ThrowIfBlocked() and rethrows StorageWriteException (517-529). The rename box and SaveRenameButton (ConversationView.axaml:74-78) are not gated on IsStorageBlocked. The sidebar's equivalent does catch it (ConversationListViewModel.cs:375-385), which shows this one is an omission.
(b) ModelSettingsViewModel.RunAsync (271-295) catches only ModelSettingsException, ChatClientException and CredentialUnavailableException. SaveAsync (98-100) calls ModelSettingsService.SaveAsync, whose store.SaveActiveAsync goes through the write queue and throws StorageWriteException; that service does not consult StorageHealth at all.
(c) RecoveryViewModel.RetryAsync and ConfirmRestoreAsync (63-106) use try/finally with no catch.

**Failure scenario.** Disk fills while an answer streams. The answer finishes, its terminal write is refused and kept in memory (TerminalWrites.cs:57), and the page shows the 'storage paused' banner. The user renames the conversation on its page, or opens Settings and presses Save. StorageWriteException propagates out of the command and the app exits. Because storage is still refusing writes, the final flush cannot save the kept terminal write; the answer text after the last successful checkpoint is lost and the answer is marked interrupted on the next start. The same crash happens for a plain write failure on these two commands even when storage was not yet paused.

**Recommendation.** Add a last line of defence: handle Dispatcher.UIThread.UnhandledException (log, show a non-modal error, set Handled for non-fatal exception types), or build commands with a shared wrapper that catches and reports. Then fix the specific gaps: catch StorageWriteException in ConversationViewModel.CommitRenameAsync and ModelSettingsViewModel.RunAsync (showing UiText.StoragePaused), and gate rename on IsStorageBlocked. Add a test that every [RelayCommand] async method survives a StorageWriteException from its service; an architecture test could enumerate the commands.

**Skeptic: partially confirmed, medium.** I could not refute the core of the finding; every cited code path exists and says what is claimed. The severity is overstated because the data-loss part is conditional and the trigger is narrow.

**What holds**
- **No last line of defence.** A grep of `src` for `UnhandledException`, `FlowExceptionsToTaskScheduler` and `UnobservedTaskException` returns only the two log-only handlers in `Program.cs`. There is no dispatcher-level handler and no shared command wrapper.
- **(a) Conversation-page rename.** `ConversationViewModel.CommitRenameAsync` awaits `RenameAsync` with no catch. `ConversationService.RenameAsync` goes through `CommitAsync`, which calls `_health.ThrowIfBlocked()` and rethrows `StorageWriteException`. The rename box and `SaveRenameButton` are bound only to `IsRenaming`; `BeginRename` has no storage gate.
- **It is an omission, not a design choice.** The sibling `SummaryViewModel.CommitRenameAsync` routes through a `RunAsync` wrapper, and the sidebar and workspace renames handle `StorageWriteException`. The conversation page is the outlier.
- **(b) Model settings Save.** `ModelSettingsViewModel.RunAsync` catches only `ModelSettingsException`, `ChatClientException` and `CredentialUnavailableException`. `ModelSettingsService` has no `StorageWriteException` or `_health` handling, and `SqliteModelSettingsStore.SaveActiveAsync` goes through `database.WriteAsync`.
- **(c) Recovery page.** `RetryAsync` and `ConfirmRestoreAsync` are try/finally with no catch.
- **No test or decision covers it.** No test exercises conversation-page rename or settings save under a storage failure. `TRACK.md` and `docs/design` have no accepted-risk entry; `s6-wrapup.md:173` shows the team already infers that an uncaught command exception is an unhandled exception in the UI.

**What is overstated**
- **The answer loss is not caused by the crash alone.** `AppHost.Run` wraps the UI loop in try/finally and flushes kept writes even when the loop throws. The kept terminal write is lost only if storage is still refusing writes at that moment, in which case a deliberate quit would lose it too. The crash removes the chance for background recovery (`TerminalWrites` retry) to save it after the user frees space. Text up to the last checkpoint survives either way.
- **(c) is weaker than (a) and (b).** `AppStartup.RunAsync` and `RestoreAsync` return failures as outcomes from the startup coordinator, so a throw there needs an unexpected defect. No concrete throwing path was shown.
- **The trigger is narrow.** It needs a storage failure (disk full, I/O error, read-only) plus one of two specific user actions.

**Not verified at runtime**
The claim that the process actually terminates rests on CommunityToolkit.Mvvm 8.4.2's documented default (a faulted `AsyncRelayCommand` is rethrown on the captured synchronization context) and on Avalonia having no handler installed. I did not run it, because building was forbidden. The project's own design note makes the same inference and also records it as unverified.

**Severity**
Medium: a real, user-reachable crash in exactly the degraded state the app otherwise handles carefully, with no global safety net. It is not high, because the trigger is rare and the extra data loss is conditional and bounded by checkpoints and the exit flush.

*Corrections to the finding.* - **Data loss is overstated.** The crash path still runs the exit flush (`AppHost.Run` finally). The kept answer is lost only if storage is still blocked at that moment, which a normal quit would also lose; the crash's real cost is removing the window for background recovery. Loss is bounded to text after the last checkpoint.
- **Wrong file path for (b).** `ModelSettingsViewModel` is at `src/Mindweave.Desktop/ViewModels/Pages/ModelSettingsViewModel.cs`, not `ViewModels/Settings/`. The line numbers are correct.
- **(c) is latent, not demonstrated.** The recovery actions return failures as outcomes; the finding shows no concrete path that throws.
- **Process termination is inferred, not observed.** It rests on CommunityToolkit's default rethrow behaviour and the absence of a dispatcher handler; nobody has run it.
- **Severity should be medium, not high.**

**Reproduction attempt: confirmed, high.** Reproduced at track/claude-auto 1ba95c9 with two headless UI tests in tests/Mindweave.Desktop.Tests (both passed, meaning the defect is real).

(a) Rename on the conversation page: with StorageHealth blocked, SaveRenameButton is visible and enabled. CommitRenameCommand.ExecuteAsync throws StorageWriteException. Invoking the same command the way a button does (ICommand.Execute) and then pumping the dispatcher makes Dispatcher.UIThread.RunJobs() throw StorageWriteException, i.e. the exception leaves the dispatcher loop. In the real app that is the main loop inside StartWithClassicDesktopLifetime, and nothing handles it: Program.cs installs only AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException, both log-only; grep finds no Dispatcher UnhandledException handler and no FlowExceptionsToTaskScheduler in src.

(b) Model settings Save on a really full database (PRAGMA max_page_count, the repo's own FillDiskAsync helper): SaveCommand.ExecuteAsync throws StorageWriteException; RunAsync catches only ModelSettingsException, ChatClientException and CredentialUnavailableException.

(c) RecoveryViewModel.RetryAsync / ConfirmRestoreAsync: confirmed by reading only (try/finally, no catch); I did not test which exceptions AppStartup can actually throw there.

Not reproduced, code reading only: the data-loss consequence. AppHost.Run does run a final flush in a finally block even when the UI loop throws (AppHost.cs:43-47), so the kept answer is lost only if storage is still refusing writes at that moment. That is the likely case for a full disk, but if space has come back the flush would save it. For a plain write failure when storage was not yet paused, the app still crashes but no kept answer is at stake. I also did not run the real (non-headless) app to watch the process exit; that step rests on the dispatcher throw shown in the test plus the absence of any handler.

Severity stays high: an enabled button on a page that is already showing the 'storage paused' banner crashes the app, in exactly the state where unsaved answer text is being held in memory.

*Corrections to the finding.* 1. Wrong path: ModelSettingsViewModel is at src/Mindweave.Desktop/ViewModels/Pages/ModelSettingsViewModel.cs, not ViewModels/Settings/. Line numbers are right.
2. The data-loss claim is conditional, not certain: AppHost.Run flushes kept writes in a finally block after the crash, so the answer is lost only if storage still refuses writes at exit. The finding says this, but the title ('losing kept answers') reads as unconditional.
3. 'Two such paths are reachable while storage is paused': only rename is tied to the pause itself (ThrowIfBlocked). Settings Save ignores StorageHealth and crashes only when its own write fails; my test needed a genuinely full database plus a 20,000-character name to force page growth. A small settings update on a full disk may fit in existing pages and succeed.
4. Part (c) (RecoveryViewModel) is unverified as a crash path: the missing catch is real, but I did not establish that AppStartup.RunAsync/RestoreAsync can throw to the caller.
5. The worktree was created from main (docs only); I checked out track/claude-auto detached inside it to get the code, then switched back and cleaned up. Nothing committed.

<details><summary>Reproduction</summary>

File added temporarily (now deleted): tests/Mindweave.Desktop.Tests/ReproUnhandledCommandTests.cs

```csharp
public class ReproUnhandledCommandTests
{
    [AvaloniaFact]
    public async Task Rename_on_the_page_while_storage_is_paused_throws_out_of_the_command()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var page = await app.StartWithConversationAsync(waitForTitle: true);
        var health = app.Services.GetRequiredService<StorageHealth>();
        var kept = health.Retain(new StorageWriteException(StorageFailureKind.Full, "simulated full disk", null));
        await UiWait.UntilAsync(() => page.IsStorageBlocked);

        page.BeginRenameCommand.Execute(null);
        page.RenameText = "新的标题";
        window.UpdateLayout();
        var save = Find<Button>(CurrentView(window), "SaveRenameButton");
        Assert.True(save.IsEffectivelyVisible);
        Assert.True(save.IsEffectivelyEnabled); // not gated on the pause

        var ex = await Assert.ThrowsAsync<StorageWriteException>(() => page.CommitRenameCommand.ExecuteAsync(null));

        // The form a button uses (ICommand.Execute): rethrown on the UI context, out of the dispatcher loop.
        page.BeginRenameCommand.Execute(null);
        Exception? fromDispatcher = null;
        try
        {
            save.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }
        catch (Exception e) { fromDispatcher = e; }
        Assert.IsType<StorageWriteException>(fromDispatcher);
        kept.Resolve();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Saving_model_settings_on_a_full_disk_throws_out_of_the_command()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        var page = await app.StartWithConversationAsync(waitForTitle: true);
        var service = app.Services.GetRequiredService<ModelSettingsService>();
        var model = new ModelSettingsViewModel(service);
        await UiWait.UntilAsync(() => model.BaseUrl == "https://fake.example/v1");
        var database = app.Services.GetRequiredService<SqliteDatabase>();
        await StoragePauseUiTests.FillDiskAsync(database);

        model.Name = new string('n', 20_000);
        model.Model = "another-model";
        model.ApiKey = "sk-test-2";
        Exception? thrown = null;
        try { await model.SaveCommand.ExecuteAsync(null); }
        catch (Exception e) { thrown = e; }
        await StoragePauseUiTests.FreeDiskAsync(database);
        Assert.IsType<StorageWriteException>(thrown);
        window.Close();
    }
}
```

Command: dotnet test tests/Mindweave.Desktop.Tests --filter "FullyQualifiedName~ReproUnhandledCommandTests" --logger "console;verbosity=detailed"

Output (tail):
```
[xUnit.net 00:00:00.92]   Starting:    Mindweave.Desktop.Tests
[xUnit.net 00:00:02.57]   Finished:    Mindweave.Desktop.Tests
  Passed Mindweave.Desktop.Tests.ReproUnhandledCommandTests.Saving_model_settings_on_a_full_disk_throws_out_of_the_command [920 ms]
  Passed Mindweave.Desktop.Tests.ReproUnhandledCommandTests.Rename_on_the_page_while_storage_is_paused_throws_out_of_the_command [165 ms]

Test Run Successful.
Total tests: 2
     Passed: 2
```
The tests assert the faulty behaviour, so a pass means the defect is present. The results are deterministic (no timing dependence in the assertions). Worktree restored: test file removed, build output cleaned, back on branch worktree-wf_5c3b45e2-023-56 with clean git status.
```

</details>

### errors-2. Startup has no failure path after the database opens: a read-only database file or a full disk crashes every launch with no UI

- Final: **high** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Desktop/Services/AppStartup.cs:62`; effort S

**Evidence.** App.axaml.cs:47 starts the app with Dispatcher.UIThread.Post(async () => await ...RunAsync()), an async-void lambda. AppStartup.RunAsync (62-71) has no try/catch. StartupCoordinator.ContinueAfterOpenAsync runs a write, conversations.RecoverUnfinishedAsync (StartupCoordinator.cs:72; SqliteConversationStore.cs:235-247, an unconditional UPDATE), and then workspaces.ListAsync (78), both unguarded; only the last-location read is wrapped (85-95).

OpenExisting (SqliteDatabase.cs:460-491) returns Ready after reading PRAGMAs and quick_check and never tests that the file is writable. SQLite's READWRITE open mode silently falls back to read-only for a write-protected file.

Verified with a stand-alone probe using Microsoft.Data.Sqlite 10.0.13: on a 0444 WAL database, PRAGMA user_version and quick_check succeed, then the same UPDATE fails with 'SQLite Error 8: attempt to write a readonly database' even with zero matching rows. StorageFailures maps code 8 to StorageWriteException(ReadOnly), which nothing on this path catches. No test covers a read-only database file at startup.

**Failure scenario.** The database file is read-only (data directory copied back from a backup or another machine with read-only permissions, the Windows read-only attribute, a volume mounted read-only), or the disk is full when the app starts after an abnormal exit. OpenAsync reports Ready, RecoverUnfinishedAsync throws, the exception leaves the async-void startup lambda, and the process exits while the start page is showing. This repeats on every launch. The recovery page, which exists for exactly this class of problem and has an IoError explanation about disk space and permissions, is never shown.

**Recommendation.** Wrap the post-open part of startup: catch StorageWriteException/SqliteException in ContinueAfterOpenAsync and return NeedsRecovery with an IoError or ReadOnly status and the detail. Better, make OpenExisting prove writability (for example a BEGIN IMMEDIATE / ROLLBACK) so the condition is classified during open. Wrap the Post lambda in App.axaml.cs so any remaining startup exception lands on the recovery page instead of ending the process. Add a startup test with a read-only database file.

**Reproduction attempt: partially confirmed, medium.** I could not refute the core defect: the code is as cited and no guard, handler, test or documented decision covers it. Severity is overstated, and two of the listed triggers do not hold as written.

**What holds (read-only database file):**
- `App.axaml.cs:47` posts `async () => await ...RunAsync()` to the dispatcher with no try/catch.
- `AppStartup.RunAsync` (62-71) has no catch; `StartupCoordinator.StartAsync` goes straight into `ContinueAfterOpenAsync`.
- `ContinueAfterOpenAsync` calls `RecoverUnfinishedAsync` (line 72) and `workspaces.ListAsync` (78) unguarded; only `GetLastAsync` is wrapped.
- `RecoverUnfinishedAsync` is an unconditional pair of UPDATEs through `database.WriteAsync`; the write queue opens a transaction and rethrows via `StorageFailures.ForCaller`, which maps code 8 to `StorageFailureKind.ReadOnly`.
- `OpenExisting` returns Ready at the latest version after only `application_id`, `user_version` and `quick_check`; nothing tests writability. The connection uses `SqliteOpenMode.ReadWrite`.
- I reproduced the SQLite behaviour independently (Python sqlite3, SQLite 3.54.0, in the scratchpad): on a 0444 WAL database opened with `mode=rw`, `user_version` and `quick_check` succeed, then `BEGIN IMMEDIATE` plus a zero-row UPDATE fails with "attempt to write a readonly database".
- There is no dispatcher-level handler anywhere in `src`. `Program.cs:36-37` only logs `AppDomain.UnhandledException` and `UnobservedTaskException`. `AppHost.Run` has only a `finally`, so the exception ends the UI loop and the process.
- No test opens a read-only database file at startup. `docs/design/s6-wrapup.md:182` lists "只读文件造成的 I/O 错误" as not done, so this is an acknowledged test gap, not an accepted behaviour.

**What is overstated:**
- **Disk full:** not demonstrated. On a full disk a zero-row UPDATE in WAL mode writes no frames and most likely succeeds. It would fail only when rows actually need interrupting (after an abnormal exit mid-generation), and SQLite may already fail earlier during open/WAL recovery, which `OpenAsync` classifies as IoError/Unreadable. This is a narrow, unverified sub-case.
- **Read-only volume:** would not reach this path. `SingleInstance.TryAcquire` runs first in `Program.Main` and does `Directory.CreateDirectory` plus a ReadWrite `FileStream` on the lock file; `UnauthorizedAccessException` is not caught there (only `IOException`). That is a different, earlier crash.
- **"Async-void lambda":** imprecise but the outcome is the same; the exception is rethrown on the dispatcher and ends the loop.

**Severity:** medium, not high. The trigger is an uncommon environmental condition (database file itself read-only while the directory is writable: restored from a backup with read-only bits, or the Windows read-only attribute). There is no data loss or corruption, and the crash is logged. It is still a deterministic crash on every launch with no UI and no user-visible explanation, bypassing a recovery page built for this class of problem, which is a real gap at a production quality bar.

The recommendation is sound: classify writability during open, or catch `StorageWriteException` in `ContinueAfterOpenAsync` and return `NeedsRecovery`, and guard the posted lambda.

*Corrections to the finding.* 1. Severity should be medium, not high: uncommon trigger, no data loss, crash is logged.
2. The disk-full trigger is unverified and probably wrong for the common case: a zero-row UPDATE in WAL mode writes nothing. It can fail only when unfinished rows exist, and open may classify the condition first.
3. A volume mounted read-only does not reach this path: `SingleInstance.TryAcquire` (directory create plus ReadWrite lock file, runs before the database opens) fails first with an uncaught `UnauthorizedAccessException` or exits with code 2. That is a separate startup defect.
4. "The process exits while the start page is showing" is inferred from the code (no dispatcher handler, `AppHost.Run` has no catch), not observed by running the app.
5. `Program.cs` does log the crash through `AppDomain.UnhandledException`, so the failure is not silent in the logs, only in the UI.
6. The recovery page's suitability is slightly overstated: `DatabaseOpenStatus` has IoError but no ReadOnly member (ReadOnly exists only on `StorageFailureKind`), so the fix needs a small mapping or a new status.

<details><summary>Reproduction</summary>

Stand-alone Python sqlite3 probe (SQLite 3.54.0) in a temporary scratchpad directory, since removed. Nothing was built or run in the repository, and the app itself was not launched, so the process exit is inferred from the code path, not observed.

Steps: create a WAL database with a `message(state, revision)` table and `user_version=10`, close it, `chmod 0444`, reopen with `file:...?mode=rw`, then run the PRAGMAs and `BEGIN IMMEDIATE; UPDATE message SET state='i' WHERE state IN ('pending'); COMMIT`.

Output:
```
['t.db']
3.54.0 [(10,)] [('ok',)] [('wal',)]
ERR attempt to write a readonly database
```

So `user_version` and `quick_check` succeed on the read-only file and the zero-row write then fails with SQLITE_READONLY, matching the auditor's Microsoft.Data.Sqlite result. The disk-full variant was not reproduced.

</details>

**Reproduction attempt: confirmed, high.** Reproduced with a focused xUnit test on track/claude-auto at 1ba95c9 (macOS), using the real SqliteDatabase, stores and StartupCoordinator.

1. With mindweave.db marked read-only (File.SetAttributes ReadOnly, which is 0444 on Unix and the read-only attribute on Windows), SqliteDatabase.OpenAsync returns Status=Ready with no detail. OpenExisting never tests writability.
2. StartupCoordinator.StartAsync then throws StorageWriteException ("SQLite Error 8: attempt to write a readonly database") from RecoverUnfinishedAsync at StartupCoordinator.cs:72, on a fresh database with zero matching rows. It never returns NeedsRecovery.

The rest of the chain is confirmed by code reading, not by a test:
- AppStartup.RunAsync has no try/catch.
- App.axaml.cs launches it with Dispatcher.UIThread.Post(async () => await ...RunAsync()), an async-void lambda.
- No Dispatcher.UIThread.UnhandledException handler exists anywhere in src. The only handlers are AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException in Program.cs:36-37, which only log. AppHost.Run has no catch (finally only).
- So the exception is rethrown on the dispatcher, the UI loop ends and the process dies with only the start page shown, on every launch. I did not run the Desktop app to watch the exit.

Not verified: the disk-full variant. In WAL mode an UPDATE that matches no rows writes nothing, so a full disk would only fail this step when unfinished answers or tasks exist (the finding does say "after an abnormal exit"). It is plausible but narrower than the read-only case.

Severity stays high: a recoverable environmental condition becomes a permanent crash loop with no UI, while the recovery page built for it is bypassed. It causes no data loss, and the trigger is uncommon. TRACK.md does not list this as a known finding.

The worktree is restored: test file deleted, original branch checked out, git status clean, nothing committed.

*Corrections to the finding.* - File paths: SqliteDatabase.cs and SqliteConversationStore.cs are in src/Mindweave.Infrastructure/Sqlite/, not a Storage/ folder. The cited line numbers are correct.
- The disk-full variant is not demonstrated and is narrower than stated. In WAL mode the recovery UPDATE writes nothing when no rows match, so a full disk fails startup only when unfinished answers or tasks actually exist. The read-only case fails unconditionally.
- "The process exits" is inferred from code (no Dispatcher.UnhandledException handler, AppHost.Run has no catch). It was not observed by running the app.
- The recommendation should also cover AppStartup.RetryAsync, RestoreAsync and RestoreInPlaceAsync, which share ContinueAfterOpenAsync. Pressing Retry or Restore on the recovery page would hit the same unguarded path through a command handler.

<details><summary>Reproduction</summary>

File added temporarily (now deleted): tests/Mindweave.Infrastructure.Tests/Repro/ReadOnlyDatabaseStartupReproTests.cs

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Repro;

public sealed class ReadOnlyDatabaseStartupReproTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();

    public void Dispose()
    {
        if (File.Exists(_dir.Paths.DatabasePath))
            File.SetAttributes(_dir.Paths.DatabasePath, FileAttributes.Normal);
        _dir.Dispose();
    }

    private static StartupCoordinator Coordinator(SqliteDatabase database) =>
        new(database, new SqliteWorkspaceStore(database), new SqliteConversationStore(database), new SystemClock(),
            new SqliteLocationStore(database, new SystemClock()), NullLogger<StartupCoordinator>.Instance);

    private async Task MakeReadOnlyDatabaseAsync()
    {
        await using (var first = _dir.Database())
        {
            Assert.True((await first.OpenAsync(TestContext.Current.CancellationToken)).IsReady);
        }
        File.SetAttributes(_dir.Paths.DatabasePath, FileAttributes.ReadOnly);
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"files: {string.Join(", ", Directory.GetFiles(_dir.Paths.DataDirectory).Select(Path.GetFileName))}; attrs={File.GetAttributes(_dir.Paths.DatabasePath)}");
    }

    [Fact]
    public async Task Open_reports_Ready_for_a_read_only_database_file()
    {
        await MakeReadOnlyDatabaseAsync();
        await using var database = _dir.Database();
        var result = await database.OpenAsync(TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper!.WriteLine($"OpenAsync: {result.Status} detail={result.ErrorDetail}");
        Assert.True(result.IsReady); // the defect's first half: open does not notice
    }

    [Fact]
    public async Task Startup_on_a_read_only_database_file_should_lead_to_recovery_not_throw()
    {
        await MakeReadOnlyDatabaseAsync();
        await using var database = _dir.Database();
        var ex = await Record.ExceptionAsync(() => Coordinator(database).StartAsync(TestContext.Current.CancellationToken));
        TestContext.Current.TestOutputHelper!.WriteLine($"StartAsync threw: {ex}");
        Assert.Null(ex); // expected behaviour: an outcome (NeedsRecovery), never an exception
    }
}
```

Command: dotnet test tests/Mindweave.Infrastructure.Tests --filter "FullyQualifiedName~ReadOnlyDatabaseStartupReproTests" --logger "console;verbosity=detailed"

Output (trimmed):
```
Passed ...Open_reports_Ready_for_a_read_only_database_file [220 ms]
 files: mindweave.db; attrs=ReadOnly
 OpenAsync: Ready detail=

Failed ...Startup_on_a_read_only_database_file_should_lead_to_recovery_not_throw [17 ms]
 Assert.Null() Failure: Value is not null
 Actual: Mindweave.Application.Storage.StorageWriteException: SQLite Error 8: 'attempt to write a readonly database'. (extended code 8)
  ---> Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 8: 'attempt to write a readonly database'.
    at Mindweave.Infrastructure.Sqlite.SqliteCommands.Execute(...) in .../Sqlite/SqliteCommands.cs:line 31
    at Mindweave.Infrastructure.Sqlite.SqliteConversationStore.<RecoverUnfinishedAsync>b__0(...) in .../Sqlite/SqliteConversationStore.cs:line 239
    at Mindweave.Infrastructure.Sqlite.SqliteWriteQueue.WorkItem`1.Run(...) in .../Sqlite/SqliteWriteQueue.cs:line 95
    --- End of inner exception stack trace ---
    at Mindweave.Application.Startup.StartupCoordinator.ContinueAfterOpenAsync(...) in .../Startup/StartupCoordinator.cs:line 72
    at Mindweave.Application.Startup.StartupCoordinator.StartAsync(...) in .../Startup/StartupCoordinator.cs:line 39

Test Run Failed. Total tests: 2  Passed: 1  Failed: 1
```

The result is deterministic, not timing-dependent. The process-exit step (async-void lambda with no dispatcher handler) was verified by code reading only, not by running the Desktop app.
```

</details>

### errors-3. Generation runners use closed catch lists; an unexpected exception leaves the answer permanently 'generating' until restart

- Final: **high** (confirmed); auditor said high
- Where: `src/Mindweave.Application/Conversations/AnswerRunner.cs:104`; effort M

**Evidence.** AnswerRunner.RunAsync catches only OperationCanceledException and ChatClientException around the stream (136-146), and only OperationCanceledException, ContextCompressionException and StorageAdmissionRefusedException around compression (73-87). The only backstop is GenerationCoordinator.RunAsync (530-535), which logs 'Generation task crashed' and frees the slot. FinishAsync is never called on that path, so the live answer stays Streaming and the message and task rows stay streaming/running.

Exceptions that verifiably escape:
(1) ChatCompletionsClient.ThrowIfFailedAsync line 283 (JsonNode.Parse(text)?["error"]?["message"]) and Parse lines 244-249 catch only JsonException. A stand-alone probe confirmed that an error body of {"error":"model not found"} (string error, common with gateways and local servers), an array body [{"error":{...}}], or a bare JSON string all throw InvalidOperationException ('The node must be of type JsonObject'); {"choices":{}} throws the same from AsArray().
(2) ContextCompressor.PrepareAsync line 84 (summaries.AddAsync) and line 71 (ListAsync) throw StorageWriteException/SqliteException; these are neither caught nor reported to StorageHealth.
(3) AutomaticDecompression = All (ChatCompletionsClient.cs:37) can raise InvalidDataException, which derives from SystemException, not IOException, so ReadAsync (211-230) does not wrap it.

Also on this path the 'using var checkpointStop' is disposed without being cancelled, so CheckpointLoopAsync keeps running forever.

RunSummaryAsync has the same shape (TaskRunners.cs:135-150): its task row stays 'running', and SummaryService.cs:161 then refuses every new summary with SummaryInProgress. RunTitleAsync (44-73) likewise.

**Failure scenario.** The user configures an OpenAI-compatible endpoint whose error responses carry "error" as a string or wrap the body in an array, and sends a question with a wrong key or model name. The service answers HTTP 4xx. InvalidOperationException escapes and the run 'crashes'. The message shows as generating forever with no error text. Stop does nothing (GenerationCoordinator.StopAsync finds it neither queued nor running). Every further question in that conversation is rejected as 'answer in progress' because the stored row is still streaming (SqliteConversationStore.cs:283). Only restarting the app clears it. The same test from the settings page (ListModelsCoreAsync line 132) throws past ModelSettingsViewModel.RunAsync and crashes the app.

Second trigger: the disk fills while older context is being summarized for a long conversation. Same stuck answer, and storage is not paused because the failure was never reported.

**Recommendation.** Give every runner a terminal fallback: in AnswerRunner.RunAsync wrap the body in catch (Exception ex) that cancels the checkpoint loop and calls FinishAsync(work, Failed, an Internal/InvalidResponse kind, ex.Message); report a StorageWriteException to StorageHealth first. Do the same in RunSummaryAsync, RunNamingAsync and RunTitleAsync (FinishTaskAsync as Failed). In ChatCompletionsClient, parse defensively (check 'node is JsonObject', 'error is JsonObject') and convert InvalidOperationException, InvalidDataException and UriFormatException to ChatClientException(InvalidResponse/Network). Add tests with a string-valued and an array-wrapped error body, and with a failing context-summary store.

**Skeptic: confirmed, high.** I could not refute this finding; every cited piece of code exists and behaves as claimed. Nothing was executed (read-only audit), so the JSON exception behaviour rests on reading the code and on System.Text.Json's documented semantics, not on a run.

**The structural defect is real.**
- `AnswerRunner.RunAsync` catches only `OperationCanceledException`, `ContextCompressionException` and `StorageAdmissionRefusedException` around compression, and only `OperationCanceledException` and `ChatClientException` around the stream.
- The checkpoint cancel and `FinishAsync` sit after the try block with no `finally`, so any other exception skips them.
- The only backstop is `GenerationCoordinator.RunAsync`, which logs "Generation task {TaskId} crashed" and removes the task from `_running`. It writes no terminal state, calls no `LiveAnswer.Finish`, and raises no `AnswerEnded`/`AnswerFinished`.
- `GenerationRequests.StreamAsync` is a bare pass-through to the client, so there is no intermediate guard.

**The consequences follow from the code.**
- Stop is a no-op: `StopAsync` finds the answer neither queued nor in `_running`.
- The conversation is locked: `ThrowIfUnfinished` throws `AnswerInProgressException` while a message row is still `pending` or `streaming`.
- The checkpoint loop leaks: `using var checkpointStop` is disposed without being cancelled, so `CheckpointLoopAsync` keeps waking every interval for the life of the process (cheap, but never ends).

**The escaping exceptions are real.**
- `ThrowIfFailedAsync` evaluates `JsonNode.Parse(text)?["error"]?["message"]` and catches only `JsonException`. The string indexer on a `JsonValue` or `JsonArray` throws `InvalidOperationException`, so `{"error":"..."}` and array bodies escape.
- `Parse` has the same flaw on stream events: `error["message"]` on a string-valued error, and `AsArray()` on a non-array `choices`.
- The outer `StreamAsync` wrapper only redacts `ChatClientException`; `SendAsync` rethrows everything else.
- `ReadAsync` catches `IOException` and `HttpRequestException` only, so `InvalidDataException` from decompression would escape. I did not confirm this from the type hierarchy by running anything; it is the weakest of the three triggers.
- `ContextCompressor.PrepareAsync` calls `summaries.ListAsync` and `summaries.AddAsync` with no catch. `AddAsync` goes through `database.WriteAsync`, and neither the compressor nor `PrepareCompressedAsync` reports to `StorageHealth`.

**The other runners have the same shape.**
- `RunSummaryAsync` has a closed catch list (only the `work.Save` step has a catch-all). Its task row stays `running`, and `SummaryService` then rejects new summaries with `SummaryInProgress`.
- `RunTitleAsync` is the same, though titles are best-effort so the impact there is small.

**The settings-page crash is real.**
- `ModelSettingsService.TestAsync` catches only `ChatClientException` and `CredentialUnavailableException`.
- `ModelSettingsViewModel.RunAsync` catches only `ModelSettingsException`, `ChatClientException` and `CredentialUnavailableException`.
- The commands are plain `[RelayCommand]` with no `FlowExceptionsToTaskScheduler` (grep found none), and `Program.cs` only logs unhandled exceptions. An `InvalidOperationException` therefore reaches the UI thread unhandled.

**No mitigation or design decision covers it.** `TRACK.md` and `docs/design` have no entry accepting this behaviour. The CA1031 comment on the coordinator ("A failed run must never take down the scheduler; it is logged and its slot freed") shows the catch-all was meant to protect the scheduler, not to finish the task. I found no test for a runner crash or for a string/array error body.

**Severity stays high.** The trigger needs a non-conforming error body, but arbitrary OpenAI-compatible endpoints are the product's core use case and a wrong key or model name is a first-run path. The result is a silently stuck conversation or an app crash. Mitigating factors: no data loss, and a restart is expected to clear the row.

*Corrections to the finding.* - The settings-page crash has two entry points, not just `ListModelsCoreAsync`. The "Test" button goes through `ModelSettingsService.TestAsync` → `StreamAsync` → `ThrowIfFailedAsync` and escapes the same way. `ListModelsCoreAsync` additionally has unguarded `AsArray()` / `GetValue<string>()` on a 2xx body with an unexpected shape.
- "Only restarting the app clears it" is likely but not verified. I did not read the startup recovery code; only the existence of `interrupted` states suggests it.
- "Common with gateways and local servers" is somewhat overstated. Mainstream OpenAI-compatible services, including Ollama's /v1 endpoint, return an object-valued `error`. String-valued errors come from native or non-conforming proxies, so the trigger is realistic but not the common case.
- In the disk-full scenario the store's exception may be a raw `SqliteException` rather than `StorageWriteException`, depending on how `database.WriteAsync` maps it (not traced). Either way it is uncaught and unreported to `StorageHealth` on this path.
- The leaked checkpoint loop is a minor timer leak: the state stays Streaming with an unchanged revision, so it wakes and does nothing rather than writing.
- `RunTitleAsync` impact is low: titles are best-effort and only a task row is left `running`.
- Minor line-number drift only (the coordinator catch is around lines 530-535 as cited; `SummaryService` check is near 161).

**Reproduction attempt: confirmed, high.** Reproduced with a focused xUnit test against track/claude-auto at 1ba95c9 (the worktree was created from main, which has no product code, so I detached it to that commit for the run and restored it afterwards).

Client level: ChatCompletionsClient.StreamAsync throws System.InvalidOperationException, not ChatClientException, for all six malformed bodies tried: HTTP 404 {"error":"model not found"}, HTTP 401 array-wrapped body, HTTP 500 bare JSON string, and 200 SSE events {"error":"overloaded"}, {"choices":{}} and [1]. ListModelsAsync throws the same for the three non-2xx bodies.

End to end (real ChatCompletionsClient, real GenerationCoordinator, real SQLite stores via the existing ChatHarness; HTTP 404 with a string "error"): after the coordinator is fully idle (UnfinishedCount=0) the stored message is still Streaming with no error, the generation_task row is still 'running' and the live answer is still Streaming. StopAsync changes nothing. A follow-up question is rejected with AnswerInProgress and Retry is rejected with NotRetryable. Nothing changes 1.5 s later. So within the session the conversation is unusable and the user sees no error text. This does not depend on timing: the wait is on coordinator idleness, and the state is terminal-by-omission.

By code reading only (not exercised by a test):
- The other runners have the same closed catch lists (TaskRunners.cs RunTitleAsync 44-73, RunSummaryAsync ~135-150), and the only backstop is GenerationCoordinator.RunAsync's catch-all, which logs and frees the slot without a terminal write.
- ContextCompressor.PrepareAsync calls summaries.ListAsync/AddAsync with no catch for storage exceptions, and AnswerRunner's compression block catches only OperationCanceled, ContextCompressionException and StorageAdmissionRefusedException, so the disk-full trigger is credible.
- 'using var checkpointStop' is disposed without cancelling on the exception path, so CheckpointLoopAsync never ends (its token can no longer fire and the live state stays Streaming).
- AutomaticDecompression=All is set and ReadAsync catches only OperationCanceled, IOException and HttpRequestException, so InvalidDataException would escape; I did not trigger it.

Severity stays high: a plausible real-world input (gateways and local servers commonly return a string-valued "error") makes a conversation permanently stuck for the session with no error shown and no working Stop/Retry/Ask.

*Corrections to the finding.* Mostly accurate; a few points need narrowing.
- Settings page: the claim cites ListModelsCoreAsync line 132 for "the same test from the settings page". The Test button actually goes through ModelSettingsService.TestAsync -> client.StreamAsync (which I showed throws InvalidOperationException); model discovery goes through ListModelsAsync (also shown to throw for non-2xx bodies). Neither wrapper catches it, so the exception does leave ModelSettingsViewModel.RunAsync, but I did not run the UI, so "crashes the app" is plausible, not demonstrated.
- ListModelsAsync is only affected for non-2xx responses; for 2xx bodies with the wrong shape my cases returned a proper ChatClientException ("The model list is not valid JSON").
- "Only restarting the app clears it" was not tested (I did not exercise startup recovery); within the session it is confirmed stuck.
- Besides AskAsync being rejected, Retry is also rejected (NotRetryable), which the finding did not mention and makes it slightly worse.
- Not reproduced by test, code reading only: the disk-full/compression trigger, the InvalidDataException decompression trigger, the summary/title runner variants (and the SummaryInProgress consequence), and the never-ending checkpoint loop.

<details><summary>Reproduction</summary>

Temporary file tests/Mindweave.Infrastructure.Tests/Faults/ReproStuckAnswerTests.cs (deleted afterwards). Essential parts:

```csharp
public static TheoryData<string, int, string, string> Bodies => new()
{
    { "string error, 404", 404, "application/json", "{\"error\":\"model not found\"}" },
    { "array body, 401", 401, "application/json", "[{\"error\":{\"message\":\"bad key\"}}]" },
    { "bare string, 500", 500, "application/json", "\"upstream down\"" },
    { "sse string error, 200", 200, "text/event-stream", "data: {\"error\":\"overloaded\"}\n\n" },
    { "sse choices object, 200", 200, "text/event-stream", "data: {\"choices\":{}}\n\n" },
    { "sse array event, 200", 200, "text/event-stream", "data: [1]\n\n" },
};

[Theory, MemberData(nameof(Bodies))]
public async Task Client_reports_odd_bodies_as_ChatClientException(string name, int status, string type, string body)
{
    var client = new ChatCompletionsClient(new HttpClient(new Handler(status, type, body)), new ChatClientOptions());
    Exception? thrown = null;
    try { await foreach (var _ in client.StreamAsync(new ChatRequest(Model, [new ChatTurn(ChatRole.User, "hi")]), "gw-key-0123456789", Ct)) { } }
    catch (Exception ex) { thrown = ex; }
    output.WriteLine($"[{name}] stream threw: {thrown?.GetType().FullName}: {thrown?.Message}");
    Assert.IsType<ChatClientException>(thrown);
}

[Theory, MemberData(nameof(Bodies))]
public async Task ListModels_reports_odd_bodies_as_ChatClientException(...)  // same handler for /models; asserts ChatClientException when status != 200

[Fact]
public async Task String_error_body_ends_the_answer_as_failed_end_to_end()
{
    using var http = new HttpClient(new Handler(404, "application/json", "{\"error\":\"model not found\"}"));
    await using var h = await ChatHarness.StartAsync(client: new ChatCompletionsClient(http, new ChatClientOptions()));
    var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
    var id = sent.Answer!.Value;
    await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0, 30000);
    // ... prints stored/live/task state, then StopAsync, AskAsync, RetryAsync, waits 1.5 s ...
    Assert.Equal(MessageState.Failed, later.State);
}
// Handler: HttpMessageHandler returning the given status/content-type/body (and {"data":[]} for /models unless everywhere:true)
```

Actual output (dotnet test ... --filter FullyQualifiedName~ReproStuckAnswerTests):

```
[string error, 404] / [array body, 401] / [bare string, 500] stream threw: System.InvalidOperationException: The node must be of type 'JsonObject'.   (all six stream cases FAILED: Expected ChatClientException, Actual InvalidOperationException)
[sse string error, 200] stream threw: System.InvalidOperationException: The node must be of type 'JsonObject'.
[sse choices object, 200] stream threw: System.InvalidOperationException: The node must be of type 'JsonArray'.
[sse array event, 200] stream threw: System.InvalidOperationException: The node must be of type 'JsonObject'.
[string error, 404] list threw: System.InvalidOperationException: The node must be of type 'JsonObject'.   (FAILED; same for array body 401 and bare string 500)
[sse ... , 200] list threw: Mindweave.Application.Ai.ChatClientException: The model list is not valid JSON.   (3 PASSED)

String_error_body_ends_the_answer_as_failed_end_to_end [FAIL]
  Assert.Equal() Failure: Expected: Failed  Actual: Streaming
  coordinator UnfinishedCount=0, UnfinishedAnswerCount=0
  stored message state=Streaming error=
  task row state=running
  live state=Streaming
  HasUnfinishedAnswer=False
  after Stop: stored state=Streaming, task=running
  follow-up accepted=False rejection=AnswerInProgress
  retry accepted=False rejection=NotRetryable
  1.5 s later: stored state=Streaming, task=running

Test Run Failed. Total tests: 13  Passed: 3  Failed: 10  Total time: 2.7198 Seconds
```

Cleanup: test file deleted, worktree switched back to its original branch, build outputs removed, git status clean, nothing committed.
```

</details>

### errors-4. Every SQLite error at open is reported as a damaged database with an offer to restore; the recovery-page restore does not verify the backup first

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteDatabase.cs:406`; effort M

**Evidence.** OpenCore lines 401-410: after the 3-second transient window, every SqliteException becomes DatabaseOpenStatus.Unreadable, including SQLITE_BUSY/LOCKED (5/6), IOERR (10), FULL (13), READONLY (8) and CANTOPEN (14). StorageFailures.Classify already distinguishes these but is not used here. UiText.Storage.cs:31 and 45 then show the heading 'cannot read data' and a text saying the database file is damaged or not a valid database, suggesting a restore from backup.

The recovery-page restore, SqliteDatabase.RestoreAsync (74-97), calls _backups.ReplaceDatabaseWith(backup) directly; the in-app path first runs _backups.Check (131-136) and puts the old database back if the copy cannot be opened (165-178). The recovery path does neither. The Storage page lists the 'replaced' files only for deletion (StorageDetailsViewModel.cs:69-72, 99-107).

TRACK.md (2026-10-28) records this hazard for the post-crash -shm case and added the 3-second retry; the misclassification past that window remains.

**Failure scenario.** Another program holds the database for more than a few seconds at launch (a SQLite browser with an open write transaction, a backup or sync tool, antivirus on Windows), or the disk is full so WAL recovery cannot write. The user is told the database is damaged and is offered backups. If they confirm (rename succeeds on macOS/Linux even while the file is open elsewhere), the healthy current database is renamed to mindweave.db.replaced-<time> and an older backup becomes the data. Everything since that backup disappears from the app, and the only in-app action on the renamed file is delete. If the chosen backup is itself corrupt, the user ends on the recovery page again with the original under a 'replaced' name and no in-app way back.

**Recommendation.** Classify at open with StorageFailures.Classify: Busy/Locked gives a 'the data file is in use by another program, close it and retry' page with no restore offer; Full/ReadOnly/IoError gives the IoError page; only CORRUPT/NOTADB and a failed quick_check give Unreadable. Make the recovery-page restore share RestoreInPlaceCore's steps (Check before the swap, PutBack when the copy does not open). Consider letting the Storage page restore a 'replaced' database.

**Skeptic: confirmed, medium.** I could not refute either half; the code says what the finding claims, with one example scenario overstated.

**Misclassification at open (confirmed).** `OpenCore` turns every `SqliteException` into `Unreadable` once the retry window is over. `IsTransient` covers only codes 5, 6 and 10, so CANTOPEN (14), READONLY (8), PERM (3) and FULL (13) are not retried at all and become `Unreadable` immediately. The `IOException`/`UnauthorizedAccessException` catch that yields `IoError` never fires for SQLite-level failures, because Microsoft.Data.Sqlite raises `SqliteException` for them. `StorageFailures.Classify` exists and separates Full, Busy, ReadOnly, IoError and Corrupt, but it is used only on the write path.

**What the user sees (confirmed).** The `Unreadable` heading is "无法读取数据" and the explanation states the file is damaged or not a valid database and suggests restoring from backup. The recovery view shows the backup list for any status whenever backups exist. The only mitigation is the detail card, which shows the raw SQLite message and extended code.

**Unverified recovery-page restore (confirmed).** `RestoreAsync` calls `ReplaceDatabaseWith` and then `OpenCore`, with no `Check` beforehand and no `PutBack` if the copy fails to open. `RestoreInPlaceCore` does both. After a bad restore, `ContinueAfterOpenAsync` returns `NeedsRecovery` with `ListBackups()` only, so the renamed original is not offered. On the Storage page, `CanRestore` requires `Entry.Backup`, so "replaced" leftovers can only be deleted.

**No design decision makes this intended.** `docs/design/stage-reports.md:48` and `:65` only record that the startup recovery page was left unchanged when in-app restore was added. `docs/design/s0-skeleton-storage.md:44` specifies the recovery page for a failed `quick_check`, meaning real corruption. TRACK.md line 145 itself calls the misdirected restore offer "错误且有风险" and fixed only the transient case. The only test for `Unreadable` at open uses a garbage file; none covers busy, read-only or full.

**Severity stays medium.** The triggers are uncommon and nothing is deleted: the original survives under a "replaced" name and the confirmation text says so. But the app asserts corruption for non-corruption conditions and steers the user to roll back a healthy database, with delete as the only in-app action on the original. That fits medium for a production quality bar.

*Corrections to the finding.* - **The "SQLite browser with an open write transaction" trigger is overstated.** The database is in WAL mode, and with a current schema version the open only reads (`application_id`, `user_version`, `quick_check`). A foreign writer does not block those reads. If a migration is pending, the busy error surfaces as `BackupFailed` or `MigrationFailed`, not `Unreadable`.
- **The realistic triggers are narrower.** They are: a process holding the file in exclusive locking mode; a Windows sharing violation from antivirus or a backup tool (CANTOPEN or IOERR); a database or `-shm` file or directory that became read-only (READONLY or CANTOPEN); and a full disk or I/O error while WAL recovery writes `-shm`.
- **"After the 3-second window" is imprecise in both directions.** CANTOPEN, READONLY, PERM and FULL are not in `IsTransient`, so they become `Unreadable` immediately. For BUSY, the 5-second busy timeout usually outlasts the 3-second deadline, so there is effectively one attempt.
- **The corrupt-backup branch is low probability.** Backups are verified with `quick_check` when created (`SqliteBackupFiles.cs:218-229`), so a corrupt one requires later bit rot or outside modification.
- **Nothing is deleted on any of these paths.** The original stays under the "replaced" name, the confirmation text says so, the detail card shows the SQLite message and extended code, and the page has an "open data folder" button. Recovery is possible by hand but not in the app, and the Storage page offers to delete the leftover.

### errors-5. Read failures have no policy: page loads fail silently, and reloads awaited inside commands crash the app

- Final: **medium** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/Services/IUiDispatcher.cs:66`; effort M

**Evidence.** SqliteDatabase.ReadAsync (263-289) lets raw SqliteException through; reads are never classified or reported to StorageHealth. Its finally block runs COMMIT (283-286), which can throw and replace the original exception.

SerialRefresh.RunAsync (IUiDispatcher.cs:66-82) has no catch; a throwing refresh also drops a queued '_again' request. Callers discard the task: ConversationViewModel.cs:72 ('_ = _load.Request()') and 89 (Loaded), SummaryViewModel.cs:57/59, ArchivedViewModel.cs:70/72, NetworkHomeViewModel.cs:41/46, ConversationListViewModel.cs:73. ConversationViewModel.LoadAsync (377-423) makes eight unguarded reads. Only the statistics page has a load-failed text (UiText.Statistics.cs:30).

The same reloads are awaited inside command paths without a catch (SummaryViewModel.RunAsync line 499, ArchivedViewModel 120/199), and Composer send runs reads inside AskAsync (ConversationService.cs:249-260); there an exception reaches the dispatcher.

**Failure scenario.** The data directory is on a drive that is ejected or a network or sync folder that becomes unavailable, the file is replaced by a sync client, or an external tool holds an exclusive lock. Opening a conversation shows a page titled 'new conversation' with no messages and no error; the fault surfaces only later as a Critical 'Unhandled exception' log entry when the task is finalized. An already open page silently stops updating. If the user presses Send, or any summary or archive action that reloads, the SqliteException propagates out of the command and the app exits.

**Recommendation.** Decide a read-failure policy. Classify read errors like write errors (a StorageReadException or reuse StorageFailures). Catch in SerialRefresh (or give it an onError callback) and show a page-level 'could not read data, retry' state. Do not let reloads after a command throw past the command. Guard the COMMIT in ReadAsync's finally so it cannot mask the original error.

**Skeptic: partially confirmed, medium.** I could not refute the core of the finding; the code says what is claimed, with a few wrong paths and one overstatement.

**What holds (read in code, not executed):**
- **Reads are unclassified.** `SqliteDatabase.ReadAsync` opens a read-only connection, runs `BEGIN`, calls `work`, and runs `COMMIT` in a `finally`, with no catch. `StorageFailures.Classify` is used only on the write and backup paths. A raw `SqliteException` from a read reaches the caller and `StorageHealth` is never told.
- **The `COMMIT` can mask the original error.** It runs unguarded in the `finally`, so if it throws (for example after SQLite has already rolled back on an I/O error), its exception replaces the one from `work`.
- **`SerialRefresh.RunAsync` has no catch**, and the throttled reload callers discard the task (`_ = _load.Request()` in the Conversation, Summary, Archived and NetworkHome view models). `ConversationViewModel.LoadAsync` makes its reads with no try/catch.
- **A failed first load is silent.** `ConversationView` line 94 discards `RestoreAsync`, which awaits `page.Loaded`, so the fault is unobserved. `DisplayTitle` defaults to `UiText.NewConversation`, so the page shows "新会话" with no messages and no error.
- **The only global handlers are log-only.** `Program.cs` 36-37 registers `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`, both calling `LogCrash`. There is no `Dispatcher.UIThread.UnhandledException` handler and no `FlowExceptionsToTaskScheduler` option anywhere in `src`.
- **Command paths rethrow.** `SummaryViewModel.RunAsync` catches only `SummaryRefusedException` and `StorageWriteException`, then awaits `_load.Request()` outside the try. `ArchivedViewModel` 120 and 199 await the reload after catching only `StorageWriteException`. `Composer.SendAsync` awaits `_send` with no catch, and `ConversationService.AskAsync` does unguarded reads (`CheckAsync`, `GetMessagesAsync`, `GetActiveSnapshotAsync`) with no `SqliteException` catch in the service.
- **The crash follows from library defaults.** With CommunityToolkit.Mvvm 8.4.2 default `[RelayCommand]` options, an exception from an async command is rethrown on the UI synchronization context, which with no dispatcher handler ends the process.

**No design decision covers this.** TRACK.md line 112 and the s6-wrapup design note address write failures only. The s6 note itself says an unhandled command exception was "inferred from the command library's default behaviour, not separately verified". The statistics page and the deletion dialogs show the team does handle read failure where they thought of it; the main pages were not covered.

**Severity stays medium.** The trigger is uncommon for a local single-user SQLite file (ejected or unavailable volume, file replaced by a sync client, a lock held past the 5 s busy timeout). The consequence is a process exit or a silently blank page, not data loss. Writes are guarded and answer checkpoints go through the `StorageHealth` path.

*Corrections to the finding.* 1. **Wrong paths.** `SqliteDatabase.cs` is under `src/Mindweave.Infrastructure/Sqlite/` (not `Storage/`). The view models are under `src/Mindweave.Desktop/ViewModels/Conversations/` and `ViewModels/Pages/`. The cited line numbers are otherwise accurate.
2. **"Only the statistics page has a load-failed text" is overstated.** The conversation and workspace deletion dialogs and `ConversationLifecycleActions` also catch read failures and show `ConversationDeleteCountFailed` / `WorkspaceDeleteCountFailed` / `WorkspaceCountsFailed`. The accurate claim is that none of the main content pages (conversation, sidebar list, summary, archived, network home) has a read-failure state.
3. **"Drops a queued `_again` request" is immaterial.** After a throw `_busy` is reset, so the next `Request()` starts a fresh run; the real loss is that the page stays stale with no error until some later change event.
4. **"The app exits" is inferred, not demonstrated.** It follows from CommunityToolkit `AsyncRelayCommand` defaults plus the absence of a dispatcher-level handler. No test exercises it, and the project's own design note marks the same inference as unverified.
5. **"Surfaces as a Critical log entry when the task is finalized" is weaker than stated.** `UnobservedTaskException` fires only on a finalizer GC, so the entry may be much delayed or never written in a session. The silent failure is therefore somewhat worse for diagnosis than the finding says.
6. **The `COMMIT` masking is real but minor.** It changes which exception is reported, not whether one is.

### errors-6. Failures before the window exists are invisible: the lock path treats any IOException as 'already running' and reports only to stderr

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Infrastructure/Hosting/SingleInstance.cs:46`; effort M

**Evidence.** TryAcquire (42-56) opens the lock file and then calls WriteOwner (131-138: SetLength, Write, Flush). 'catch (IOException) { return null; }' covers both the open and the write, and the stream is not disposed when WriteOwner throws, so the process keeps its own handle and the second attempt (Program.cs:26) must fail too. UnauthorizedAccessException from Directory.CreateDirectory or the open is not caught at all.

Program.cs:29-30 reports the result only with Console.Error.WriteLine and exit code 2; a GUI launch has no console. Program.cs:13-35 (AppPaths.Resolve, TryAcquire, CompositionRoot.Build, including FileLoggerProvider's constructor at FileLoggerProvider.cs:29) runs before the exception handlers on lines 36-37 are installed.

FileLoggerProvider.Write (59-66) swallows only IOException; an UnauthorizedAccessException from opening the log file (read-only logs folder or file) propagates into every logging call site, including catch blocks that are logging a handled failure.

**Failure scenario.** The disk is full: writing the owner PID fails with IOException, the app waits 5 seconds trying to signal a primary that does not exist, prints 'already running ... did not respond' to a console nobody sees and exits. From the user's side the app does not start and gives no reason. With a data directory that is not writable, UnauthorizedAccessException ends the process before logging or the recovery page exists. With only the logs folder read-only, the first Log call inside a catch block throws and turns a handled failure into an unhandled one.

**Recommendation.** Separate 'lock held' (a sharing violation on open) from other I/O failures in TryAcquire, dispose the stream on failure, and make the owner note best effort. Install the unhandled-exception handlers first and wrap the pre-UI section of Main so a failure shows a minimal native or Avalonia error window with the data directory path. Give 'primary not responding' a visible message as well. In FileLoggerProvider catch UnauthorizedAccessException and ObjectDisposedException in Write and in the constructor (fall back to no file logging).

**Skeptic: confirmed, medium.** I could not refute any of the code claims; each one matches the source, and nothing in tests, TRACK.md or the design docs marks the behaviour as intended.

1. **Lock path conflates failures.** `TryAcquire` wraps both the exclusive open and `WriteOwner` in one `try` and returns null on any `IOException`. The stream is a local that is never disposed when `WriteOwner` throws, so the process keeps its own exclusive handle until finalization. `Directory.CreateDirectory` sits outside the `try`, and `UnauthorizedAccessException` (read-only `instance.lock`, non-writable data directory) is not caught anywhere. The comment on `WriteOwner` says "Diagnostic only; correctness rests on the exclusive open", yet a failure there is reported as "another instance owns it".

2. **Only stderr, and no console.** `Program.Main` reports the failure with `Console.Error.WriteLine` and `return 2`. The Desktop project is `OutputType` `WinExe`, so a GUI launch shows nothing. This also deviates from the track's own design: `docs/design/s0-skeleton-storage.md:27` says the second process should show the user "已有实例无响应" before exiting.

3. **Handlers installed late.** The `UnhandledException` and `UnobservedTaskException` handlers are attached on lines 36-37, after `AppPaths.Resolve`, `TryAcquire` and `new AppSession(...)`. The `AppSession` constructor calls `Build()` eagerly, which runs `CompositionRoot.Build` and constructs `FileLoggerProvider` (its `Directory.CreateDirectory` is unguarded). Even once installed, the handlers only log to that same file logger; no window is shown.

4. **Logger can throw.** `FileLoggerProvider.Write` catches only `IOException`. `WriterFor` opens a `FileStream` that throws `UnauthorizedAccessException` on a read-only logs folder or file, and that propagates through `FileLogger.Log` to every call site. `Prune` likewise catches only `IOException`. The rest of the codebase routinely catches `IOException or UnauthorizedAccessException` together (`SqliteDatabase`, `SqliteBackupFiles`, `AtomicFileWriter`), so this is an inconsistency, not a deliberate choice.

**No mitigation found.** `SingleInstanceTests` cover only lock-held, activation and kill-release, with no `WriteOwner` failure or access-denied case. TRACK.md line 120 covers only the Windows delayed-release retry. No decision accepts stderr-only reporting.

**Severity.** Medium holds against the stated industrial bar: startup failures are silent with no diagnostic, and the design-specified user prompt is missing. It is not higher because every trigger is an uncommon environment (non-writable data directory, read-only logs, I/O error on the lock file, primary not answering within 5 seconds) and no data is lost or corrupted.

*Corrections to the finding.* - **Disk-full trigger is weaker than stated.** `WriteOwner` truncates the file and writes about 6 bytes. On NTFS that is typically stored resident in the MFT and may well succeed on a full volume, so disk-full is an unproven trigger. The conflation of "lock held" with other I/O errors is still real for other `IOException` sources (device or network-share errors, path problems).
- **"Second attempt must fail" is slightly too strong.** The leaked `FileStream` could in principle be finalized during the 5-second wait and release the handle. Nothing forces a GC, so in practice the retry does fail.
- **"Primary not responding" is rarer than a hung UI.** The pipe listener runs on a pool thread, so a primary with a frozen UI thread still acknowledges. The silent exit 2 mainly occurs when the primary is shutting down or is more than 5 seconds from starting its listener, or in the misclassified I/O-error case.
- **Logger exception type.** Microsoft.Extensions.Logging wraps provider exceptions, so the logs-folder failure surfaces at call sites as an `AggregateException` containing the `UnauthorizedAccessException`. It still throws, so the claim holds.

### errors-7. The async-void AnswerFinished handler turns a subscriber exception on a worker thread into a process crash; worker-thread events are not isolated

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Application/Conversations/ConversationService.cs:618`; effort S

**Evidence.** Lines 616-618: the comment says 'async void on purpose: an exception from a ConversationChanged subscriber goes to the synchronization context, never back into the generation worker', above 'private async void OnAnswerFinished'. AnswerFinished is raised from AnswerRunner.FinishAsync (line 163) on a pool thread, where SynchronizationContext.Current is null. An exception from an async-void method without a context is rethrown on the thread pool and terminates the process; the main thread's finally in AppHost.Run does not get to flush anything. Without async void the exception would have been caught and logged by GenerationCoordinator.RunAsync (530-535).

Related: StorageHealth raises Changed outside any guard (StorageHealth.cs:246 and 323). If a subscriber throws at line 323, the RecoverAsync task faults with _loopRunning still true, EnsureLoop (250-259) never starts another loop, and the next storage failure pauses the app permanently. GenerationCoordinator.RunAsync's finally (546-547) calls Pump() and RaiseChanged() unguarded, so a throwing subscriber faults Running.Completion, which StopAsync and StopForExitAsync then await.

**Failure scenario.** A ConversationChanged subscriber throws while an answer finishes (a page that is being disposed, a dispatcher post during shutdown, a defect in a new subscriber). Instead of one logged error the process terminates from a pool thread with no final flush; a terminal write still kept in memory is lost. Separately, a subscriber that throws when storage recovers kills the recovery loop, and a later disk-full episode never clears until restart.

**Recommendation.** Make OnAnswerFinished an ordinary handler with a try/catch that logs (the titler call already never throws). Raise worker-thread events through a helper that invokes each subscriber in its own try/catch and logs. In StorageHealth.RecoverAsync, reset _loopRunning in a finally and guard the Changed invocation.

**Skeptic: partially confirmed, low.** The mechanisms are real, but no current subscriber can realistically trigger them, so this is a latent robustness gap rather than a medium defect. Nothing was run; this is from reading the code only.

**What holds up**

- **Async-void crash path.** `OnAnswerFinished` is `async void`, and its comment says a subscriber exception "goes to the synchronization context". It is invoked from `AnswerRunner.FinishAsync` after `ConfigureAwait(false)` awaits, inside `Task.Run(() => RunAsync(running))`, so there is no synchronization context. `ConversationChanged?.Invoke` sits before the first await, but an async-void builder still captures the exception and, with no context, rethrows it on the thread pool. That is an unhandled exception and the process terminates. `Program.cs` only logs in its `UnhandledException` handler; it does not stop termination.
- **The comment's premise is wrong for this call site.** TRACK.md line 95 shows the async void was kept deliberately: a review caught that making the handler synchronous let subscriber exceptions land in the generation thread. So the isolation goal is intended, but the result is a process crash, not isolation. No test covers a throwing subscriber.
- **StorageHealth recovery loop.** `Changed?.Invoke` at line 323 is outside the try, and `_loopRunning` is only reset on the two return paths. A throwing subscriber faults the loop task with `_loopRunning` still true, so `EnsureLoop` never starts another loop. `StopAsync` (line 195) would then rethrow the fault, since it only catches `OperationCanceledException`. The subscriber chain is wide: `GenerationCoordinator` line 54 runs `Pump()` and `RaiseChanged()`, which fans out to UI subscribers.
- **Coordinator finally block.** `Pump()` and `RaiseChanged()` in the `finally` of `RunAsync` are unguarded, so a throwing `Changed` subscriber faults `Running.Completion`. `StopAsync` awaits `Task.WhenAll` of those at line 317 and would throw.

**What is overstated**

- **Likelihood.** Every current `ConversationChanged` subscriber is trivially non-throwing on the raising thread: a `_disposed` check plus `UiThrottle.Request()` (an `Interlocked` flag and a `Task.Delay(...).ContinueWith`), or `Dispatcher.UIThread.Post`. The same holds for the `StorageHealth.Changed` subscribers I checked. The claimed triggers ("a page being disposed", "a dispatcher post during shutdown") do not throw in the code as written. Only a future defective subscriber would trigger this.
- **Data loss.** `AnswerFinished` is raised after the terminal write for that answer has been awaited, so that answer is already persisted. Only other in-flight answers, or writes held in memory during a storage pause, would be lost in a crash.
- **"Without async void it would have been caught and logged."** True, but incomplete: a synchronous throw would skip `_requests.RecordAsync(trace.Finish(...))` in `AnswerRunner` line 150, losing the request record. The recommended fix (try/catch inside the handler) is still correct and avoids both problems.
- **Exit path.** `StopForExitAsync` uses `WhenAny` and never awaits the combined task directly, so the exit path would not throw from a faulted Completion; only `StopAsync` line 317 would.

*Corrections to the finding.* - Severity should be low, not medium: no existing subscriber can realistically throw, so this is a latent hardening gap.
- The answer whose `AnswerFinished` is being raised has already been persisted; "a terminal write still kept in memory is lost" applies only to other concurrent work or writes held during a storage pause.
- The async void is a recorded deliberate choice (TRACK.md:95), not an oversight. The actual defect is that its comment assumes a synchronization context that does not exist on the pool thread.
- A plain synchronous handler would be caught by `RunAsync`, but it would also skip the request-trace `RecordAsync`; the finding omits this.
- `StopForExitAsync` would not throw from a faulted Completion (it uses `WhenAny`); only `StopAsync` line 317 would. Line references 530-535 and 546-547 are approximately right (actual catch 531-536, finally 537-549).

### errors-8. Listing backups on the recovery path can itself throw, replacing the recovery page with a crash

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Sqlite/SqliteBackupFiles.cs:44`; effort S

**Evidence.** StartupCoordinator.cs:67 calls database.ListBackups() unguarded when the database is not ready. SqliteBackupFiles.List (30-50) calls Directory.EnumerateFiles, int.Parse on the version digits, DateTimeOffset.ParseExact on the time stamp (the regex '\d{8}T\d{9}Z' accepts impossible dates such as 99999999T...) and new FileInfo(file).Length, all without a catch. ListLeftovers (114-142) has the same pattern and is called by the Storage page's Refresh (StorageDetailsViewModel.cs:62-69). Prune (165-180) calls List() and catches only IOException/UnauthorizedAccessException, so a FormatException there escapes OpenExisting (SqliteDatabase.cs:519) after a successful migration.

**Failure scenario.** The data directory is unreadable (the very reason OpenCore returned IoError), or the backups folder contains a file a user renamed or copied by hand to something like mindweave-v3-20261340T000000000Z.db. ListBackups throws UnauthorizedAccessException or FormatException, the startup exception is unhandled, and the user gets a crash instead of the recovery page. The same file makes the Storage page throw on open and makes a completed migration report as a crash.

**Recommendation.** Make List and ListLeftovers total: use TryParseExact/TryParse and skip names that do not parse, and catch IOException/UnauthorizedAccessException per file and for the enumeration (return what could be listed, log the rest).

### errors-9. After a save failure at exit the only choices are retry and return; with storage gone for good the app can only be force-quit

- Final: **info** (not verified); auditor said info; already known in TRACK.md
- Where: `src/Mindweave.Desktop/ViewModels/ExitCoordinator.cs:271`; effort S

**Evidence.** SaveAndReleaseAsync (255-285) sets Stage = Failed whenever SaveEverythingAsync returns false. The Failed stage exposes only RetryCommand and ReturnCommand (195-205). UiText.cs:96 says the app will not exit before the write succeeds. This follows architecture section 12.3 step 5, so it is a recorded decision, not an oversight.

**Failure scenario.** The volume holding the data directory is ejected or fails permanently while a draft or a kept terminal write is pending. The user cannot quit through the app at all and must kill the process, which skips the orderly release.

**Recommendation.** Consider a third, explicit choice in the Failed state after at least one retry: quit anyway, naming exactly what will be lost, and optionally offering to save the kept text to a file elsewhere. This needs a user decision on main since it changes section 12.3.

