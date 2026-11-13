# Code quality and maintainability — grade B

The code is clearly above course-project level: methods are short (the longest in src is 121 lines), responsibilities are split deliberately, comments explain why, and the build enforces warnings as errors. It falls short of an A because the same job is implemented several times with different safety. There are three task-runner skeletons, three "stream to completion" loops, four rename paths, and three delete/archive paths, and the copies have already drifted. Error handling relies on per-call-site catch lists with no backstop that writes a terminal state. As a result, one unexpected exception type either strands rows in 'running'/'streaming' until restart or ends the UI loop. I confirmed concrete instances of both by reading the code; none was reproduced by running it, since the audit was read-only. There is also a small amount of dead code and a production API shaped by test seams.

**Strengths**

- Build discipline: Directory.Build.props sets TreatWarningsAsErrors, AnalysisMode=Recommended and EnforceCodeStyleInBuild. All 64 CA1031 suppressions in src are scoped to a single catch and carry a one-line justification; there are no TODO/HACK/FIXME markers.
- Method and class size is controlled. A brace-matching scan of src found the longest method at 121 lines (AnswerRunner.RunAsync) and only about 10 methods over 70 lines. GenerationCoordinator delegates to AnswerRunner, TaskRunners, TerminalWrites and GenerationRequests, each documented as holding no scheduler state.
- Stored enum values are mapped explicitly in src/Mindweave.Infrastructure/Sqlite/SqlNames.cs, so renaming a C# member cannot silently change stored data; unknown values throw InvalidDataException.
- Policy numbers are mostly named constants with a reason attached, e.g. ConversationTitler.TitleOutputTokens (cites the reasoning-model finding), ContextCompressor.SummaryAttempts, SummaryRules.ReadThreshold, ConversationService.EndingAnswerWait, ConversationDeletionViewModel.ChangeHold.
- Concurrency intent is written where it matters: GenerationCoordinator assigns Running.Completion under the lock, cancels idle work outside it, and explains why the CancellationTokenSource is not disposed. SerialRefresh and UiThrottle are small, documented primitives reused by 9 view models.
- Outcome types instead of exceptions for expected refusals (SendOutcome/SendRejection, SummarizeOutcome, ArchiveOutcome, ConversationDeleteOutcome) keep most UI call sites free of try/catch.
- Layering is real: Desktop reaches services through narrow interfaces (IConversations, ISummaries, IWorkspaces), CompositionRoot is the only place Desktop references Infrastructure, and the provider is built with ValidateOnBuild and ValidateScopes.

**Not examined**

- Views and code-behind: NetworkCanvas.cs (736 lines), AnswerView.axaml.cs, GhostTextBox.cs, ShellView.axaml.cs, Motion.cs, UsageChart.cs, and all XAML styles/themes.
- Markdown/SourceAnchors.cs and the anchor/locator logic.
- Internals of SqliteDatabase.cs, SqliteBackupFiles.cs, SqliteWriteQueue.cs, SchemaMigrations.cs and most SQL text; only state-literal usage and SqlNames were sampled.
- StorageHealth recovery loop, TerminalWrites, SummaryDrafts.cs, ReadingStateService.cs, WorkspaceNamePreparation.cs, UsageStatistics and UsageStatisticsViewModel.cs, NetworkLayout/NetworkSession, ExitCoordinator.cs, BackupDialogs.cs, the credential stores.
- tests/** was only grepped to check whether specific members or cases are covered; test quality was not reviewed.
- No build, analyzer, formatter or test run was performed (read-only audit), and the baseline output files in the scratchpad were not consulted. The unused-member list comes from a regex word-count heuristic plus manual grep confirmation, not a Roslyn analysis, so it is a lower bound.
- The crash and stuck-state scenarios in findings 1-3 were derived by reading code paths and known .NET/CommunityToolkit behaviour (JsonNode indexer, AsyncRelayCommand.Execute, async void without a SynchronizationContext); none was reproduced by running the app.
- TRACK.md was read only in part (headings, pending decisions, and a keyword search of the findings section); a finding recorded there in different wording could have been missed.

### maintainability-1. ChatCompletionsClient leaks InvalidOperationException for valid JSON of an unexpected shape, which crashes the model settings page and strands answers

- Final: **high** (confirmed); auditor said high
- Where: `src/Mindweave.Infrastructure/Ai/ChatCompletionsClient.cs:283`; effort S

**Evidence.** Line 283: `detail = JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? text;` sits inside `catch (JsonException)` only. Line 244-246 does the same for stream events (`error["message"]`), line 249 calls `node?["choices"]?.AsArray()`, and line 134 calls `json?["data"]?.AsArray()`. The JsonNode string indexer and AsArray() throw InvalidOperationException when the node is not an object/array. That happens for an error body such as `{"error":"Model is overloaded"}` (error as a string, a shape some OpenAI-compatible gateways use) or any event whose `choices`/`delta` is not the expected type. StreamAsync (line 56) and ListModelsAsync (line 117) only translate ChatClientException. Callers catch narrowly: ModelSettingsService.TestAsync (lines 195-202) catches ChatClientException and CredentialUnavailableException; ModelSettingsViewModel.RunAsync (lines 279-290) catches ModelSettingsException, ChatClientException and CredentialUnavailableException; AnswerRunner.RunAsync (lines 136-146) catches OperationCanceledException and ChatClientException. ChatCompletionsClientTests only covers the `{"error":{"message":...}}` shape (lines 126-175).

**Failure scenario.** The user points Mindweave at a gateway that answers 401/429/500 with `{"error":"..."}` and presses Test connection or Discover models. InvalidOperationException escapes the [RelayCommand] task, AsyncRelayCommand rethrows it on the UI thread, and no dispatcher-level handler exists (Program.cs only logs AppDomain/TaskScheduler events), so the UI loop ends and the app exits. The same response during an answer takes the path in the next finding: the answer is never finished and the conversation is locked until restart.

**Recommendation.** Make the client's contract total: wrap Parse, ThrowIfFailedAsync and the ListModels projection so InvalidOperationException, FormatException and IOException become ChatClientException(InvalidResponse/Network). Read fields with type checks (`as JsonObject`, `as JsonArray`) instead of indexers. Add test rows for `{"error":"text"}`, a top-level JSON array or string, and `choices` as a non-array.

**Reproduction attempt: confirmed, medium.** Could not refute. The code is as cited, and I reproduced the exception behaviour of System.Text.Json on net10.0 with a throwaway console project in the scratchpad (outside the worktree).

Client: ThrowIfFailedAsync (line 283) catches only JsonException around `JsonNode.Parse(text)?["error"]?["message"]`. Parse() (244-250) uses the string indexer on `node`, `error`, `choice` and `choice["delta"]`, plus `AsArray()`, with no catch. ListModelsCoreAsync (134-135) uses `AsArray()` and `GetValue<string>()` and catches only OperationCanceled/HttpRequest/JsonException. The public wrappers (56, 117) translate only ChatClientException. There is no type guard anywhere on these paths.

Callers: none catches InvalidOperationException on these paths.
- ModelSettingsService.TestAsync (195-202) catches ChatClientException and CredentialUnavailableException; DiscoverModels (173) catches nothing.
- ModelSettingsViewModel.RunAsync (279-290) catches ModelSettingsException, ChatClientException, CredentialUnavailableException.
- Both commands are [RelayCommand] Task methods (CommunityToolkit.Mvvm 8.4.2, no FlowExceptionsToTaskScheduler option anywhere in src), so the exception is rethrown on the Avalonia dispatcher. The only global handlers are AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException in Program.cs:36-37, and both only log. AppHost.Run (36-48) is try/finally with no catch. So the process exits; the finally block still flushes writes, closes the database and releases the lock.
- AnswerRunner.RunAsync (136-146) catches OperationCanceledException and ChatClientException … (cut; full text in the workflow journal)

*Corrections to the finding.* 1. "The conversation is locked until restart" is not verified here and is partly contradicted. GenerationCoordinator.RunAsync's finally (536-547) removes the task from _running and pumps the queue, so the scheduler slot is freed. What is confirmed is that the LiveAnswer is never finished, the message is never persisted as Failed, no AnswerEnded/AnswerFinished event is raised, and no request trace is recorded. Whether the UI then blocks further sends in that conversation was not traced.

2. The answer path is not a crash. The exception is caught and logged by the coordinator; only the model settings page (Test connection, Discover models) takes the app down.

3. The settings-page crash is an orderly exit, not an abrupt kill: AppHost.Run's finally still flushes kept writes, closes the database and releases the single-instance lock.

4. One more trigger the finding did not list: a /models e … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Throwaway console project (net10.0) in the session scratchpad, running the same expressions the client uses. Output:

error-string http  `{"error":"Model is overloaded"}` ?["error"]?["message"]  -> InvalidOperationException: The node must be of type 'JsonObject'.
top-level array    `[1]` ?["error"]                                         -> InvalidOperationException: The node must be of type 'JsonObject'.
top-level string   `"oops"` ?["error"]                                      -> InvalidOperationException: The node must be of type 'JsonObject'.
top-level number   `5` ?["error"]                                           -> InvalidOperationException: The node must be of type 'JsonObject'.
choices non-array  `{"choices":{}}` .AsArray()                              -> InvalidOperationException: The node must be of type 'JsonArray'.
choice elem string `{"choices":["x"]}` first?["delta"]                      -> InvalidOperationException: The node must be of type 'JsonObject'.
delta string       `{"choices":[{"delta":"x"}]}` ?["delta"]?["content"]     -> InvalidOperationException: The node must be of type 'JsonObject'.
data non-array     `{"data":{}}` .AsArray()                                 -> InvalidOperationException: The node must be of type 'JsonArray'.
id number          `{"data":[{"id":5}]}` GetValue<string>()                 -> InvalidOperationException: An element of type 'Number' cannot be converted to a 'System.String'.
empty body         JsonNode.Parse("") … (cut; full text in the workflow journal)

</details>

**Reproduction attempt: confirmed, high.** The client defect reproduces exactly as claimed. A focused xUnit theory against track/claude-auto @ 1ba95c9 fed ChatCompletionsClient 21 well-formed JSON responses of an unexpected shape; all 21 leaked System.InvalidOperationException from the public methods StreamAsync / ListModelsAsync. The one control row with the expected shape ({"error":{"message":"x"}}) passed.

Three independent leak sites, all reproduced:
1. ThrowIfFailedAsync (line 283): a non-2xx body of {"error":"..."}, {"error":[...]}, a top-level array or a top-level string throws "The node must be of type 'JsonObject'". Only JsonException is caught. Reached from both StreamAsync and ListModelsAsync.
2. Parse (lines 244-253): a stream event of {"error":"..."}, choices as object or string, choices:["x"], delta as string, or a top-level array/string/number throws InvalidOperationException.
3. ListModelsCoreAsync (lines 134-135): data as object or string, a top-level array or string, data:["a"], and data:[{"id":1}] (GetValue<string>() on a number) all throw. The last is an extra shape the finding did not list.

The outer wrappers (lines 56, 117) only translate ChatClientException, so nothing converts these.

Downstream consequences were verified by code reading only, not by a test:
- ModelSettingsService.TestAsync catches only ChatClientException and CredentialUnavailableException; DiscoverModelsAsync has no catch. ModelSettingsViewModel.RunAsync catches ModelSettingsException, ChatClientException and CredentialUnavailableException, so InvalidOperationException escapes the [RelayCommand] task for both Test and Dis … (cut; full text in the workflow journal)

*Corrections to the finding.* Nothing in the finding is wrong; three points are narrower or broader than stated.

1. Not reproduced by test: the UI-thread crash and the "conversation locked until restart" outcome. Both are supported by code reading (narrow catches, no dispatcher handler, FinishAsync skipped) but I did not run the desktop app or an AnswerRunner test. Treat the app-exit and lock-until-restart wording as expected, not observed.

2. The scheduler itself is not harmed: GenerationCoordinator.RunAsync (lines 525-530) catches every exception, logs it and frees the slot. The damage on the answer path is limited to the one answer never reaching a terminal state and its request trace never being recorded.

3. The finding understates the ListModels surface. Besides data not being an array, data:["a"] and data:[{"id":1}] also leak, the latter via GetValue<string>() on a number (line 135). The fix needs type check … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Temporary file tests/Mindweave.Infrastructure.Tests/Chat/HealthCheckReproShapeTests.cs (deleted afterwards). Run with:
dotnet test tests/Mindweave.Infrastructure.Tests --filter "FullyQualifiedName~HealthCheckReproShapeTests"

Test code (helpers: Client wraps a stub HttpMessageHandler returning the given response; Stream enumerates client.StreamAsync to the end):

[Theory]
[InlineData(HttpStatusCode.Unauthorized, "{\"error\":\"Invalid API key\"}")]
[InlineData(HttpStatusCode.TooManyRequests, "{\"error\":\"Model is overloaded\"}")]
[InlineData(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}")]
[InlineData(HttpStatusCode.InternalServerError, "[\"boom\"]")]
[InlineData(HttpStatusCode.InternalServerError, "\"boom\"")]
[InlineData(HttpStatusCode.BadRequest, "{\"error\":[{\"message\":\"x\"}]}")]
[InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"x\"}}")]   // control
public async Task Http_error_body_of_unexpected_json_shape(HttpStatusCode status, string body)
{
    var ex = await Record.ExceptionAsync(() => Stream(_ => new HttpResponseMessage(status) { Content = new StringContent(body) }));
    Assert.IsType<ChatClientException>(ex);
}

[Theory]
[InlineData("{\"error\":\"upstream overloaded\"}")]
[InlineData("{\"choices\":{\"delta\":{\"content\":\"a\"}}}")]
[InlineData("{\"choices\":\"none\"}")]
[InlineData("{\"choices\":[\"x\"]}")]
[InlineData("{\"choices\":[{\"delta\":\"x\"}]}")]
[InlineData("[1,2]")]
[InlineData("\"hello\"")]
[InlineData("42")]
public as … (cut; full text in the workflow journal)

</details>

### maintainability-2. The scheduler's catch-all frees the slot but writes no terminal state: any unexpected exception in a runner leaves 'running'/'streaming' rows and a live answer that never ends

- Final: **high** (confirmed); auditor said high
- Where: `src/Mindweave.Application/Conversations/GenerationCoordinator.cs:531`; effort M

**Evidence.** GenerationCoordinator.RunAsync lines 530-535: `catch (Exception ex) { LogRunCrashed(...); }`, then the finally removes the entry from `_running`. Nothing calls FinishAsync or FinishTaskAsync. AnswerRunner.RunAsync (lines 97-151) reaches FinishAsync only when the stream ends, is stopped, or throws ChatClientException; on any other exception `using var checkpointStop` is disposed without being cancelled, so CheckpointLoopAsync's Task.Delay loop never ends. TaskRunners has the same shape for titles, summaries and naming. The database decides what is busy from these rows: SqliteArchiveStore.UnfinishedUserWork (lines 346-352) tests `state IN ('queued','running')`, SqliteSummaryStore line 403 does the same for summaries, and ConversationService.AskAsync line 250 rejects when the stored history has an unfinished answer. Rows are only repaired by RecoverUnfinishedAsync at the next start (SqliteConversationStore line 243).

**Failure scenario.** A provider sends one stream event that trips the exception in the previous finding (or any future non-ChatClientException from compression, tracing or the store). The answer bubble keeps showing 'streaming' and the composer stays blocked. Stop does nothing, because StopAsync finds the answer neither queued nor running. Asking again returns AnswerInProgress, and archive, delete and workspace delete report the conversation as busy, until the user restarts the app. For a summary task, SummarizeAsync returns SummaryInProgress until restart. For a title, the titler's `_titleRequested` mark is never removed, so the conversation gets no automatic title for the rest of the session.

**Recommendation.** Give every work type a single `FailUnexpectedAsync(work, ex)` that the coordinator's catch-all calls: finish the LiveAnswer as Failed, write the terminal message/task row through TerminalWrites, and raise TitleAbandoned/SummaryTaskFinished. Cancel the checkpoint token in a finally. Add a fault-injection test where the chat client throws InvalidOperationException mid-stream and assert the task row is terminal and the conversation accepts a new question.

**Skeptic: confirmed, high.** I could not refute the finding. The code is as described, and I found two triggers that are reachable in today's code, not only hypothetical future ones. Nothing was executed (read-only audit), so this rests on code reading.

**The mechanism holds.**
- `GenerationCoordinator.RunAsync` catches `Exception`, logs "Generation task {TaskId} crashed", and the `finally` only removes the entry from `_running`, pumps and raises `Changed`. It writes no terminal state and does not finish the `LiveAnswer`.
- `AnswerRunner.RunAsync` catches only `OperationCanceledException` (on stop) and `ChatClientException` around the stream. The compression block catches only cancellation, `ContextCompressionException` and `StorageAdmissionRefusedException`. Any other exception skips `FinishAsync`.
- The checkpoint token source is disposed without being cancelled, so `CheckpointLoopAsync` (`while (true)` on `Task.Delay`) never exits. This is a small permanent leak per crashed answer.
- `TaskRunners` title, summary and naming have the same catch lists and no generic catch around the request.

**Reachable triggers today.**
- **Malformed stream event.** `ChatCompletionsClient.Parse` wraps only `JsonNode.Parse` in a `JsonException` catch. `node?["error"]`, `error["message"]` and `node?["choices"]?.AsArray()` throw `InvalidOperationException` when the node is not the expected shape. A 200-status SSE event such as `{"error":"rate limited"}` (error as a string, common on OpenAI-compatible relays), a bare scalar or array, or a non-array `choices` does this. `StreamAsync` rewraps only `ChatClientException`, s … (cut; full text in the workflow journal)

*Corrections to the finding.* - **"The previous finding" is not needed as a trigger.** Concrete triggers exist in the cited code: `ChatCompletionsClient.Parse` lines 244-249 on a non-object event, a string-valued `error`, or a non-array `choices`; and unguarded `summaries.ListAsync` / `AddAsync` in `ContextCompressor.PrepareAsync`. "Tracing" is not a trigger: `RecordAsync` and `RecordCompressedInputAsync` swallow everything, as does `TerminalWrites.SaveAsync`.
- **Summary and naming already raise `SummaryTaskFinished`.** `RunSummaryAsync` and `RunNamingAsync` do so in a `finally`, even on an unexpected exception. Only the terminal row write is missing there, so that part of the recommendation applies to titles and answers only.
- **The title consequence is weaker than stated.** `_titleRequested` is also left set on an ordinary `ChatClientException` failure, by design ("never retry automatically"). No automatic title … (cut; full text in the workflow journal)

**Reproduction attempt: confirmed, high.** Reproduced with three focused xUnit tests against the real coordinator, services and SQLite stores (ChatHarness), using a chat client that yields one TextDelta and then throws InvalidOperationException. Code under test is track/claude-auto at commit 1ba95c9, checked out into the isolated worktree.

All three tests assert the correct behaviour and all three fail, so the finding is real for answers, summaries and titles:

- Answer: after the crash the scheduler reports 0 unfinished (slot freed), but the stored message stays Streaming, the generation_task row stays 'running', and the LiveAnswer stays Streaming, even after a 1.5 s grace period. StopAsync changes nothing. AskAsync returns AnswerInProgress. ArchiveAsync reports the conversation as busy.
- Summary: the task row stays Running and a second SummarizeAsync is rejected with SummaryInProgress.
- Title: the title task row stays 'running'. A later successful answer in the same conversation sends no second title request (1 title request in total) and the conversation keeps TitleSource None.

Code reading agrees. GenerationCoordinator.RunAsync's catch-all only logs, and its finally only removes the entry from _running. AnswerRunner catches only OperationCanceledException and ChatClientException around the stream, and the checkpointStop cancel and FinishAsync sit after the try with no finally. TaskRunners.RunTitleAsync and RunSummaryAsync have the same typed-catch shape. Only RunTransientAsync has a real catch-all.

Severity stays high. The impact is a conversation locked until restart (ask, stop, archive, summarize), with n … (cut; full text in the workflow journal)

*Corrections to the finding.* - The checkpoint loop does not run forever in the answer case: `using var checkpointStop` disposes the CTS without cancelling it, so the loop keeps polling every CheckpointInterval while the app runs, but it only writes while the live state is Streaming and the revision advances. In practice it is a leaked idle timer loop per crashed answer, not a growing cost. I did not measure the leak directly.
- "Stop does nothing" is confirmed at the service level (StopAsync leaves message Streaming and task 'running'); the UI symptoms (bubble, composer) are inferred, not tested.
- Workspace delete and conversation delete reporting busy were not exercised; only ArchiveAsync was, and it does report the conversation busy.
- Naming tasks were not exercised; the same catch shape is visible in TaskRunners.RunNamingAsync.
- The trigger's likelihood is unproven: the scenario depends on a non-ChatClientExce … (cut; full text in the workflow journal)

<details><summary>Reproduction</summary>

Temporary file tests/Mindweave.Infrastructure.Tests/Chat/ReproUnexpectedRunnerExceptionTests.cs (deleted afterwards). Built with `dotnet build tests/Mindweave.Infrastructure.Tests` (0 warnings, 0 errors); run with `dotnet test tests/Mindweave.Infrastructure.Tests --no-build --filter "FullyQualifiedName~ReproUnexpectedRunnerException"`.

Core of the test code:

```csharp
private sealed class CrashingClient(FakeChatClient inner) : IChatClient
{
    public Func<ChatRequest, bool> CrashWhen { get; set; } = _ => false;
    public TaskCompletionSource Crashed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken ct)
    {
        if (CrashWhen(request))
        {
            await Task.Yield();
            yield return new ChatStreamEvent.TextDelta("半句");
            Crashed.TrySetResult();
            throw new InvalidOperationException("simulated unexpected failure inside the stream");
        }
        await foreach (var item in Inner.StreamAsync(request, apiKey, ct)) yield return item;
    }
    ...
}

[Fact] // answer
var client = new CrashingClient(new FakeChatClient()) { CrashWhen = FakeChatClient.IsAnswer };
await using var h = await ChatHarness.StartAsync(client: client);
var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
var answer = sent.Answer!.Value;
await client.Crashed.Task.WaitAsync(TimeSpan.FromS … (cut; full text in the workflow journal)
```

</details>

### maintainability-3. Renaming a conversation from its page while storage is paused throws an unhandled StorageWriteException; four rename paths each handle failure differently

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/Conversations/ConversationViewModel.cs:296`; effort S

**Evidence.** Lines 295-300: `private async Task CommitRenameAsync() { IsRenaming = false; await _conversations.RenameAsync(Id, RenameText, CancellationToken.None); }` has no try/catch. ConversationService.RenameAsync goes through CommitAsync (line 519), which calls `_health.ThrowIfBlocked()`; StorageHealth documents that it throws StorageWriteException because 'callers already handle that'. The RenameButton in ConversationView.axaml line 14 is not gated on IsStorageBlocked. The other rename paths do handle it: ConversationListViewModel.RenameAsync lines 377-384 catches StorageWriteException only; WorkspacesViewModel.CommitCoreAsync lines 215-234 catches StorageWriteException and then Exception; SummaryViewModel routes through RunAsync (lines 482-499), which catches SummaryRefusedException and StorageWriteException. No test executes the page's CommitRenameCommand (only WorkspacesPageTests, GalleryTests and RowMenuUiTests reference a CommitRenameCommand).

**Failure scenario.** The disk fills up and storage pauses, with the page showing the pause banner. The user clicks rename in the conversation toolbar, types a title and presses Enter. StorageWriteException leaves the AsyncRelayCommand, is rethrown on the UI thread with no handler, and the application exits, in exactly the state the design otherwise handles as a recoverable pause.

**Recommendation.** Introduce one shared guard for storage commits in view models (a generalised form of SummaryViewModel.RunAsync) and route every rename/archive/restore/delete command through it. Add a dispatcher-level unhandled-exception policy so a missed catch shows a notice instead of ending the loop. Add a page-rename-while-paused test.

**Skeptic: confirmed, medium.** I could not refute it by reading the code; nothing was executed (read-only audit), so the crash itself is inferred from the code path rather than observed.

What holds:
- The page's CommitRenameAsync awaits IConversations.RenameAsync with no try/catch.
- ConversationService.RenameAsync goes through CommitAsync, which calls _health.ThrowIfBlocked() first. That method is documented to throw StorageWriteException while storage is blocked.
- Neither BeginRenameCommand nor CommitRenameCommand has a CanExecute. RenameButton and SaveRenameButton are bound only to IsRenaming; IsStorageBlocked only drives the notice panel, retry, the composer and summarise. So the path is reachable while the pause banner is showing.
- No dispatcher-level handler exists. A grep of src for FlowExceptionsToTaskScheduler and Dispatcher UnhandledException found nothing. Program.cs only logs from AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException. AppHost.Run is try/finally with no catch.
- With CommunityToolkit.Mvvm 8.4.2 defaults, a faulted AsyncRelayCommand started via ICommand.Execute rethrows on the UI synchronization context, so the exception ends the UI loop. The finally block still flushes and closes the database, so this is a process exit without data loss.
- The sibling paths do catch it: ConversationListViewModel.RenameAsync catches StorageWriteException and sets a RowNotice.
- No test runs the page's CommitRenameCommand. ConversationPolishTests only executes BeginRenameCommand and checks button styling; RowMenuUiTests sets the page's RenameText but commits through the row. … (cut; full text in the workflow journal)

*Corrections to the finding.* The finding's facts about the page rename path are accurate. Three qualifications:
- The crash is inferred from code plus CommunityToolkit.Mvvm's default AsyncRelayCommand behaviour; it was not reproduced.
- The exit is not abrupt for data: AppHost.Run's finally block still flushes kept writes, closes the database and releases the single-instance lock, and AppDomain.UnhandledException logs it.
- I did not re-read the WorkspacesViewModel and SummaryViewModel handlers the finding cites for the "four paths handle it differently" claim; I verified only the ConversationListViewModel one.

### maintainability-4. Three copies of the task-run skeleton and of 'stream one request to completion' have drifted: naming does not guard its apply step or the closed-queue case that summaries and titles guard

- Final: **low** (confirmed); auditor said medium
- Where: `src/Mindweave.Application/Conversations/TaskRunners.cs:178`; effort M

**Evidence.** RunTitleAsync, RunSummaryAsync and RunNamingAsync (lines 29-74, 76-155, 157-200) repeat mark-running, read-key, request, and a four-arm catch ladder. Drift 1: RunSummaryAsync wraps `work.Save(output)` (lines 108-128) in catch (Exception), reports a StorageWriteException to StorageHealth and writes Failed with retryOnFailure. RunNamingAsync line 178 awaits `work.Apply(output)` unguarded; Apply calls `_summaries.ApplyNamesAsync` (SummaryNaming.cs), which can throw StorageWriteException. Drift 2: ConversationTitler lines 125-137 catches InvalidOperationException from `_generation.Enqueue`, removes its mark and cancels the stored row. SummaryNaming lines 151-168 does TryAdd on `_namingQueued`, AddTaskAsync, then Enqueue with no such handling; the outer ScheduleNamingSafelyAsync only logs. Drift 3: the collect-until-Completed loop exists in GenerationRequests.RequestCompleteTextAsync (lines 71-129), ContextCompressor.RequestSummaryAsync (lines 162-204) and ModelSettingsService.TestAsync. The problem strings differ ('回答流未正常结束。' vs '回答流未正常结束'), and the compressor stores a mixed-language detail (`$"Summary unusable: {problem}."`). SummaryOutputTokens = 16_384 is declared twice (ContextCompressor.cs:46, SummaryService.cs:56).

**Failure scenario.** A storage write fails while names are applied: the exception leaves RunNamingAsync, the coordinator logs 'crashed', the naming row stays 'running' until restart, and StorageHealth is not told, so the app does not pause until some other write fails. Separately, if an exit begins between the naming schedule's checks and its Enqueue and the user then returns to the app (Reopen), the row stays 'queued' and the `_namingQueued` key is never removed, so that same-name group is not named again in the session. The next fix applied to one runner (for example a new exception type) will again miss the other two.

**Recommendation.** Extract one `RunTaskAsync(task, body)` template that owns mark-running, the key read, the catch ladder and the terminal write, with per-kind hooks for 'use output' and 'on abandoned'. Make ContextCompressor and TestAsync call a single complete-text helper. Move the closed-queue compensation into a shared 'store task then enqueue' helper used by titler, naming and summaries. Keep one SummaryOutputTokens constant.

**Skeptic: confirmed, low.** I could not refute any factual claim; every cited piece of code exists and behaves as described. I lowered the severity because both concrete failures need rare preconditions and self-heal on restart.

**Drift 1 (unguarded apply) is real.**
- `RunNamingAsync` awaits `work.Apply(output)` with no guard, and its catch ladder only handles `OperationCanceledException`, `ChatClientException` and `StorageAdmissionRefusedException`.
- The Apply lambda in `SummaryNaming` is try/finally with no catch, and it calls `ApplyNamesAsync`, which goes through `database.WriteAsync`; that surfaces storage conditions as `StorageWriteException`.
- The exception therefore reaches the coordinator's catch-all, which only logs "Generation task {TaskId} crashed". No `FinishTaskAsync` runs and `_health.Report` is not called on this path, so the row stays `running`.
- `RunSummaryAsync` guards the equivalent step: catch-all, `_health.Report` for `StorageWriteException`, Failed write with `retryOnFailure`.
- The row is only cleared at next start, by the startup sweep that marks `queued`/`running` rows `interrupted`.
- No test covers a naming apply failure (grep of `tests/` found nothing).

**Drift 2 (closed queue) is real.**
- `ConversationTitler` checks `IsClosed`, then catches `InvalidOperationException` from `Enqueue`, removes its mark and writes Cancelled.
- `ScheduleNamingAsync` does `_namingQueued.TryAdd`, `AddTaskAsync`, `_namingTasks[...] =`, then `Enqueue` with no handling. `Enqueue` throws when `_closed`.
- `ScheduleNamingSafelyAsync` only logs. The `_namingQueued` key is removed only in the Ap … (cut; full text in the workflow journal)

*Corrections to the finding.* - The title's "closed-queue case that summaries and titles guard" is imprecise. Only `ConversationTitler` guards the closed queue. `SummaryService.cs:209` enqueues with no `IsClosed` check and no catch, so summaries share the naming gap (the recommendation implicitly acknowledges this). Summaries guard only the apply/save step.
- "The app does not pause until some other write fails" is accurate but overstated as harm: under a real storage fault, the next terminal write or service write reports to `StorageHealth` almost immediately.
- The stuck `running` naming row does not block archiving. It may count toward the credential-in-use query at `SqliteConversationStore.cs:253` until restart; I did not trace that query's callers.
- In drift 1 the `_namingQueued` key is released by the Apply finally, so only the task row is stuck, not the dedupe key. The finding does not claim otherwise.
- In d … (cut; full text in the workflow journal)

### maintainability-5. The 'async void on purpose' comment in ConversationService is wrong about where exceptions go: on worker threads they terminate the process

- Final: **low** (not verified); auditor said low; already known in TRACK.md
- Where: `src/Mindweave.Application/Conversations/ConversationService.cs:616`; effort S

**Evidence.** Lines 616-618: `// async void on purpose: an exception from a ConversationChanged subscriber goes to the synchronization context, never back into the generation worker that raised AnswerFinished.` followed by `private async void OnAnswerFinished(...)`. AnswerFinished is raised from AnswerRunner.FinishAsync after `.ConfigureAwait(false)` inside `Task.Run` (GenerationCoordinator line 487), so there is no SynchronizationContext. An exception from an async void method without a context is rethrown on the thread pool and is unhandled; Program.cs line 36 only logs it. TRACK.md line 95 records that changing this handler to synchronous was rejected in review because subscriber exceptions then reached the generation thread, so the choice is known, but the stated consequence is not what the code does.

**Failure scenario.** A future ConversationChanged subscriber throws (today's subscribers only post to the dispatcher or request a throttled reload, so this is latent). Instead of one logged failed run, the whole process is torn down mid-generation, after the terminal write but before the request trace is recorded.

**Recommendation.** Raise ConversationChanged inside a try/catch that logs, and start the titler with an explicitly observed task (`_ = _titler.OnAnswerCompletedAsync(e)`, which already never throws). Then the handler can be a plain synchronous method and the comment can state the real behaviour.

### maintainability-6. One storage-failure constant named for archiving is reused for rename, restore and delete, and its 'nothing was changed' claim is false after a partial multi-archive

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/ViewModels/ConversationArchiving.cs:26`; effort S

**Evidence.** UiText.ArchiveDelete.cs line 9: `ArchiveStorageFailed = "本地数据暂时无法保存，没有做任何改动。请稍后再试。"`. It is used at 13 call sites, including sidebar rename (ConversationListViewModel.cs:383), restore (ConversationLifecycleActions.cs:188, ArchivedViewModel.cs:116), conversation delete (ConversationDeletionViewModel.cs:201) and workspace delete (WorkspaceDeletionViewModel.cs:175). ConversationArchiving.ArchiveAllAsync (lines 15-32) archives roots one by one and on StorageWriteException returns `(busy, true)` without saying how many already succeeded; ConversationListViewModel lines 239-243 and ConversationDeletionViewModel line 251 then show the constant. Workspace creation takes a third route: WorkspaceNameForm shows `UiText.SaveFailed(ex.Message)` (OnboardingViewModel.cs:40), which is the raw exception text, including English SQLite messages. Related naming drift: the conversation-delete dialog reuses UiText.WorkspaceDeleteBackupFailed, UiText.DeleteWorkspaceBusy and UiText.WorkspaceDeleting for non-workspace cases.

**Failure scenario.** The user selects five conversations and archives them; the third write hits a storage error. Two conversations are already archived and disappear from the list, while the notice says no change was made. Separately, a failed workspace creation shows text like `SQLite Error 13: 'database or disk is full' (extended code ...)` in an otherwise Chinese UI, while the same failure on rename shows a friendly sentence.

**Recommendation.** Rename the constant to what it means (StorageWriteRefused) or split it per action. Have ArchiveAllAsync return the archived count and use a 'N archived, the rest not saved' message. Map StorageWriteException to the same friendly text in WorkspaceNameForm rather than ex.Message.

### maintainability-7. GenerationCoordinator._live is never pruned: every answer's full text is retained for the life of the session

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Conversations/GenerationCoordinator.cs:197`; effort S

**Evidence.** Line 27 declares `ConcurrentDictionary<MessageId, LiveAnswer> _live`. The only write is line 197 (`_live[answer.Live.MessageId] = answer.Live;`) and the only read is Observe at line 140; a grep for `_live` in the Application project finds no Remove/TryRemove. LiveAnswer holds a StringBuilder with the whole answer. Entries survive terminal states, retries (the superseded answer stays), deletion of the conversation and deletion of the workspace. ConversationViewModel.NewItem (line 427) calls Observe for every message on every page build, so finished answers are served from this map indefinitely.

**Failure scenario.** A long-running session (the app is designed to stay open with background generation) accumulates one retained LiveAnswer per answer ever generated, including those of deleted conversations, whose text is otherwise removed from storage. Memory grows without bound. The next change that assumes Observe returns null for finished answers, or that deleted content is gone from memory, will be wrong.

**Recommendation.** Remove the entry once the terminal write has landed and AnswerFinished has been raised (the page reloads from storage on ConversationChanged), or at least in NotifyDeleted and on workspace deletion. Document on Observe whether terminal answers are returned.

### maintainability-8. Dead members and test-only surface in production code

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Desktop/Converters.cs:18`; effort S

**Evidence.** A word-count scan over src (*.cs and *.axaml), confirmed with grep, found members referenced nowhere but their declaration: Converters.BackupLabel, FileSize and IsBeforeDelete (Converters.cs:18, 24, 29); ConversationService.StorageStateChanged (ConversationService.cs:133); Workbench.AddWorkspace (Workbench.cs:84); UiText.RetryAttempt, GraphView, StatisticsScopeLabel, StatisticsPeriodLabel, SummaryRetry and ExploreEmpty. Commands with no XAML binding that only tests execute: NetworkHomeViewModel.ToggleViewCommand (GalleryTests.cs:350) and ShellViewModel.ToggleCreateWorkspaceCommand (NavigationTests.cs:79, 103). GenerationCoordinator.UnfinishedCount and UnfinishedAnswerCount are public and used only by tests. 37 comments in src mark members as existing for tests ('tests await it', '(tests)'), e.g. WorkspacesViewModel exposes Loaded, Renaming, Suggesting, KeptNamesRead and IsDisposed. GenerationTaskKind.NameSuggestion exists (IConversationStore.cs:23) but SqlNames.Of(GenerationTaskKind) has no arm for it and would throw ArgumentOutOfRangeException if such a task were ever stored.

**Failure scenario.** Tests that drive ToggleCreateWorkspaceCommand or ToggleViewCommand pass while no user can reach that path, so they give false assurance about the real UI. A developer who decides to persist transient name-suggestion tasks (the record type already carries a kind for it) gets a runtime ArgumentOutOfRangeException from SqlNames rather than a compile error.

**Recommendation.** Delete the unused members and strings. Either bind the two commands or remove them and their tests. Enable an unused-member check (IDE0051/IDE0052 as errors, plus a periodic unused-public-member report). Either map NameSuggestion in SqlNames or stop modelling transient work as a GenerationTaskRecord with a storable kind.

### maintainability-9. Near-total public surface in Application and concrete-class coupling behind the interfaces

- Final: **info** (not verified); auditor said info
- Where: `src/Mindweave.Application/Conversations/ConversationDeletion.cs:38`; effort M

**Evidence.** Type declarations counted by regex: Application has 214 public and 9 internal types; Desktop has 164 public and 21 internal, although it is an executable with InternalsVisibleTo for its tests. Services meant to be reached through tailored interfaces are public concrete classes that depend on each other directly: ConversationDeletionService's constructor (lines 38-44) takes `GenerationCoordinator generation, ConversationService conversations` and calls the internal `conversations.NotifyDeleted`; ConversationService and SummaryService take the concrete GenerationCoordinator, StorageHealth and ModelSettingsService. Application's DI registration lives in Mindweave.Infrastructure/ApplicationServices.cs (its header explains why).

**Failure scenario.** Not a runtime defect. Because everything is public, the compiler cannot flag unused or misused members (see the dead-code finding), and any view model can bypass IConversations and call GenerationCoordinator directly without an architecture test noticing unless one is written for it.

**Recommendation.** Make Desktop types internal by default. In Application, keep contracts and records public and make service implementations internal with InternalsVisibleTo for the composition and test assemblies. This also lets the compiler's unused-member analysis work.

