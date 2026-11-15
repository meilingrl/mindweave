# Observability and diagnostics — grade C

The storage and AI-request core is well instrumented: a per-request ai_request trace, source-side redaction with tests, a flush-per-entry file log, and explicit log lines for migrations, restores, deletions and storage pauses. Above that core the app is largely dark. The whole Desktop layer has one logger (ExitCoordinator); 26 bare catch (Exception) blocks and the fire-and-forget page loads discard exceptions. A crash is logged but never shown to the user. The log has no version or session banner and no size bound. Nothing in the app reads ai_request for diagnosis, and there is no diagnostics view or export (known, deferred to D2). A storage or provider-HTTP failure can usually be diagnosed from what is left behind; a "page is blank", "it just closed" or "answer spins forever" report often cannot, or only by someone who opens the SQLite file by hand. That is below an industrial bar, though the foundations are sound.

**Strengths**

- Per-request tracing is real and content-free: ai_request stores timings, HTTP status, provider request id, outcome, error kind, token counts and app-side estimate (src/Mindweave.Application/Diagnostics/RequestTrace.cs, SchemaMigrations.cs:132 and :396). TRACK.md (2026-10-27) records it being used to reproduce a real failure from trial data.
- Redaction happens where text is produced and in layers: ChatCompletionsClient.Redacted scrubs the exact key on every ChatClientException (ChatCompletionsClient.cs:303), RequestTrace.Finish scrubs again, and FileLoggerProvider scrubs every line including exception text (FileLoggerProvider.cs:61). Covered by RedactionTests and FileLoggerRedactionTests; migration v3 scrubs error details stored before redaction existed.
- Logs carry identifiers, not content: messages use workspace, conversation and task ids and counts, never titles, questions or answers. Reasoning deltas are reduced to a length (ChatCompletionsClient.cs:252).
- The file log is crash-tolerant: AutoFlush per entry, FileShare.ReadWrite, UTC timestamps, daily files, 14 kept (FileLoggerProvider.cs:17, :76).
- Destructive and recovery paths log well, with actionable Critical messages: migration with backup path, restore refused or failed, 'move it back by hand before starting again' (SqliteBackupFiles.cs:423), deletions with counts and backup path, interrupted tasks recovered at startup, transient open failures with the SQLite extended code (SqliteDatabase.cs:593).
- A lost diagnostic write cannot affect the product: GenerationRequests.RecordAsync (GenerationRequests.cs:137) catches everything, logs, and keeps the trace failure out of storage-health state.
- Storage health is surfaced to the user as a pause state with automatic recovery, and StorageHealth logs both the failure and the recovery (StorageHealth.cs:354-364).
- Consistent use of source-generated LoggerMessage with stable templates; both AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException are hooked (Program.cs:36-37).

**Not examined**

- Did not run the app, so no real log file was inspected and the on-screen behaviour of a crashed generation task (finding 3) is inferred from code, not observed.
- macOS and Windows native crash behaviour (whether Avalonia.Native surfaces managed exceptions from run-loop callbacks to AppDomain.UnhandledException; WER and macOS crash-report dialogs).
- Credential store implementations (MacKeychainCredentialStore, WindowsCredentialStore) beyond grepping their exception messages.
- SummaryService, SummaryDrafts, ContextCompressor, ConversationMarksService and WorkspaceNamePreparation logging call sites were only listed, not traced end to end.
- The recovery page (RecoveryViewModel) and the exit-failure UI were not read in full.
- Whether tests assert on log output beyond the two redaction test files; the scripts/ directory (perf.sh, windows.sh, mailbox.sh).
- The statistics queries over ai_request (SqliteStatisticsStore) for correctness.
- Baseline vuln/outdated/format outputs; of test.txt only the tail was read (Desktop tests: 381 passed, 58 skipped, 0 failed).

### observability-1. A non-ChatClientException in a generation run leaves the answer unfinished with no error shown and no ai_request row; the same exception from 'Test connection' crashes the app

- Final: **high** (confirmed); auditor said high
- Where: `src/Mindweave.Infrastructure/Ai/ChatCompletionsClient.cs:246`; effort S

**Evidence.** Parse: `if (node?["error"] is { } error) { throw new ChatClientException(..., Limit(error["message"]?.ToString() ?? ...` (line 246), `node?["choices"]?.AsArray()` (line 249); ThrowIfFailedAsync: `JsonNode.Parse(text)?["error"]?["message"]` (line 283). I ran these expressions in a scratch net10 console: `{"error":"model not found"}`, `"keepalive"` and `[1]` all throw InvalidOperationException ("The node must be of type 'JsonObject'"); `{"choices":{}}` throws for AsArray. StreamAsync (line 57) and AnswerRunner (lines 136-146) catch only OperationCanceledException and ChatClientException. The exception therefore skips `checkpointStop.CancelAsync()`, FinishAsync and `_requests.RecordAsync(trace.Finish(...))` and lands in GenerationCoordinator.cs:531-534, which only logs "Generation task {TaskId} crashed" and frees the slot. Nothing calls live.Finish or FinishAnswerAsync; no test covers this path (no test references the crash log). ModelSettingsViewModel.RunAsync (lines 271-295) also catches only ModelSettingsException, ChatClientException and CredentialUnavailableException, and no AsyncRelayCommand uses FlowExceptionsToTaskScheduler.

**Failure scenario.** A compatible gateway returns an error as a string, e.g. HTTP 401 with body `{"error":"Unauthorized"}` or an SSE event `data: {"error":"overloaded"}`. During an answer: the message row stays 'streaming' until the next start marks it interrupted, the user gets no failure label or detail, ai_request has no row for the very request that failed, and the only evidence is one log line keyed by a task id the UI never shows. From Settings › Test connection the InvalidOperationException escapes the command and terminates the app. This overlaps the error-handling dimension; it is reported here for the diagnostic gap.

**Recommendation.** In ChatCompletionsClient wrap response-shape failures (InvalidOperationException, FormatException) as ChatClientException(InvalidResponse). In AnswerRunner and the task runners add a last-resort catch that finishes the message or task as Failed with an 'internal error' kind and still records the trace. Add a test with a string-valued error body.

**Reproduction attempt: confirmed, high.** I could not refute the finding; every link in the chain holds in the code, and the key runtime behaviour reproduces.

**The throw is real.** A scratch net10 console (outside the worktree) running the same expressions as `Parse` gave `InvalidOperationException` for `{"error":"Unauthorized"}`, `"keepalive"`, `[1]` and `{"choices":{}}`. Both `Parse` and `ThrowIfFailedAsync` catch only `JsonException`.

**Nothing downstream converts it.**
- `ChatCompletionsClient.StreamAsync` catches only `ChatClientException`.
- `SendAsync` has a bare `catch` that disposes and rethrows.
- `GenerationRequests.StreamAsync` is a straight pass-through.
- `AnswerRunner` catches only `OperationCanceledException` and `ChatClientException`, with no `finally`.

So `checkpointStop.CancelAsync()`, `FinishAsync` (which does `live.Finish`, `FinishAnswerAsync` and the `AnswerFinished` event) and `_requests.RecordAsync(trace.Finish(...))` are all skipped. The only handler is the catch-all in `GenerationCoordinator.RunAsync`, which logs "Generation task {TaskId} crashed" and frees the slot.

**Test connection crashes the app.** `ModelSettingsService.TestAsync` catches only `ChatClientException` and `CredentialUnavailableException`. `ModelSettingsViewModel.RunAsync` adds only `ModelSettingsException`. The command is a plain `[RelayCommand]` on CommunityToolkit.Mvvm 8.4.2 with no `FlowExceptionsToTaskScheduler`, so the exception is rethrown on the UI thread. The only global handlers are `AppDomain.UnhandledException` and `UnobservedTaskException` in `Program.cs`, and both only log. The crash itself is inferred from toolkit semantics; I did not run the app.

**No mitigating decision or test.** No test references the crash log or a string-valued error body.

**Severity stays high**, for three reasons:
- The trigger is realistic: string-valued `error` bodies are common from proxies and gateways on 401/404/5xx, and the app targets arbitrary OpenAI-compatible services.
- The consequences are worse than the finding states (see corrections): the conversation is blocked until restart.
- A remote response body terminates the app from Settings.

It is not critical: no data is lost, and a restart recovers.

*Corrections to the finding.* Nothing in the finding is wrong; it is understated in three places.

1. **The conversation is blocked, not just unlabelled.** With the message left 'streaming', `ThrowIfUnfinished` (`SqliteConversationStore.cs:276-285`) rejects further sends and retries in that conversation with `AnswerInProgressException` until the app is restarted. The in-memory live answer also never leaves the streaming state.
2. **The checkpoint loop is orphaned.** `checkpointStop` is disposed by `using` without being cancelled, so `CheckpointLoopAsync` keeps running for the life of the process.
3. **More paths share the defect.** `ListModelsCoreAsync` has the same hole (`json?["data"]?.AsArray()` plus the shared `ThrowIfFailedAsync`). The transient/title/summary path in `GenerationRequests` (lines 83-99) has the same narrow catch pattern, though I did not trace its consequences.

One precision point: the Test-connection crash rests on CommunityToolkit `AsyncRelayCommand` semantics and the absence of any dispatcher-level handler in `src`. I did not observe the process terminating.

<details><summary>Reproduction</summary>

Scratch console (net10.0) in the session scratchpad, outside the worktree, mirroring the `Parse` expressions:

```csharp
using System.Text.Json.Nodes; using System.Linq;
foreach (var s in new[]{"{\"error\":\"Unauthorized\"}","\"keepalive\"","[1]","{\"choices\":{}}","{\"error\":{\"message\":\"x\"}}"}){
 try { var n=JsonNode.Parse(s); if (n?["error"] is {} e) { Console.WriteLine(s+" => msg "+(e["message"]?.ToString() ?? e.ToJsonString())); continue;} var c=n?["choices"]?.AsArray().FirstOrDefault(); Console.WriteLine(s+" => ok"); }
 catch(Exception ex){ Console.WriteLine(s+" => "+ex.GetType().Name+": "+ex.Message);} }
```

Output:

```
{"error":"Unauthorized"} => InvalidOperationException: The node must be of type 'JsonObject'.
"keepalive" => InvalidOperationException: The node must be of type 'JsonObject'.
[1] => InvalidOperationException: The node must be of type 'JsonObject'.
{"choices":{}} => InvalidOperationException: The node must be of type 'JsonArray'.
{"error":{"message":"x"}} => msg x
```

The end-to-end paths (stuck answer, app crash on Test connection) were traced by reading code, not executed; the audit is read-only and the app was not run.

</details>

**Reproduction attempt: confirmed, high.** Reproduced with xUnit tests against the real ChatCompletionsClient, GenerationCoordinator, AnswerRunner and SQLite stores (existing ChatHarness), at track/claude-auto commit 1ba95c9.

Setup note: my assigned worktree is based on main and has no product code, so I exported track/claude-auto with `git archive` into a scratchpad directory and built/tested there. The assigned worktree and the shared checkout were never modified; the scratch copy is deleted. Uncommitted changes in the shared checkout, if any, were not tested.

What the tests showed:
1. Client: any non-2xx body or SSE event whose JSON root is not an object, or whose `error` is a string, or whose `choices` is not an array, throws InvalidOperationException instead of ChatClientException. All 8 odd shapes failed; both well-formed controls passed.
2. Answer run: with HTTP 401 `{"error":"Unauthorized"}` or SSE `data: {"error":"overloaded"}`, after the coordinator freed the slot (UnfinishedCount == 0) plus a 1 s wait, the message row was still `Streaming` with no error kind, generation_task was still `running`, and ai_request had 0 rows. The control (`{"error":{"message":"bad key"}}`) gave Failed / Unauthorized / task failed / 1 row.
3. Test connection: ModelSettingsService.TestAsync throws InvalidOperationException for the same two bodies (stack traces through ChatCompletionsClient.cs:283 and :246); the control returns a failed ConnectionTestResult.

Not reproduced by test, confirmed by reading only: the app crash. ModelSettingsViewModel.RunAsync catches only ModelSettingsException, ChatClientException and CredentialUnavailableException; TestAsync is a plain [RelayCommand] (CommunityToolkit.Mvvm 8.4.2, no FlowExceptionsToTaskScheduler anywhere in src); there is no Dispatcher unhandled-exception handler; AppHost.Run has try/finally only. So the exception should leave the UI loop and end the process, but I did not run the desktop app or a headless UI test.

Severity stays high: string-valued `error` bodies are common from proxies and gateways, the trigger is entirely server-side, and the result is either a process exit from Settings or an answer stuck in streaming with zero user-visible or ai_request evidence.

*Corrections to the finding.* - The generation_task row is also left in `running`, not only the message in `streaming`; the finding mentions only the message.
- "Terminates the app" from Settings > Test connection is confirmed only down to the service call throwing InvalidOperationException; the process exit itself is inferred from code (no general catch in the view model, no dispatcher handler), not run.
- "Stays streaming until the next start marks it interrupted" was not checked; I only verified the row is still Streaming/running after the run ended and the slot was freed.
- The finding's cited line numbers (246, 249, 283; AnswerRunner 136-146; GenerationCoordinator 531-534) match the code within a few lines.

<details><summary>Reproduction</summary>

Test file added temporarily as tests/Mindweave.Infrastructure.Tests/Faults/Repro02399Tests.cs in a scratch export of track/claude-auto (now deleted). Run with: dotnet test tests/Mindweave.Infrastructure.Tests --filter "FullyQualifiedName~Repro02399" --logger "console;verbosity=detailed"

Core of the test code (Handler returns the given status/body for chat calls, `{"data":[]}` for /models):

```csharp
[Theory, MemberData(nameof(Shapes))]
public async Task Client_reports_odd_shapes_as_ChatClientException(int status, string body, bool sse)
{
    var client = new ChatCompletionsClient(new HttpClient(new Handler(status, body, sse)), new ChatClientOptions());
    Exception? thrown = null;
    try { await foreach (var _ in client.StreamAsync(new ChatRequest(Model, [new ChatTurn(ChatRole.User, "hi")]), "gw-key-0123456789", Ct)) { } }
    catch (Exception ex) { thrown = ex; }
    output.WriteLine($"{status} {body.Trim()} -> {thrown?.GetType().Name}: {thrown?.Message}");
    Assert.IsType<ChatClientException>(thrown);
}

[Theory]
[InlineData(401, "{\"error\":{\"message\":\"bad key\"}}", false)] // control
[InlineData(401, "{\"error\":\"Unauthorized\"}", false)]
[InlineData(200, "data: {\"error\":\"overloaded\"}\n\n", true)]
public async Task Answer_run_ends_failed_with_a_request_record(int status, string body, bool sse)
{
    using var http = new HttpClient(new Handler(status, body, sse));
    await using var h = await ChatHarness.StartAsync(client: new ChatCompletionsClient(http, new ChatClientOptions()));
    var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (h.Generation.UnfinishedCount != 0 && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
    await Task.Delay(1000, Ct);
    var answer = (await h.GetMessageAsync(sent.Answer!.Value))!;
    var task = await h.TaskStateAsync(sent.Answer!.Value);
    var requests = await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM ai_request;"), Ct);
    output.WriteLine($"... -> message={answer.State} error={answer.ErrorKind} task={task} ai_request rows={requests} unfinished={h.Generation.UnfinishedCount}");
    Assert.Equal(MessageState.Failed, answer.State);
    Assert.Equal("failed", task);
    Assert.Equal(1, requests);
}

[Theory] // same three InlineData rows
public async Task Test_connection_returns_a_failed_result_instead_of_throwing(int status, string body, bool sse)
{
    using var http = new HttpClient(new Handler(status, body, sse));
    await using var h = await ChatHarness.StartAsync(client: new ChatCompletionsClient(http, new ChatClientOptions()));
    var thrown = await Record.ExceptionAsync(async () =>
        output.WriteLine($"result {await h.Models.TestAsync("https://fake.example/v1", "fake-model", "sk-test", Ct)}"));
    Assert.Null(thrown);
}
```

Actual output (per-case lines, trimmed):

```
Passed Answer_run...(401, {"error":{"message":"bad key"}})
 -> message=Failed error=Unauthorized task=failed ai_request rows=1 unfinished=0
Failed Answer_run...(401, {"error":"Unauthorized"})
 -> message=Streaming error= task=running ai_request rows=0 unfinished=0
Failed Answer_run...(200, data: {"error":"overloaded"})
 -> message=Streaming error= task=running ai_request rows=0 unfinished=0

Failed Client... 401 {"error":"Unauthorized"} -> InvalidOperationException: The node must be of type 'JsonObject'.
Failed Client... 404 {"error":"model not found"} -> InvalidOperationException: The node must be of type 'JsonObject'.
Failed Client... 500 "keepalive" -> InvalidOperationException: The node must be of type 'JsonObject'.
Failed Client... 500 [1] -> InvalidOperationException: The node must be of type 'JsonObject'.
Failed Client... 200 data: {"error":"overloaded"} -> InvalidOperationException: The node must be of type 'JsonObject'.
Failed Client... 200 data: "keepalive" -> InvalidOperationException: The node must be of type 'JsonObject'.
Failed Client... 200 data: [1] -> InvalidOperationException: The node must be of type 'JsonObject'.
Failed Client... 200 data: {"choices":{}} -> InvalidOperationException: The node must be of type 'JsonArray'.
Passed Client... 401 {"error":{"message":"bad key"}} -> ChatClientException: HTTP 401: bad key
Passed Client... 200 data: {"error":{"message":"overloaded"}} -> ChatClientException: overloaded

Failed Test_connection...(401, {"error":"Unauthorized"}) -> thrown InvalidOperationException: The node must be of type 'JsonObject'.
   at JsonNode.get_Item(String) / ChatCompletionsClient.ThrowIfFailedAsync ... ChatCompletionsClient.cs:line 283 / ModelSettingsService.TestAsync ... line 185
Failed Test_connection...(200, data: {"error":"overloaded"}) -> thrown InvalidOperationException: The node must be of type 'JsonObject'.
   at ChatCompletionsClient.Parse ... ChatCompletionsClient.cs:line 246
Passed Test_connection...(401, {"error":{"message":"bad key"}}) -> result ConnectionTestResult { Succeeded = False, Error = Unauthorized, Detail = HTTP 401: bad key }

Test Run Failed.
Total tests: 16
     Passed: 4
     Failed: 12
```
```

</details>

### observability-2. Desktop layer swallows exceptions without logging: 26 bare catch (Exception) blocks, one logger in the whole project

- Final: **medium** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Desktop/ViewModels/Pages/UsageStatisticsViewModel.cs:334`; effort M

**Evidence.** The only ILogger in src/Mindweave.Desktop is ExitCoordinator's (ExitCoordinator.cs:58) plus the crash hook in Program.cs:55. A count of `catch (Exception)` with no variable gives 26 across 11 files: SectionPages.cs (8), UsageStatisticsViewModel.cs (3), WorkspaceNewsViewModel.cs (3), WorkspacesViewModel.cs (3), ShellViewModel.cs (2), NetworkSession.cs (2), and one each in ConversationDeletionViewModel, WorkspaceDeletionViewModel, ConversationLifecycleActions, ArchivedViewModel, AppStartup. Typical: UsageStatisticsViewModel.cs:334 `catch (Exception) { ... LoadError = UiText.StatisticsLoadFailed; }`; NetworkSession.cs:169 `catch (Exception) { SaveFailed = true; return false; }`; ConversationLifecycleActions.cs:104 sets ArchiveNotice to a fixed string. The exception object is discarded and nothing is written to the log. The Infrastructure read path does not log either: SqliteDatabase.ReadAsync (line 263) has no logging wrapper.

**Failure scenario.** A user reports 'the statistics page says it could not load' or 'saving the network layout failed'. The cause could be SQLITE_BUSY, a corrupt page, a query bug after a migration, or a NullReferenceException in the view model. The log contains nothing for that moment and the UI shows a fixed sentence, so the report cannot be diagnosed without reproducing it.

**Recommendation.** Inject ILogger into the view models (or a small IUiFaults service) and log every swallowed exception at Warning with the operation name and relevant ids. Fail the build for a bare `catch (Exception)` without a log call in Desktop: a Roslyn analyzer or an architecture test alongside SeamTests.

**Skeptic: partially confirmed, medium.** The factual core holds; the severity is overstated.

**What I confirmed**
- The count is exact: 26 `catch (Exception)` blocks with no variable across 11 files in src/Mindweave.Desktop (SectionPages 8, UsageStatisticsViewModel 3, WorkspaceNewsViewModel 3, WorkspacesViewModel 3, ShellViewModel 2, NetworkSession 2, and one each in the other five).
- The only logging in Desktop is ExitCoordinator's `ILogger` (used in its four catch blocks) and the crash hook in Program.cs:55. No view model has a logger.
- The cited sites say what is claimed: UsageStatisticsViewModel.cs:334 sets `LoadError` to a fixed string, NetworkSession.cs:169 sets `SaveFailed = true`, ConversationLifecycleActions.cs:104 sets a fixed notice. Each discards the exception.
- Nothing below these sites logs either. `SqliteDatabase.ReadAsync` and `WriteAsync` have no logging wrapper, and UsageStatistics, UsagePrices, AppearanceService, NewsAlerts, SidebarLayoutService, NetworkService and SqlitePreferenceStore contain no logger at all. So a statistics read failure, a layout save failure, a preference failure or a delete-scope inspection failure leaves no log line.
- No design decision in docs/design/decisions.md or TRACK.md says these are deliberately unlogged. The docs repeatedly say comparable best-effort failures are "只记日志" (log only), so silent swallowing goes against the project's own convention.

**Why high is overstated**
- Core content writes are logged before they reach the UI. ConversationService, SummaryService, SummaryDrafts, TerminalWrites, WorkspaceService, WorkspaceDeletion, ConversationDeletion and ReadingStateService call `StorageHealth.Report` or `Retain`, which logs at Error with the exception (StorageHealth.cs:135, 143, 354).
- Startup, migration, backup and restore failures are logged in SqliteDatabase; unhandled exceptions are logged at Critical.
- The unlogged set is therefore non-core paths: cosmetic preferences (appearance, sidebar, news toggles), network layout, statistics and other reads, and scope inspection.
- No data is lost and the user is told in every non-cosmetic case. Each catch carries a CA1031 pragma with a written justification for swallowing, so the swallowing is deliberate; only the missing log is the defect.
- The impact is a diagnosability gap in a local single-user desktop app, including a view-model bug such as a NullReferenceException being masked as "could not load". That is real but medium.

**One thing the finding missed**
Four more `catch (Exception ex)` blocks in Desktop also do not log, though they show `ex.Message` to the user: OnboardingViewModel.cs:37, BackupDialogs.cs:209, SummaryViewModel.cs:353 and WorkspacesViewModel.cs:225.

The recommendation (inject a logger or a small UI-fault service, plus an architecture test) is sound.

*Corrections to the finding.* - Severity should be medium, not high. Core content write failures are logged at Error through `StorageHealth.Report`/`Retain` before the UI catches them, and startup, migration, backup, restore and crash paths are logged. The gap is limited to non-core paths.
- The scenario list overreaches on "SQLITE_BUSY, a corrupt page" for writes to core content: those are logged. It is accurate for the two examples given (statistics load, network layout save) and for a view-model bug masked by the catch.
- NetworkSession.cs is at src/Mindweave.Desktop/Network/, not under ViewModels/Pages/.
- The swallowing itself is deliberate and justified per site by CA1031 pragmas, mostly for cosmetic preferences; the defect is only the missing log line.
- The finding undercounts slightly: four `catch (Exception ex)` sites (OnboardingViewModel.cs:37, BackupDialogs.cs:209, SummaryViewModel.cs:353, WorkspacesViewModel.cs:225) also do not log, though they show `ex.Message` to the user.

**Reproduction attempt: confirmed, medium.** Reproduced on track/claude-auto at 1ba95c9 with two focused tests in tests/Mindweave.Desktop.Tests, both of which failed exactly as the finding predicts.

A recording ILoggerProvider was added through the harness's service overrides, so it sees everything any layer logs.
1. A stub IUsageStatistics threw InvalidOperationException. The page showed UiText.StatisticsLoadFailed, and the log held only the two startup Information lines; the exception appeared nowhere.
2. With no stub, the real ai_request table was renamed so the real SQLite read path failed. The page showed the same sentence and zero log entries were written after the rename. This confirms the claim that SqliteDatabase.ReadAsync has no logging wrapper: it is a bare Task.Run with try/finally and no logger call.

The counts are exact. There are 26 `catch (Exception)` blocks without a variable across 11 files in src/Mindweave.Desktop, with the per-file numbers as stated. Each carries a `#pragma warning disable CA1031` with a comment justifying the user-visible handling, so the swallow is deliberate for the UI, but none logs. The only logger consumers in Desktop are ExitCoordinator and the crash hook at Program.cs:55. TRACK.md does not list this as a known finding.

Severity adjusted to medium. This is a diagnosability gap, not data loss or wrong behaviour: every site tells the user about the failure and most offer a retry. Some failures on write paths are probably logged lower down, since many Application services have loggers, but I did not trace each of the 26 sites. For a single-user local desktop app the impact is that field reports of these failures cannot be diagnosed from the log; that is a real gap against the stated production bar, and medium-to-high is defensible. The recommended fix is sound and cheap.

The worktree is restored: test file deleted, original branch checked out, build artifacts removed, nothing committed.

*Corrections to the finding.* - The finding says ExitCoordinator's is "the only ILogger" in Desktop. More precisely, it is the only injected logger; Program.cs:55 also creates one for the crash hook (the finding does mention this) and CompositionRoot wires the provider.
- All 26 sites carry a deliberate CA1031 suppression with a comment justifying the UI handling. The defect is the missing log call, not careless swallowing, and every site still tells the user about the failure.
- "Nothing is written to the log" is demonstrated for the statistics read path, both with a stubbed service and with a real SQLite failure. For sites that call Application services which own loggers (write paths), some failures may already be logged below Desktop; I did not trace each of the 26, so "nothing logged" should not be assumed for all of them.
- Severity: high overstates it for a local single-user app where every failure is shown to the user and no data is lost; medium fits better.

<details><summary>Reproduction</summary>

Temporary file tests/Mindweave.Desktop.Tests/ReproSwallowedExceptionTests.cs (deleted afterwards). Core of the test code:

```csharp
public class ReproSwallowedExceptionTests(ITestOutputHelper output)
{
    private const string Marker = "REPRO-MARKER-7f3a";

    [AvaloniaFact]
    public async Task A_view_model_fault_on_the_statistics_page_reaches_the_log()
    {
        var log = new Recorder();   // ILoggerProvider that enqueues (category, level, message, exception)
        await using var app = DesktopHarness.Create(s =>
        {
            s.AddLogging(b => b.AddProvider(log));
            s.AddSingleton<IUsageStatistics>(new Throwing());   // LoadAsync => Task.FromException(new InvalidOperationException(Marker))
        });
        await app.StartWithWorkspaceAsync("火车历史");
        var page = await OpenAsync(app);   // 设置 -> click StatisticsButton -> await page.Loaded
        Assert.Equal(UiText.StatisticsLoadFailed, page.LoadError);   // passed
        Dump(log);
        Assert.Contains(log.Entries, e => e.Level >= LogLevel.Warning && (e.Exception?.Message.Contains(Marker, StringComparison.Ordinal) ?? false));
    }

    [AvaloniaFact]
    public async Task A_real_sqlite_read_failure_on_the_statistics_page_reaches_the_log()
    {
        var log = new Recorder();
        await using var app = DesktopHarness.Create(s => s.AddLogging(b => b.AddProvider(log)));
        await app.StartWithWorkspaceAsync("火车历史");
        var db = app.Services.GetRequiredService<SqliteDatabase>();
        await db.WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "ALTER TABLE ai_request RENAME TO ai_request_gone;";
            return command.ExecuteNonQuery();
        }, CancellationToken.None);
        var before = log.Entries.Count;
        var page = await OpenAsync(app);
        Assert.Equal(UiText.StatisticsLoadFailed, page.LoadError);   // passed
        Dump(log);
        Assert.Contains(log.Entries.Skip(before), e => e.Level >= LogLevel.Warning && e.Exception is not null);
    }
}
```

Command: `dotnet test tests/Mindweave.Desktop.Tests --filter FullyQualifiedName~ReproSwallowedExceptionTests --logger "console;verbosity=detailed" -p:TreatWarningsAsErrors=false -p:EnforceCodeStyleInBuild=false`

Actual output (trimmed):

```
Mindweave.Desktop.Tests.ReproSwallowedExceptionTests.A_view_model_fault_on_the_statistics_page_reaches_the_log [FAIL]
  Assert.Contains() Failure: Filter not matched in collection
  ... ReproSwallowedExceptionTests.cs:line 34
  Output:
    log entries: 2
      [Information] Mindweave.Infrastructure.Sqlite.SqliteDatabase: Created database /var/folders/.../mindweave.db at schema 10 | ex=:
      [Information] Mindweave.Application.Workspaces.WorkspaceService: Created workspace 01a1d1e6-bbe4-7287-b102-18fd35cf7cec | ex=:
Mindweave.Desktop.Tests.ReproSwallowedExceptionTests.A_real_sqlite_read_failure_on_the_statistics_page_reaches_the_log [FAIL]
  Assert.Contains() Failure: Filter not matched in collection
  Collection: []
  ... ReproSwallowedExceptionTests.cs:line 58
  Output:
    log entries: 2   (same two startup Information lines; none after the table rename)
Test Run Failed.
Total tests: 2
     Failed: 2
 Total time: 2.9648 Seconds
```

In both tests the preceding assertion `LoadError == UiText.StatisticsLoadFailed` passed, so the failure path was taken and the UI showed the fixed sentence. The failing assertion is the one requiring a Warning-or-above log entry carrying the exception. Neither result depends on timing.
```

</details>

### observability-3. Fire-and-forget page loads lose their exceptions: a failed load leaves a blank or stale page with no message and no timely log line

- Final: **medium** (confirmed (one verifier lowered it)); auditor said high
- Where: `src/Mindweave.Desktop/ViewModels/Conversations/ConversationViewModel.cs:72`; effort M

**Evidence.** `_reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(30), () => _ = _load.Request());` The same pattern is in SummaryViewModel.cs:57, ArchivedViewModel.cs:70 and NetworkHomeViewModel.cs:41. SerialRefresh.RunAsync (Services/IUiDispatcher.cs:66-82) has try/finally only. ConversationViewModel.LoadAsync (line 377) awaits five store reads with no catch. A faulted task is therefore discarded; it reaches the log only if the finalizer later raises TaskScheduler.UnobservedTaskException, which Program.cs:37 logs as Critical "Unhandled exception" at GC time (possibly minutes later or never), with no conversation id. Other discarded tasks: ConversationView.axaml.cs:94 `_ = RestoreAsync(_page)`, AnswerView.axaml.cs:76 `_ = CreateBranchAsync()`, WorkspacesViewModel.cs:710, SummaryView.axaml.cs:108.

**Failure scenario.** A conversation read throws (SqliteException from a locked or damaged database, or a mapping bug on an unusual row). The page keeps its previous content or stays empty and no notice appears. The user reports 'the conversation didn't open'. The log has either nothing, or a Critical 'Unhandled exception' stamped at an unrelated later time that reads like a crash although the app kept running.

**Recommendation.** Catch inside SerialRefresh.RunAsync (or accept an onError callback): log immediately with the page identity and set a visible load-failed state on the page. Replace the other `_ = XAsync()` calls with a helper that observes and logs faults. Log UnobservedTaskException under a distinct message so it is not mistaken for a crash.

**Skeptic: partially confirmed, medium.** I could not refute the mechanism; every cited line exists and says what is claimed. What I could not support is the "high" rating.

**What holds**
- Four page view models discard the reload task: `() => _ = _load.Request()`.
- `SerialRefresh.RunAsync` has try/finally only: no catch, no logging, no error callback.
- `ConversationViewModel.LoadAsync` awaits five reads (`LoadAsync`, `GetLineageAsync`, `ListBranchesAsync`, `ArchivedAmongAsync`, `PlanRestoreAsync`) with no catch. The file contains no catch at all, and `ConversationService.LoadAsync` passes store exceptions straight through.
- The first load is equally unobserved. `Loaded = LoadFirstAsync(...)` is awaited only by `ConversationView.RestoreAsync`, which is itself discarded (`_ = RestoreAsync(_page)`) and has no catch.
- The only safety net is `TaskScheduler.UnobservedTaskException` in `Program.cs`, which logs Critical "Unhandled exception" through the same `LogCrash` used for real crashes, with no page or conversation identity.
- There is no load-failed state on these pages. The only one in the Desktop project is `UsageStatisticsViewModel` (`LoadError = UiText.StatisticsLoadFailed`), which shows the team knows the pattern but did not apply it here.
- No design decision or TRACK.md entry accepts this, and `SerialRefreshTests` has a single test that does not cover faults.

**Where the finding understates it**
- `SerialRefresh` keeps the faulted task in its `_running` field. It cannot be finalized while the page view model is alive and no new request replaces it, so the log line is delayed until at least page disposal plus a GC, not merely "at GC time".

**Why medium, not high**
- The trigger is rare. The database is local SQLite behind a single-instance lock with a 5 s busy timeout (`SqliteConnections.cs:12`), and write-side storage failures have their own visible pause mechanism (`StorageHealth` / `IsStorageBlocked`).
- When it does happen there is no data loss or corruption, the app keeps running, and the next change event retries the load.
- The log record is usually late and misleadingly labelled rather than absent.
- It is a real diagnosability and UX gap against a production bar, but not high.

*Corrections to the finding.* - **Severity:** high is overstated; medium fits. The trigger is rare, nothing is lost or corrupted, and the next change event retries the load.
- **"No timely log line":** this is, if anything, understated. `SerialRefresh._running` holds the faulted task, so it cannot be finalized while the page is alive and no new request arrives.
- **"Awaits five store reads":** they are five `IConversations` service calls, which wrap more store reads than that. The point stands.
- **`WorkspacesViewModel.cs:710`:** weaker than the other examples. `SuggestCoreAsync` has its own handling (at least for cancellation), and the task is kept in `Suggesting`. I did not read its full catch list.
- **Not mentioned by the finding:** other view models (`WorkspaceNewsViewModel:82`, `ConversationListViewModel:73`) store the task in `Loaded` instead of discarding it. That observes nothing either unless something awaits it, so the gap is somewhat wider than the four files listed.

**Reproduction attempt: confirmed, medium.** Reproduced with a headless UI test on track/claude-auto at 1ba95c9 (checked out detached in my isolated worktree, because the worktree branch itself is cut from main and has no product code).

The test wraps the real ConversationService in a proxy whose LoadAsync faults on demand, opens a real conversation page, then renames the conversation through the service so the page reloads via ConversationChanged -> UiThrottle -> `_ = _load.Request()` -> SerialRefresh.RunAsync -> LoadAsync.

What it showed:
- The failing LoadAsync was called once.
- The page kept its old title ("火车历史") although the stored title had changed; the message list was unchanged; IsMissing stayed false.
- The page's three notice properties (SourceNotice, BranchNotice, StorageNotice) stayed empty. There is no load-failed state on the page at all.
- A recording ILoggerProvider received zero entries at Warning or above and nothing mentioning the exception.
- TaskScheduler.UnobservedTaskException did not fire, even after five forced GC + finalizer passes.

So the core claim holds: a faulted page reload is discarded, the page goes silently stale, and nothing is logged.

Why I lower it to medium:
- The trigger is rare: a store read throwing on a local SQLite database (locked, damaged, or a mapping bug).
- There is no data loss or corruption. SerialRefresh resets _busy in finally, so the next change notification reloads normally.
- The cost is a stale or empty page plus missing diagnostics, which is a real observability gap for an industrial bar but not a high-severity defect.

Code reading confirms the rest of the pattern:
- SerialRefresh.RunAsync has try/finally only.
- ConversationViewModel.LoadAsync has no catch.
- The same `() => _ = _load.Request()` appears in SummaryViewModel:57, ArchivedViewModel:70 and NetworkHomeViewModel:41.
- The first load is also lost: Loaded = LoadFirstAsync(...) is awaited only by ConversationView.RestoreAsync, which is itself discarded at ConversationView.axaml.cs:94 and has no catch.
- AnswerView.CreateBranchAsync (line 427) has no catch and is discarded at line 76.
- I did not find a try/catch in the first ~20-25 lines of WorkspacesViewModel.SuggestNamesAsync or SummaryViewModel.ReportProgressAsync, but I did not read those methods in full or the callees, so those two sites are plausible rather than confirmed.

*Corrections to the finding.* 1. The logging claim is understated. The finding says the fault reaches the log "at GC time (possibly minutes later or never)". In the test, UnobservedTaskException did not fire even after five forced GC + finalizer passes. The likely reason is that SerialRefresh keeps the faulted task in its `_running` field, so it stays reachable until the next Request() replaces it or the page is collected. For a page that stays open, "never" is the normal case, so the "misleading Critical 'Unhandled exception' at an unrelated time" scenario is rarer than claimed for the SerialRefresh sites. It still applies to the discarded tasks that nothing holds (RestoreAsync, CreateBranchAsync and the like).

2. "A blank page" (first load) was not exercised by the test. Code reading supports it: LoadFirstAsync's task (Loaded) is only awaited by the discarded RestoreAsync.

3. Severity high looks overstated; I rate it medium. The trigger is a rare local-read failure, nothing is lost or corrupted, and the page recovers on the next change notification. The harm is a silently stale or empty page and no diagnostics.

4. Line references are accurate. One small precision: LoadAsync at line 377 awaits seven calls (five store reads before the UI update, then GetContextUsageAsync and Summary.LoadAsync), all without a catch.

5. The recommendation to catch inside SerialRefresh.RunAsync needs care. ConversationLifecycleActions is handed `() => _load.Request()` (ConversationViewModel.cs:84) and LoadFirstAsync awaits it, so some callers may rely on the fault propagating. An onError callback, or log-and-rethrow, is safer than swallowing.

<details><summary>Reproduction</summary>

Test added temporarily as tests/Mindweave.Desktop.Tests/ReproLostLoadFaultTests.cs, then deleted; the worktree is clean and back on its branch.

Command: `dotnet test tests/Mindweave.Desktop.Tests --filter "FullyQualifiedName~ReproLostLoadFault" --logger "console;verbosity=detailed"`

Test code, condensed (the RecordingLogs ILoggerProvider and the Gate {Fail, FailedLoads} holder are omitted):

```csharp
[AvaloniaFact]
public async Task SerialRefresh_fault_is_only_on_the_returned_task()
{
    var refresh = new SerialRefresh(() => Task.FromException(new InvalidOperationException("boom")));
    var task = refresh.Request();
    await Task.Yield();
    output.WriteLine($"SerialRefresh task status: {task.Status}; exception: {task.Exception?.InnerException?.Message}");
    Assert.True(task.IsFaulted);
}

[AvaloniaFact]
public async Task A_failed_reload_leaves_a_stale_page_no_notice_and_no_log_line()
{
    var gate = new Gate(); var logs = new RecordingLogs();
    await using var app = DesktopHarness.Create(services =>
    {
        services.AddLogging(b => b.AddProvider(logs));
        services.AddSingleton<IConversations>(sp => FailingConversations.Wrap(sp.GetRequiredService<ConversationService>(), gate));
    });
    var page = await app.StartWithConversationAsync(waitForTitle: true);
    var titleBefore = page.Title; var messagesBefore = page.Messages.Count; var logsBefore = logs.Entries.Count;
    var unobserved = new ConcurrentQueue<Exception>();
    EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) => unobserved.Enqueue(e.Exception);
    TaskScheduler.UnobservedTaskException += handler;
    try
    {
        gate.Fail = true;
        await app.Services.GetRequiredService<ConversationService>().RenameAsync(page.Id, "改名后的标题", CancellationToken.None);
        await UiWait.UntilAsync(() => gate.FailedLoads > 0);
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        // ... output.WriteLine diagnostics ...
        Assert.Equal(titleBefore, page.Title);
        Assert.Equal(messagesBefore, page.Messages.Count);
        Assert.DoesNotContain(logs.Entries.Skip(logsBefore), e => e.Text.Contains("simulated", StringComparison.Ordinal));
        Assert.Empty(unobserved);
        for (var i = 0; i < 5 && unobserved.IsEmpty; i++) { await Task.Delay(50); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        output.WriteLine($"unobserved task exceptions after forced GC: {unobserved.Count}");
    }
    finally { gate.Fail = false; TaskScheduler.UnobservedTaskException -= handler; }
}

// DispatchProxy over IConversations: forwards everything to the real service, except
protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
{
    if (_gate.Fail && targetMethod!.Name == nameof(IConversations.LoadAsync))
    {
        Interlocked.Increment(ref _gate.FailedLoads);
        return Task.FromException<ConversationContent?>(new InvalidOperationException("simulated read failure"));
    }
    return targetMethod!.Invoke(_inner, args);
}
```

Actual output:

```
  Passed Mindweave.Desktop.Tests.ReproLostLoadFaultTests.A_failed_reload_leaves_a_stale_page_no_notice_and_no_log_line [894 ms]
  Standard Output Messages:
 failed LoadAsync calls: 1
 title before: '火车历史', after: '火车历史' (stored title is now '改名后的标题')
 messages before: 2, after: 2
 IsMissing: False
 error/notice-like properties on the page: [SourceNotice=, BranchNotice=, StorageNotice=]
 warning+ or matching log entries since the failure, before GC: 0
 unobserved task exceptions before GC: 0
 unobserved task exceptions after forced GC: 0:

  Passed Mindweave.Desktop.Tests.ReproLostLoadFaultTests.SerialRefresh_fault_is_only_on_the_returned_task [< 1 ms]
  Standard Output Messages:
 SerialRefresh task status: Faulted; exception: boom

Test Run Successful.
Total tests: 2
     Passed: 2
```

The tests pass because they assert the defective behaviour (stale page, no log entry, no unobserved-exception event). The harness uses NullLoggerProvider by default; I added a recording provider, so "nothing logged" was measured at the ILogger level, not against the real log file.
```

</details>

### observability-4. Crashes and failed launches are invisible to the user: no dispatcher-level handler, no crash notice, startup failures before the hook leave nothing

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Desktop/Program.cs:30`; effort M

**Evidence.** (a) Lines 14-35 (AppPaths.Resolve, SingleInstance.TryAcquire with `Directory.CreateDirectory(paths.DataDirectory)` outside its try at SingleInstance.cs:46, `new AppSession(...)` which builds the container with ValidateOnBuild and the FileLoggerProvider) all run before the UnhandledException hook is attached on line 36. (b) Line 30: `Console.Error.WriteLine($"Mindweave is already running ... but did not respond..."); return 2;` in an `<OutputType>WinExe</OutputType>` app (Mindweave.Desktop.csproj:3) with no file log entry. (c) There is no Dispatcher.UIThread.UnhandledException handler and no top-level catch in AppHost.Run (try/finally only); LogCrash writes one Critical entry and the process dies. (d) Nothing at the next start detects or mentions the previous crash, apart from an 'interrupted tasks' Warning if generation was running. (e) App.axaml.cs:48 `Dispatcher.UIThread.Post(async () => await ...AppStartup.RunAsync())` is async void, so a startup exception takes the same path.

**Failure scenario.** The primary instance hangs. The user double-clicks the icon again: after five seconds the second process exits with code 2, having written only to a console that does not exist. No window, no log entry. Similarly, with MINDWEAVE_DATA_DIR pointing at an unmounted or read-only location the process dies before any logger exists. In both cases the user can only report 'it doesn't open' and there is no artifact.

**Recommendation.** Wrap Main in a try/catch that writes a fallback crash file (temp directory if the data directory is unusable) and shows a native message box. Show a dialog for the 'already running but not responding' case and log it. Write a 'clean exit' marker so the next start can show 'Mindweave closed unexpectedly last time' with a button to open the logs.

**Skeptic: confirmed, medium.** I could not refute the finding; every cited piece of code exists and behaves as claimed, with a few overstated details.

**(a) Startup code before the hook: confirmed.** `AppPaths.Resolve`, both `SingleInstance.TryAcquire` calls, `SignalPrimaryAsync` and `new AppSession(...)` run on Program.cs lines 13-35. The two handlers are attached on lines 36-37, and they depend on the session's `ILoggerFactory`, so they could not log earlier anyway. `Directory.CreateDirectory(paths.DataDirectory)` at SingleInstance.cs:45 sits outside the try, and the try only catches `IOException`. An unusable `MINDWEAVE_DATA_DIR` therefore kills the process before any logger exists. `AppPaths.ChannelOf` can also throw `InvalidOperationException` there.

**(b) Silent "already running" exit: confirmed, and it contradicts the track's own design.** Program.cs:29-30 writes to `Console.Error` and returns 2; the project is `<OutputType>WinExe</OutputType>`. docs/design/s0-skeleton-storage.md:27 says the second process should show the user an "existing instance not responding" notice before exiting. A WinExe launched by double-click has no console, so the user sees nothing and nothing is logged.

**(c) No dispatcher handler, no top-level catch: confirmed.** A grep of `src` for `UnhandledException` finds only Program.cs:36-37. `AppHost.Run` is try/finally only. `LogCrash` writes one Critical entry and catches only `ObjectDisposedException`. The entry is likely to reach disk, because the file logger uses `AutoFlush = true`.

**(d) No previous-crash detection: confirmed.** I found no clean-exit marker or "closed unexpectedly" notice in `src`. Requirements (docs/requirements.md:264) only require that the app can be safely reopened after an abnormal exit, so this part is a quality gap, not a requirement violation.

**(e) Async void startup post: confirmed, but narrower than implied.** App.axaml.cs:48 posts an async lambda. An exception from `AppStartup.RunAsync` would reach the dispatcher and crash the process, though it would be logged by the line-36 handler. Database open failures are returned as a `NeedsRecovery` outcome and shown on a recovery page, so only unexpected exceptions take this path.

I found no decision in TRACK.md or docs/design that accepts or defers any of this. The closest is docs/design/s7-ui-polish.md:411, which only notes that a UI-loop crash skips release.

Medium stands: no data is lost (the crash-safe storage design is separate), but supportability is poor for a production-quality bar, and (b) breaks documented behaviour.

*Corrections to the finding.* 1. "No artifact" is overstated. A pre-hook unhandled .NET exception still leaves an OS-level trace: a ".NET Runtime" / Application Error entry in the Windows Event Log, or a macOS crash report under DiagnosticReports. What is missing is any Mindweave log entry or user-visible message. The exit-code-2 path does leave nothing at all.

2. The hung-primary double-click scenario is essentially Windows-only. On macOS, double-clicking a running .app normally activates the existing process via LaunchServices and does not start a second one; a second process arises only via `open -n` or running the binary directly.

3. The read-only data directory case usually throws `UnauthorizedAccessException`, which is not an `IOException`. Moving `CreateDirectory` inside the existing try would therefore not fix it; the catch at SingleInstance.cs:52 would also miss it from the `FileStream` open.

4. For (e), startup storage failures are handled as a recovery page, so the async-void path covers only unexpected exceptions, and those are logged.

5. In-loop crashes are logged with a flushed Critical entry (`AutoFlush = true`). The gap for that case is the missing user notice and next-start detection, not a missing log.

6. Not in the original finding: the "did not respond" silent exit contradicts docs/design/s0-skeleton-storage.md:27, which specifies a user-facing notice. That strengthens part (b).

### observability-5. The log has no session banner: version, channel, OS and schema version are never written, and a normal start logs nothing

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Application/Startup/StartupCoordinator.cs:37`; effort S

**Evidence.** The complete set of log call sites in src (LoggerMessage attributes plus Program.cs:55) contains no startup or shutdown message. StartupCoordinator.StartAsync logs only when the database is not ready (line 66) or tasks were interrupted (line 76). SqliteDatabase logs "Created database" or "Migrated schema" only when those happen. AppEnvironment (CompositionRoot.cs:33-40) holds version, channel and schema version but is used only by settings pages. Program.LogCrash logs "Unhandled exception" with no build identity.

**Failure scenario.** A user sends mindweave-20261113.log with a Critical stack trace. It cannot be tied to a build (the two tracks and several channels coexist by design), an OS, or a schema version, and it is impossible to tell where one process run ends and the next begins, or whether the app was restarted between two errors. Stack line numbers cannot be mapped without knowing the commit.

**Recommendation.** Log one Information line at session start (version including the commit part of InformationalVersion, channel, OS and architecture, .NET runtime, schema version, data directory) and one at clean exit. Include the version in the crash entry as well.

**Skeptic: partially confirmed, low.** Every factual claim checks out; I could not refute it. The severity is overstated: this is a supportability gap with no functional or data impact.

What I verified:
- I enumerated every log call site in src (all [LoggerMessage] attributes plus direct Log* calls). None is a startup, shutdown or build-identity message.
- StartupCoordinator.StartAsync only calls database.OpenAsync and ContinueAfterOpenAsync. That path logs only when the database is not ready, when unfinished work was interrupted, or when the last page could not be read.
- SqliteDatabase logs "Created database" and "Migrated schema" only when those events happen, so a normal start on an existing, current database writes nothing.
- FileLoggerProvider writes no header when it opens or rolls a file. It appends to mindweave-yyyyMMdd.log, so several process runs on one day share a file with no separator.
- AppEnvironment is built in CompositionRoot with the commit part of InformationalVersion stripped by Split('+')[0]. It is consumed only by view models and PageFactory, never by a logger.
- Program.LogCrash logs only "Unhandled exception" plus the exception.
- TRACK.md, docs/design/decisions.md and the other design docs contain no decision that deliberately omits a session banner, and none that requires one.

Why low rather than medium:
- Nothing malfunctions; the cost is slower triage of a log file sent in isolation.
- The log directory sits under the per-channel data directory, so channel and track can be inferred when the log is collected in place.
- An unclean previous run is sometimes visible through the "Marked ... from the previous run as interrupted" warning.
- No requirement or architecture clause mandates a banner.
- The app is pre-release at a constant 0.1.0, so the commit hash is the only useful build identity. It is already embedded in the assembly (obj AssemblyInfo shows 0.1.0+<sha>), which makes the fix a few lines.

The recommendation is sound and cheap.

*Corrections to the finding.* - "Impossible to tell where one process run ends and the next begins" is slightly overstated. A run that follows an unclean exit with unfinished work logs the "previous run ... interrupted" warning (StartupCoordinator.cs:105), and a first run or a migration logs an Information line. For ordinary clean starts the claim holds.
- "Cannot be tied to a build/channel" holds only for a log file sent on its own. In place, the logs directory is under the channel-specific data directory (AppPaths.cs:21).
- AppEnvironment is used by backup dialogs and PageFactory as well as settings pages. All are UI, so the substance is unchanged.
- Severity: medium is high for a diagnostics-convenience gap with no functional impact in a pre-release app; low fits better.

### observability-6. Log volume is unbounded: no per-file size cap, and a paused storage state logs a full exception every 5 seconds

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Application/Storage/StorageHealth.cs:306`; effort S

**Evidence.** StorageHealth recovery loop: `catch (Exception ex) { LogProbeFailed(_logger, ex); continue; }` with `RetryInterval = 5 s` (line 24). The message is Level = Information with the exception attached (lines 357-358). FileLogger.IsEnabled is `>= Information` (FileLoggerProvider.cs:113) and Write appends `exception.ToString()` (line 55). That is 17,280 multi-line stack traces per day for as long as storage stays blocked. FileLoggerProvider has no size limit; Prune (line 84) keeps 14 files by count and runs only in the constructor, so a long-running process is never pruned. logs/ sits inside the data directory, on the same volume as the database.

**Failure scenario.** The database becomes unwritable for a non-transient reason (volume remounted read-only, permissions changed, quota) and the app is left open overnight or for days: the log grows by tens of MB per day of identical stack traces, burying the first, informative failure. When the cause is a full disk, the logger also competes for every freed byte with the retries that are trying to save the user's kept writes.

**Recommendation.** Log the probe failure once per pause, then at most a periodic summary without the stack ('still paused, N probes'). Add a per-file size cap with roll-over and a total budget in FileLoggerProvider, and prune on date roll as well as at startup.

**Skeptic: partially confirmed, low.** Every cited code fact is accurate and I found no guard, throttle, test or design decision that limits it. The recovery loop logs the probe exception at Information on every failed pass (default 5 s), the file logger accepts Information and appends the full exception text, there is no per-file size cap, and Prune runs only in the constructor. So a long-lived paused process does write up to 17,280 near-identical entries per day, and a process that stays up past 14 days is not pruned until restart.

The impact is overstated, though, because logs/ lives inside the data directory and that makes the headline scenarios self-limiting:
- Full disk: the log append fails too and the IOException is swallowed, so the log does not grow. The "competes for freed bytes" point is real but marginal (a couple of KB per 5 s against a probe that itself writes a file to the same directory).
- Volume remounted read-only, or quota exhausted: the log cannot be written either, so again no growth.
- The flood only materialises when the database refuses writes while the data directory stays writable: the DB file locked by another process, corruption, DB-file-only permission change, page-count limit.

In those narrower cases the growth is roughly 17-50 MB per day (my estimate from a StorageWriteException wrapping a SqliteException at 1-3 KB per entry; not measured). That is a nuisance on a desktop machine, not a data-loss or availability risk. The first informative failure is logged at Error ("Core write failed...") while the repeats are INF, so it stays findable by level even if buried by volume. The app is also visibly paused to the user in this state, which makes multi-day unattended runs less likely.

Net: a real log-hygiene defect worth fixing as recommended (log once per pause plus a periodic summary; add a size cap and prune on date roll), but low rather than medium.

*Corrections to the finding.* 1. The claimed failure scenarios (volume remounted read-only, quota, full disk) are mostly ones where the log cannot grow, because logs/ is on the same volume and failed log writes are swallowed (FileLoggerProvider.cs:63-66). The flood needs the database blocked while the data directory is still writable (lock held by another process, corruption, DB-file permissions, page limit).
2. "Burying the first, informative failure" is overstated: the first failure is logged at Error, the repeats at Information, so it is distinguishable by level.
3. "Tens of MB per day" is plausible only at the upper end; roughly 17-50 MB/day depending on stack depth, and it was not measured.
4. "A long-running process is never pruned" is correct but only matters after more than 14 days of continuous uptime; pruning resumes at the next start.
5. Severity medium -> low: bounded disk nuisance and log noise in a narrow state, with no data loss or functional impact.

### observability-7. No diagnostics surface: ai_request is never read for diagnosis, error detail is only a tooltip, and there is no log entry point or diagnostic export

- Final: **low** (confirmed in part); auditor said medium; already known in TRACK.md
- Where: `src/Mindweave.Desktop/Views/Conversations/ConversationView.axaml:243`; effort M

**Evidence.** IAiRequestStore.ForTaskAsync and ForWorkspaceAsync are called only from tests; in src the table is read by SqliteStatisticsStore (token sums) and RecentTokenSamplesAsync. HTTP status, provider request id, timings and outcome are never shown. The visible error is `<TextBlock Name="ErrorText" Text="{Binding StateLabel}" ToolTip.Tip="{Binding ErrorDetail}" />`: hover-only and not copyable. Settings shows version and channel (SettingsHomeView.axaml:199-202) and StorageDetails has 'open data folder', but AppEnvironment.LogDirectory is bound nowhere and no UI text mentions logs or diagnostics. docs/design/evolution-seams.md:30 records this as deliberately deferred to D2.

**Failure scenario.** A user's answer fails with 'server error'. To report it to the provider they need the request id and time, which exist in ai_request but can only be reached by opening mindweave.db with a SQLite tool. To report it to the developer they must retype tooltip text and locate %LOCALAPPDATA%\Mindweave\<channel>\logs themselves. The log line says only "Answer task <guid> finished as Failed (ServerError)", and that guid is not visible anywhere in the UI.

**Recommendation.** As a first D2 step: a copyable 'details' popover on a failed message (kind, detail, HTTP status, provider request id, time, task id), an 'open logs folder' action next to 'open data folder', and a 'copy diagnostic info' button (version, OS, schema). A redacted diagnostic bundle can follow.

**Skeptic: partially confirmed, low.** Every factual claim checked out, but the gap is a documented, deliberate deferral and not a requirements violation, so medium is overstated.

**What holds:**
- `IAiRequestStore.ForTaskAsync` and `ForWorkspaceAsync` have no callers in `src` outside their declaration and implementation; all callers are in tests.
- In `src`, `ai_request` is read only by `SqliteStatisticsStore` (token sums and a count) and `RecentTokenSamplesAsync` (token calibration in `ContextBudgets`).
- The Desktop project never references HTTP status, provider request id or a task id for display; the only `TaskId`-like hit is `ConversationSummaryPanel.RunningSummary`, which is internal state.
- The failed-answer notice is exactly the cited line: a `TextBlock` showing `StateLabel` with `ErrorDetail` as a tooltip. The copy button in the answer's action row copies `Body`, not the error.
- `LogDirectory` is used only in `CompositionRoot` (file logger provider and environment construction) and is bound in no view. A grep of all `.axaml`/`.resx` for logs or diagnostics wording found only unrelated "Dialogs" hits.
- The log line is "Answer task {TaskId} finished as {State} ({Error})", as claimed.

**Why the severity drops:**
- The deferral is recorded in two places: `evolution-seams.md` ("未做（等 D2）: 用户可见的请求详情界面与诊断包导出") and the E3 row in `TRACK.md` ("诊断界面留给 D2"). `decisions.md` also anticipates the D2 diagnostics screen. The auditor acknowledged this.
- `docs/requirements.md` contains no requirement for a diagnostics UI, request-detail view or log access (grep for 诊断/日志/请求 ID/错误详情 returned nothing there), so there is no spec non-conformance.
- The user is not left without information: the redacted provider error text is visible on hover, the state label carries the error kind, and "open data folder" in StorageDetails lands one level above `logs` (`LogDirectory` = `DataDirectory/logs`).
- There is no data loss or correctness impact. The cost is supportability friction, mainly that the provider request id and timing are reachable only through SQLite.

This is a valid backlog item for D2 at low severity. It would be medium only if the product were shipping to external users with a support channel before D2 lands.

*Corrections to the finding.* - Severity should be low, not medium: this is a known, documented deferral to D2 with no requirement behind it, not a newly discovered defect.
- "Must locate the logs folder themselves" is slightly overstated: the existing "open data folder" action opens the directory that contains `logs`.
- "Retype tooltip text" is accurate (the tooltip is not copyable), but the tooltip does expose the redacted provider error detail, so the user is not limited to a bare "server error" label.
- The title's "ai_request is never read for diagnosis" is correct for `src`. The table is read for statistics and token calibration, so it is not dead data.

### observability-8. Connection tests and model listing leave no trace: not in ai_request, not in the log

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Models/ModelSettingsService.cs:177`; effort S

**Evidence.** TestAsync builds a ChatRequest and calls `client.StreamAsync(request, key, ...)` directly, with no RequestTrace and no logger call; its own comment says the request is one "which the provider may bill". Failures are returned as ConnectionTestResult and shown as a status string by ModelSettingsViewModel.TestAsync (line 230). DiscoverModelsAsync (line 169) is the same. ModelSettingsService logs only "Credential cleanup deferred" (line 238).

**Failure scenario.** First-run setup is where provider problems cluster (wrong base URL, key rejected, proxy returning HTML). A user who says 'the test failed and then it worked later' has left no record of status, timing or error. The billed test requests are also absent from the statistics page, which presents ai_request as the usage record.

**Recommendation.** Record connection tests through RequestTrace under an attribution kind (the v10 schema already allows task-less rows, but requires a workspace id, so either relax that or log instead). At minimum log outcome, kind and HTTP status at Information.

### observability-9. FileLoggerProvider only tolerates IOException: an access-denied log directory throws into callers and can prevent startup

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Logging/FileLoggerProvider.cs:63`; effort S

**Evidence.** Write: `try { WriterFor(...).WriteLine(...); } catch (IOException) { // Logging must never take the application down. }`. `new FileStream(path, FileMode.Append, ...)` (line 76) throws UnauthorizedAccessException for a read-only file or a directory ACL, which is not an IOException. The constructor's `Directory.CreateDirectory(directory)` (line 29) is unguarded and runs inside CompositionRoot.Build before the crash hook exists. Prune (line 92) also catches only IOException although File.Delete throws UnauthorizedAccessException on read-only files.

**Failure scenario.** logs/ or today's log file is not writable for this user (restored from a backup with other ownership, marked read-only, or locked down by security software) while the database is fine. Every logger call then throws UnauthorizedAccessException into its caller. Many callers log from inside catch blocks: StorageHealth.cs:306 would end the recovery loop so a pause never clears, and Program.LogCrash would throw inside the unhandled-exception handler. If the directory cannot be created, the app dies at startup with no message.

**Recommendation.** Catch IOException, UnauthorizedAccessException, NotSupportedException and SecurityException in Write, WriterFor, Prune and the constructor; degrade to a no-op logger and surface 'logging unavailable' once.

### observability-10. Redaction edge cases: error text is truncated before the known key is scrubbed, and provider error bodies are stored although the tables are described as content-free

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Ai/ChatCompletionsClient.cs:290`; effort S

**Evidence.** `detail = $"HTTP {status}: {Limit(detail)}";` cuts the provider body to 500 characters (Limit, line 316) and only afterwards does `Redacted(ex, apiKey)` (line 303) run `Redaction.Scrub(ex.Message, apiKey)`, which replaces the exact key string. A key that straddles the 500-character cut is no longer an exact match; what remains is removed only if it fits a pattern in Redaction.cs:31-38 (sk-/pk-/rk-/AIza prefixes, Bearer, labelled secrets) and the remaining fragment is at least 8 characters. Separately, the up-to-500-character provider body is stored verbatim in message.error_detail, generation_task and ai_request.error_detail, while AiRequestRecord's doc comment says the record holds "never content or secrets".

**Failure scenario.** A service with an unprefixed key format (hex or 'id.secret' style) echoes the Authorization value inside a long error body so that the key crosses character 500: the leading part of the key is stored in the database and shown in the message tooltip. Or a moderation or validation error quotes the user's prompt: a fragment of conversation content sits in a table documented as content-free, which matters once diagnostic export (D2) ships.

**Recommendation.** Scrub with the known key before truncating: do it inside ThrowIfFailedAsync and Parse, or pass the key down. Document error_detail as 'provider text, may quote the request' and exclude or re-scrub it in any future diagnostic bundle.

