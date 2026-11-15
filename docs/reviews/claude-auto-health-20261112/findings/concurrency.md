# Concurrency, async and lifecycle — grade B

The concurrency design is deliberate and mostly correct: documented lock orders, no awaits under locks, a single SQLite writer thread, serialized refreshes, an explicit exit sequence and an epoch-based storage-recovery loop. I found no deadlock and no sync-over-async on the UI thread. What keeps it from an A is that the "nothing escapes" discipline is uneven. The answer runner has no catch-all, so one unexpected exception (reachable from a common error-body shape) leaves an answer permanently "streaming" and the conversation locked until restart. Enqueue-after-commit is unguarded against a concurrent exit. The startup and in-app-restore pipeline has no failure path once the database is open. Several safety properties rest on subscriber good behaviour rather than on structure (synchronous events between in-memory finish and terminal write, an async void whose comment misstates where exceptions go). Everything was judged by reading code; I ran nothing.

**Strengths**

- GenerationCoordinator keeps all scheduler state under one Lock and never awaits or raises events while holding it (GenerationCoordinator.cs:468-505); cancellation of yielded idle work is done outside the lock with the reason stated in a comment.
- The 'finished on screen' versus 'persisted' gap is handled explicitly: HasUnfinishedAnswer and UnfinishedUserWork read the live state, AnswerWritesSettledAsync lets a follow-up wait (bounded 5 s) for the previous terminal write (GenerationCoordinator.cs:142-170, ConversationService.cs:571-580), and unread marks are decided at AnswerEnded.
- StorageHealth recovery is linearized with an epoch plus an outstanding-obligation count, so a failure during a recovery pass cannot be cleared by that pass (StorageHealth.cs:226-248, 310-318). TerminalWrites serializes the recovery flush and the exit flush with a gate so the exit cannot see an empty queue while a dequeued write is still failing (TerminalWrites.cs:30-32, 85-103).
- The single-writer SQLite queue runs each item in its own transaction on a dedicated thread, honours cancellation only before a write starts, completes callers with RunContinuationsAsynchronously, and drains on dispose (SqliteWriteQueue.cs:49-106). Reads use one snapshot per call.
- The exit sequence is a real state machine: every step is wrapped so a failure ends in a Failed stage with retry/return rather than a hung overlay (ExitCoordinator.cs:211-285). AppHost closes the database only after the UI loop has returned, on a pool thread, with the single-instance lock released in a finally (AppHost.cs:36-73).
- SummaryDrafts uses per-node gates plus generation/revision checks so a stale page cannot overwrite, commit or discard newer input, with a documented lock order (SummaryDrafts.cs:25-29, 179-222).
- UI marshalling is consistent: worker-thread events reach view models only through UiThrottle or IUiDispatcher.Post, and SerialRefresh prevents an older load overwriting a newer one (IUiDispatcher.cs:20-83). I found no worker-thread handler touching observable state directly.
- ConversationTitler and WorkspaceNamePreparer handle the closed-coordinator race and track their fire-and-forget tasks with logging (ConversationTitler.cs:107-138, WorkspaceNamePreparation.cs:505-528).

**Not examined**

- Nothing was executed: no build, tests, stress or race runs. All findings come from reading code; the baseline output files were not consulted.
- Workbench.FlushAsync, NetworkSession/NetworkCanvas timers, LayoutPromptViewModel and the summary-export idle wait (ISummaryExport.WhenIdleAsync) were not read.
- SummaryService.SaveAsync, SummaryNaming beyond OnTaskFinished, WorkspaceNameSuggestions, and the conversation/workspace deletion services (cancel-background-then-delete ordering) were only skimmed or not read.
- View code-behind (AnswerView, ConversationView restore logic, ShellView, MainWindow.Follow) and their DispatcherTimer lifetimes were not audited beyond a grep.
- The SQLite stores' individual methods (whether every read failure is classified, RecoverUnfinishedAsync internals) and SqliteBackupFiles were not read.
- FileLoggerProvider thread safety, the credential stores' Task.Run wrappers (including a possibly blocking macOS keychain prompt), and SseReader were not read.
- Real Avalonia behaviour for Dispatcher.Post after shutdown and for unhandled exceptions in posted async lambdas is assumed from framework knowledge, not verified.
- The test suite was not reviewed for which of these races are already covered.

### concurrency-1. An unexpected exception in an answer run leaves the answer 'streaming' forever and locks the conversation until restart

- Final: **high** (confirmed); auditor said high
- Where: `src/Mindweave.Application/Conversations/AnswerRunner.cs:136`; effort S

**Evidence.** AnswerRunner.RunAsync catches only OperationCanceledException (when stop is cancelled) and ChatClientException around the stream (lines 136-146); the compression prelude catches three specific types (73-87). Anything else escapes to GenerationCoordinator.RunAsync, whose catch-all only logs "Generation task {TaskId} crashed" and frees the slot (GenerationCoordinator.cs:531-548). FinishAsync is never called, so the LiveAnswer stays Streaming, the stored message stays streaming and the task row stays running. The orphaned CheckpointLoopAsync never ends because its CancellationTokenSource is disposed by `using` without being cancelled (line 97).

A reachable trigger is in ChatCompletionsClient: `error["message"]` (line 245) and `JsonNode.Parse(text)?["error"]?["message"]` (line 285) throw InvalidOperationException when `error` is a JSON string rather than an object. Only JsonException is caught there; SendAsync's bare `catch { ...; throw; }` (204-208) rethrows it, and StreamAsync wraps only ChatClientException (53-56). `choices` not being an array (`AsArray()`, line 248) throws the same way.

**Failure scenario.** The user configures a relay or compatible service that answers an error as `{"error":"invalid api key"}` (string form, common on proxies) with HTTP 401, or sends that shape mid-stream. The run crashes. The answer row shows the waiting indicator with a ticking counter indefinitely. Stop does nothing, because StopAsync finds the answer neither queued nor running. Every new question or retry in that conversation is refused as AnswerInProgress by the store. The exit prompt does not count it. No request trace is recorded. Only restarting the app (startup marks it interrupted) frees the conversation. Title, summary and naming runners have the same gap, leaving task rows 'running' and, for titles, the 'requested' mark set for the session.

**Recommendation.** Give AnswerRunner.RunAsync a catch-all that cancels and awaits the checkpoint loop and calls FinishAsync(Failed, InvalidResponse or an internal-error kind, message), and records the trace. Do the same for the title, summary and naming runners (finish the task row as Failed; raise TitleAbandoned for titles). Separately, make ChatCompletionsClient's JSON access shape-tolerant (`as JsonObject` checks) and wrap non-ChatClientException failures at the client boundary. Add tests with a string-valued `error` body and with a fake client that throws InvalidOperationException.

**Skeptic: confirmed, high.** I could not refute it; every link in the chain I read holds, and no guard, test or design note covers it.

1. AnswerRunner.RunAsync has only two catches around the stream: OperationCanceledException (when stop is cancelled) and ChatClientException. The cancel-and-await of the checkpoint loop, FinishAsync and the trace record all sit after the try with no finally, so any other exception skips them. LiveAnswer was already MarkStreaming'd at line 51.

2. GenerationCoordinator.RunAsync's catch-all only logs "Generation task {TaskId} crashed"; its finally removes the run from _running, pumps and raises Changed. Nothing finishes the answer or the task row.

3. The trigger is real. System.Text.Json's JsonNode string indexer calls AsObject() and AsArray() throws likewise, both InvalidOperationException on a non-object/non-array node. So:
   - ThrowIfFailedAsync: `JsonNode.Parse(text)?["error"]?["message"]` throws on `{"error":"invalid api key"}`; only JsonException is caught.
   - It also throws when the whole error body is a bare JSON string, number or array (e.g. `"Unauthorized"`), which the finding did not mention and is at least as common on proxies.
   - Parse: `error["message"]` and `choices.AsArray()` throw the same way mid-stream, as does `node?["error"]` when the event is not an object.
   - SendAsync's bare `catch { dispose; throw; }` rethrows; the public StreamAsync wraps only ChatClientException.
   This was read from code and known API behaviour, not executed (no builds allowed in the worktree).

4. Consequences check out:
   - StopAsync looks only in the queues and _running, so it is a no-op once the run has been removed.
   - The stored message stays 'streaming', so ThrowIfUnfinished throws AnswerInProgressException for any new send/retry in that conversation.
   - Only startup recovery (UPDATE ... SET state='interrupted' WHERE state IN ('pending','streaming')) clears it.
   - The checkpoint loop's token source is disposed without cancel, so its Task.Delay loop runs until process exit (a minor leak).

5. Title, summary and naming runners have the same catch set (OCE, ChatClientException, StorageAdmissionRefused, plus ContextCompression for summaries) and no catch-all, so their task rows stay 'running'. RequestCompleteTextAsync also records no trace for other exception types. RunTransientAsync does have a catch-all and is not affected.

6. No coverage or accepted-risk note: client tests only use object-form `{"error":{"message":...}}` bodies, and a grep of TRACK.md and docs/design for this gap found nothing.

Severity stays high: a plain misconfiguration against a non-conforming relay locks the conversation with no in-app recovery and no visible error. It is not critical because nothing is lost and a restart recovers.

*Corrections to the finding.* Nothing in the finding is wrong. Three points on scope:

- The trigger is slightly broader than stated: a non-2xx body that is any valid non-object JSON (bare string, number, array) also throws at ChatCompletionsClient.cs:283, as does a stream event whose top level is not an object at :244.
- Line numbers are off by one or two (the finding's 245/285/248 are 246/283/249 in the file).
- Three sub-claims I did not verify: that the exit prompt does not count the stuck answer, that the UI shows a ticking counter, and that the title 'requested' mark stays set. The core lock-until-restart claim does not depend on them.

**Reproduction attempt: confirmed, high.** Reproduced with a focused xUnit test against track/claude-auto at 1ba95c9 (4 tests written to assert correct behaviour; all 4 fail).

Client boundary: ChatCompletionsClient throws a raw System.InvalidOperationException, not ChatClientException, in all three claimed shapes: an HTTP 401 with body {"error":"invalid api key"}, a mid-stream SSE event {"error":"upstream overloaded"}, and "choices" being an object rather than an array.

End to end (real SQLite stores, real GenerationCoordinator, real ChatCompletionsClient over a stub HTTP handler returning the 401 string-error body):
- After the run ends and its slot is freed, the LiveAnswer is still Streaming, the stored message is still streaming and the generation_task row is still running.
- StopAsync changes nothing.
- A second question in the same conversation is refused with AnswerInProgress.
- Retry of the stuck answer is refused with NotRetryable.
- ai_request has 0 rows, so no trace is recorded.
- UnfinishedCount is 0 and HasUnfinishedAnswer is false, so the exit prompt does not count it.

The run took about 0.3 s and each wait was on a state condition, not a fixed delay, so this is not a timing artefact. The mechanism matches the code: AnswerRunner.RunAsync catches only OperationCanceledException and ChatClientException around the stream, and GenerationCoordinator.RunAsync's catch-all only logs and frees the slot.

High severity stands. A plausible third-party relay response permanently wedges a conversation for the session with no in-app recovery (stop, ask and retry are all dead) and leaves no diagnostic trace. No data is lost, and a restart is claimed to recover it.

Not exercised by a test, code reading only:
- The orphaned checkpoint loop: `using var checkpointStop` is disposed without being cancelled on the exception path, so the Task.Delay loop never ends.
- The title, summary and naming runners: TaskRunners.cs has the same narrow catch lists at 60-70, 135-147 and 184-192.
- Startup marking the answer interrupted: I did not check this.

TRACK.md does not list this as a known finding.

*Corrections to the finding.* - Retry is refused as NotRetryable, not AnswerInProgress as the finding says ("Every new question or retry ... is refused as AnswerInProgress"). A new question is refused as AnswerInProgress. The user-visible effect is the same: both are blocked.
- The claims about the title, summary and naming runners, the never-ending checkpoint loop, and recovery on restart were not exercised by a test. The first two are consistent with the code I read; I did not check the restart path.
- The "ticking counter" UI symptom was not exercised in a Desktop test. It is inferred from the LiveAnswer staying Streaming.

<details><summary>Reproduction</summary>

Temporary file tests/Mindweave.Infrastructure.Tests/Chat/ZzReproCrashedRunTests.cs (deleted after the run). The tests assert the correct behaviour, so they fail if the finding is real.

```csharp
public class ZzReproCrashedRunTests(ITestOutputHelper output)
{
    private static readonly ModelSnapshot Model = new("cfg", "https://api.example.com/v1/", ChatProtocol.ChatCompletions, "m1", "ref", 32, ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Client_string_error_body_with_401_is_a_ChatClientException()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"invalid api key\"}") });
        var ex = await Record.ExceptionAsync(() => Drain(client));
        output.WriteLine($"401 string error -> {ex?.GetType().FullName}: {ex?.Message}");
        Assert.IsType<ChatClientException>(ex);
    }

    [Fact]
    public async Task Client_string_error_event_mid_stream_is_a_ChatClientException()
    {
        var client = Client(_ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\ndata: {\"error\":\"upstream overloaded\"}\n\n"));
        var ex = await Record.ExceptionAsync(() => Drain(client));
        output.WriteLine($"mid-stream string error -> {ex?.GetType().FullName}: {ex?.Message}");
        Assert.IsType<ChatClientException>(ex);
    }

    [Fact]
    public async Task Client_choices_not_an_array_is_a_ChatClientException()
    {
        var client = Client(_ => Sse("data: {\"choices\":{\"delta\":{\"content\":\"a\"}}}\n\n"));
        var ex = await Record.ExceptionAsync(() => Drain(client));
        output.WriteLine($"choices object -> {ex?.GetType().FullName}: {ex?.Message}");
        Assert.IsType<ChatClientException>(ex);
    }

    [Fact]
    public async Task Answer_whose_run_throws_unexpectedly_still_reaches_a_terminal_state()
    {
        var http = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"invalid api key\"}") }));
        await using var h = await ChatHarness.StartAsync(client: new ChatCompletionsClient(http, new ChatClientOptions()));

        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是谁发明的？", Ct);
        Assert.True(sent.Accepted);
        var answer = sent.Answer!.Value;

        await ChatHarness.UntilAsync(async () => await h.TaskStateAsync(answer) is not "queued", 20_000);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0, 20_000);

        var live = h.Generation.Observe(answer)?.Snapshot().State;
        var stored = (await h.GetMessageAsync(answer))!.State;
        var task = await h.TaskStateAsync(answer);
        output.WriteLine($"after run ended: live={live}, stored message={stored}, task row={task}");
        output.WriteLine($"HasUnfinishedAnswer={h.Generation.HasUnfinishedAnswer(sent.Conversation)}, UnfinishedCount={h.Generation.UnfinishedCount}");

        await h.Generation.StopAsync(answer);
        var afterStop = (await h.GetMessageAsync(answer))!.State;
        output.WriteLine($"after StopAsync: stored message={afterStop}, task row={await h.TaskStateAsync(answer)}");

        var again = await h.Conversations.AskAsync(sent.Conversation, "再问一次", Ct);
        output.WriteLine($"second question: accepted={again.Accepted}, rejection={again.Rejection}");
        var retry = await h.Conversations.RetryAsync(sent.Conversation, answer, Ct);
        output.WriteLine($"retry: accepted={retry.Accepted}, rejection={retry.Rejection}");
        output.WriteLine($"ai_request rows={await h.CountAsync("ai_request")}");

        Assert.True(afterStop is MessageState.Failed or MessageState.Stopped, $"stored answer is still {afterStop}; task row {task}; live {live}");
        Assert.True(again.Accepted, $"second question refused: {again.Rejection}");
    }

    private static ChatCompletionsClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new Stub(respond)), new ChatClientOptions());

    private static async Task Drain(ChatCompletionsClient client)
    {
        var request = new ChatRequest(Model, [new ChatTurn(ChatRole.User, "hi")]);
        await foreach (var _ in client.StreamAsync(request, "gw-key-0123456789", Ct)) { }
    }

    private static HttpResponseMessage Sse(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
```

Actual output of `dotnet test tests/Mindweave.Infrastructure.Tests --filter "FullyQualifiedName~ZzReproCrashedRunTests" --logger "console;verbosity=detailed"`:

```
Failed ...Client_string_error_event_mid_stream_is_a_ChatClientException [25 ms]
  Assert.IsType() Failure: Value is not the exact type
  Expected: typeof(Mindweave.Application.Ai.ChatClientException)
  Actual:   typeof(System.InvalidOperationException)
  Standard Output Messages:
  mid-stream string error -> System.InvalidOperationException: The node must be of type 'JsonObject'.

Failed ...Client_string_error_body_with_401_is_a_ChatClientException [< 1 ms]
  Expected: typeof(Mindweave.Application.Ai.ChatClientException)
  Actual:   typeof(System.InvalidOperationException)
  401 string error -> System.InvalidOperationException: The node must be of type 'JsonObject'.

Failed ...Answer_whose_run_throws_unexpectedly_still_reaches_a_terminal_state [276 ms]
  Error Message:
   stored answer is still Streaming; task row running; live Streaming
  Standard Output Messages:
  after run ended: live=Streaming, stored message=Streaming, task row=running
  HasUnfinishedAnswer=False, UnfinishedCount=0
  after StopAsync: stored message=Streaming, task row=running
  second question: accepted=False, rejection=AnswerInProgress
  retry: accepted=False, rejection=NotRetryable
  ai_request rows=0

Failed ...Client_choices_not_an_array_is_a_ChatClientException [< 1 ms]
  Expected: typeof(Mindweave.Application.Ai.ChatClientException)
  Actual:   typeof(System.InvalidOperationException)
  choices object -> System.InvalidOperationException: The node must be of type 'JsonArray'.

Test Run Failed.
Total tests: 4
     Failed: 4
 Total time: 1.2569 Seconds
```
```

</details>

### concurrency-2. Startup and in-app restore have no failure path once the database is open: an exception crashes at launch or leaves the restore on a permanent spinner

- Final: **medium** (confirmed); auditor said medium
- Where: `src/Mindweave.Desktop/Services/AppStartup.cs:62`; effort M

**Evidence.** App.axaml.cs:47 starts startup as `Dispatcher.UIThread.Post(async () => await ...RunAsync())`, an async-void lambda. AppStartup.RunAsync (62-71) and RestoreInPlaceAsync (90-106) have no try/catch. StartupCoordinator.ContinueAfterOpenAsync awaits `conversations.RecoverUnfinishedAsync` (a write) and `workspaces.ListAsync` unguarded (StartupCoordinator.cs:70-76); only the last-location read is guarded. `_marks.LoadAsync` and the sidebar read are internally guarded.

For in-app restore, AppSession.RunAsync swaps the window to the new session's StartupViewModel (AppHost.cs:129-132) and then awaits EndAsync(old) and RestoreInPlaceAsync (138-139) with no catch. The caller, ExitCoordinator.RestoreAsync (291-312), belongs to the old, already-disposed session: on an exception it logs, calls Reopen on the shut-down coordinator and returns to idle, none of which changes what the window shows.

**Failure scenario.** (a) Launch with a full or failing disk after a run that left unfinished rows: the database opens, RecoverUnfinishedAsync's update throws StorageWriteException, and the exception is unhandled on the UI thread. I expect the app to exit at every launch instead of showing the recovery page or the storage-paused state.

(b) During an in-app restore, the same throw (or one from disposing the old services) happens after the window has switched: the user sees the startup spinner indefinitely with no message and must quit and relaunch. Data is not lost, since the kept copy and the put-back logic exist, but nothing tells the user what happened.

**Recommendation.** Wrap the post-open steps so any exception becomes a StartupOutcome.NeedsRecovery (or a dedicated failure page) carrying the error detail. In AppSession.RunAsync, catch failures after the swap and route the new session to its recovery page. Add tests that make RecoverUnfinishedAsync throw at startup and during an in-app restore.

**Skeptic: confirmed, medium.** I could not refute this by reading the code; every cited location says what the finding claims, and I found no guard, test or documented decision covering it. Nothing was executed (read-only audit), so the crash and the stuck spinner are inferred from the code, not observed.

**Launch path (a)**
- `App.axaml.cs:47` posts an async lambda to the dispatcher, so an exception from `RunAsync` is rethrown on the UI thread with nothing to catch it.
- `AppStartup.RunAsync` (62-71) has no try/catch.
- `StartupCoordinator.ContinueAfterOpenAsync` awaits `RecoverUnfinishedAsync` (line 72) and `workspaces.ListAsync` (line 78) unguarded; only `locations.GetLastAsync` (84-95) is caught.
- `RecoverUnfinishedAsync` is a real write: `database.WriteAsync` with two UPDATEs (`SqliteConversationStore.cs:235-247`). It runs on every launch.
- The only global handlers (`Program.cs:36-37`) log via `LogCrash` and do not prevent termination. `AppHost.Run` (37-49) has a `finally` that disposes cleanly but no catch.
- So a post-open storage failure ends the process instead of reaching the recovery page or the storage-paused state. It repeats on each launch while the fault persists.

**In-app restore path (b)**
- `AppSession.RunAsync` (`AppHost.cs:126-140`) shows `StartupViewModel` on the new session and fires `Replaced` (130-132; `MainWindow.axaml.cs:43` rebinds the window) before awaiting `EndAsync(old)` and `RestoreInPlaceAsync` with no catch.
- `EndAsync` (148-175) is try/finally only, so a failure in flush, close or dispose propagates.
- The exception travels back through `BackupDialogs.HandOverAsync` (line 219) into the old session's `ExitCoordinator.RestoreAsync` catch (300-311), which logs, calls `generation.Reopen()` and `BackToIdle()`. None of that touches the new root the window now shows, so the spinner stays.

**Looked for mitigations, found none**
- No test makes `RecoverUnfinishedAsync` throw at startup: the only test hits are direct store calls and a `FlakyStore` pass-through. Restore tests (`BackupRestoreUiTests`, `InPlaceRestoreTests`, `InPlaceRestoreFailureTests`) cover failures inside `database.RestoreInPlaceAsync`, which returns a result, not post-open throws.
- `TRACK.md` and `docs/design` do not record this as known or accepted. `s7-ui-polish.md:672` lists other unverified restore edge cases, not this one.

**Severity**
Medium holds. No data is lost and the kept copy and put-back logic exist, but the trigger (disk full, I/O error or read-only volume at the moment of the startup write) is exactly what the app's storage-paused design is meant to absorb everywhere else. The result is a crash on every launch with no user-facing message, which is below the stated production bar. It is not high because it needs a storage fault in a narrow window and resolves once the fault clears.

*Corrections to the finding.* - The stuck-spinner outcome in (b) applies when `EndAsync(old)` throws, or when `RestoreInPlaceAsync` throws before `Apply`. A throw from the new session's `RecoverUnfinishedAsync` or `ListAsync` fits this. Not every late failure leaves a spinner.
- The same gap exists on the recovery page's own buttons: `RecoveryViewModel.RetryAsync` and `ConfirmRestoreAsync` (63-106) are try/finally only and call the same uncaught `AppStartup.RunAsync` / `RestoreAsync`. A post-open throw there goes to the relay command's default handling; I did not verify the resulting UI behaviour.
- "Exits at every launch" is accurate only while the storage fault persists and unfinished rows or the write itself keeps failing. I did not check whether `OpenCore` performs a write that would turn disk-full into `NeedsRecovery` first. Even if it does, any other post-open throw (for example from `ListAsync`) takes the same uncaught path.
- The file citations are otherwise accurate. `AppSession` lives in `src/Mindweave.Desktop/AppHost.cs`, not under `Services/`.

### concurrency-3. Enqueue after the durable commit is unguarded against a concurrent exit: uncaught exception on the UI thread and an orphaned pending answer

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Application/Conversations/ConversationService.cs:290`; effort S

**Evidence.** StartAsync, AskAsync and RetryAsync commit the question, pending answer and task row, then call `Enqueue(...)` (lines 237, 290, 345) with no try/catch. SummaryService.SummarizeAsync does the same (SummaryService.cs:209). GenerationCoordinator.Enqueue throws InvalidOperationException("Generation is shutting down.") once `_closed` is set (GenerationCoordinator.cs:190-193), which StopForExitAsync does as the exit's first step (line 338). Only ConversationTitler handles this race (ConversationTitler.cs:107-138). Composer.SendAsync awaits the call with no catch (Composer.cs:56-67). CheckReadyAsync can hold a send for up to 5 s waiting for the previous terminal write (ConversationService.cs:571-580), which widens the window.

**Failure scenario.** The user presses send and then closes the window (or starts an in-app restore) before the send's store write returns. The coordinator has no queued or running work yet, so no prompt appears and CommitAsync closes the entry. The write then commits and Enqueue throws. The exception surfaces from an async command on the UI thread; I found no handler for it, so the expected result is a crash in the middle of the exit sequence. If instead the exit's save fails and the user picks 'return' (Reopen), the conversation holds a Pending answer that nothing will ever run, and every ask is refused as AnswerInProgress until restart.

**Recommendation.** Make acceptance atomic with the closed check: either have Enqueue return a refusal, or catch InvalidOperationException in the three ConversationService call sites and in SummaryService, finish the just-stored answer/task as Cancelled (as the titler does) and return a rejection the page can show. Add a test that closes the coordinator between the store write and Enqueue.

**Skeptic: partially confirmed, low.** The code defect is real: I could not find any guard, test or documented decision that closes the race. The severity is overstated, and one of the two claimed consequences does not follow from the other.

What holds up (read, not executed — builds and tests were off-limits):
- StartAsync, AskAsync and RetryAsync each commit the question, pending answer and task row, then call Enqueue outside any try/catch. SummaryService.SummarizeAsync does the same.
- GenerationCoordinator.Enqueue throws InvalidOperationException once `_closed` is set, and StopForExitAsync sets it as its first statement.
- The close hook reaches that statement synchronously on the UI thread when nothing is queued or running (InterceptClose -> Begin -> AskAboutTasks -> CommitAsync -> StopAndReleaseAsync). So there is no prompt and no delay.
- A send that is still in its awaits before the store write is not visible in UnfinishedUserWork, so the exit proceeds without asking.
- The exit's save step ends with `database.DrainAsync()`, so the in-flight write commits rather than being abandoned, and Enqueue then throws.
- Nothing above catches it: Composer.SendAsync, the two page delegates, ConversationViewModel.RetryAsync and both SummarizeAsync callers have no try/catch.
- No dispatcher-level handler exists. Program.cs only logs AppDomain.UnhandledException and UnobservedTaskException; a grep for Dispatcher.UIThread.UnhandledException and FlowExceptionsToTaskScheduler in src found nothing.
- ConversationTitler is the only caller that handles the race (IsClosed check plus catch that finishes the task as Cancelled), which shows the authors knew of it but did not apply it to user sends.
- No test closes the coordinator between a send's store write and Enqueue; the tests that touch IsClosed/StopForExitAsync are exit-stop, exit-saving, restore and workspace-name tests.

Why it is low, not medium:
- The window is the send's own pipeline (a few local SQLite reads plus one write), normally milliseconds. The user must press send and close within that. The 5-second widening only applies when the previous answer's terminal write is still landing, which is itself a rare slow-disk condition.
- The process is exiting anyway. AppHost.Run disposes in a `finally` even when the UI loop throws, so the database is closed and the lock released.
- No user data is lost. The question is stored, and the pending answer is marked interrupted on the next start (SqliteConversationStore.cs:240), where it can be retried.
- If the dispatcher has already stopped when the continuation is posted, the exception is never observed at all.
- Reaching it through in-app restore needs the send to be in flight while the user navigates to settings and confirms a restore, which is only possible inside the rare 5-second wait.

The crash itself is inferred from CommunityToolkit.Mvvm 8.4.2's known behaviour (AsyncRelayCommand rethrows on the synchronization context via an async void helper unless FlowExceptionsToTaskScheduler is set); I did not reproduce it.

The recommended fix (a refusal from Enqueue, or the titler's catch-and-cancel pattern at the four call sites, plus a test) is sound and cheap.

*Corrections to the finding.* 1. The "orphaned pending answer, every ask refused until restart" consequence is not an independent outcome. The exception always travels back to the awaiting command on the UI thread. In the "save failed, user picks return" and "restore did not go ahead" cases the window and dispatcher are still alive, so the rethrow crashes the app before any stuck state can be experienced. After the restart the pending answer is recovered as interrupted. The stuck conversation would only appear if something swallowed the exception, and nothing does.
2. In the save-failed case the send's own write is likely to fail too and return StorageRefused, so Enqueue is often never reached there.
3. "Crash in the middle of the exit sequence" is conditional. By the time the write commits (during DrainAsync) the exit is at or past its last step; depending on whether the dispatcher is still pumping, the result is either an unhandled-exception exit with a critical log entry or nothing visible.
4. Line references are accurate: ConversationService.cs 237, 290, 345; SummaryService.cs 209; GenerationCoordinator.cs 190-193 and 338; ConversationTitler.cs 107-138; Composer.cs 56-67. The 5-second wait is at ConversationService.cs 571-584 and applies to all three send paths.

### concurrency-4. The terminal write depends on every synchronous event subscriber not throwing: events are raised on the generation worker between in-memory finish and the store write

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Application/Conversations/AnswerRunner.cs:159`; effort S

**Evidence.** FinishAsync calls `live.Finish(...)`, which raises LiveAnswer.Changed synchronously (LiveAnswer.cs:112-120), then `_events.AnswerEnded(...)` (line 159), and only then `_writes.SaveAsync(FinishAnswerAsync...)` (line 160). AnswerEnded runs ConversationMarksService.OnAnswerEnded, which synchronously raises Marked, Answered and Changed to UI-layer subscribers (ConversationMarksService.cs:290-320). `live.Append` inside the stream loop raises Changed the same way (AnswerRunner.cs:113). None of these invocations is guarded. Today's subscribers only post to the dispatcher or a throttle, so this is latent.

**Failure scenario.** A subscriber added later, or an existing one that starts throwing (for example a handler touching a disposed object after an in-app restore), throws from AnswerEnded. FinishAsync aborts before the terminal write. The answer is terminal on screen but stays 'streaming' in the database, is not kept for retry, and after the next start shows as interrupted with only the text up to the last 1-second checkpoint. Combined with the missing catch-all in the first finding, the run is merely logged as crashed.

**Recommendation.** Do the terminal write (or at least register it in TerminalWrites) before raising anything that leaves the coordinator, or raise AnswerEnded/Changed through a guarded invoker that logs and swallows per-subscriber exceptions. LiveAnswer.Changed should be guarded the same way, since it runs inside the stream loop.

**Skeptic: partially confirmed, low.** The code is exactly as described and I found no guard, but there is no present-day trigger, so this is a robustness gap rather than a medium defect.

**What holds up**
- `FinishAsync` runs `live.Finish` (which raises `LiveAnswer.Changed` synchronously), then `_events.AnswerEnded`, and only then the `_writes.SaveAsync(FinishAnswerAsync…, retryOnFailure: true)` call.
- The coordinator forwards `AnswerEnded` with a bare `AnswerEnded?.Invoke`. `ConversationMarksService.OnAnswerEnded` then raises `Marked`, `Answered` and `Changed` with bare invokes. No try/catch anywhere on this path.
- A throw before line 160 means the terminal write is never handed to `TerminalWrites`, so the retry mechanism never sees it. The only catch is the coordinator's catch-all, which logs "run crashed" and frees the slot.
- The row stays `streaming` and the task `running` until the next start, when startup recovery rewrites them to `interrupted`. Text after the last checkpoint is lost.
- The ordering is deliberate: the S7 design moved `AnswerEnded` ahead of the write so the unread dot is judged at in-memory finish. That makes the gap a known trade-off in ordering, not an accident, but nothing protects the write.

**Why medium is overstated**
- Every current synchronous subscriber is effectively non-throwing:
  - `WorkspaceNewsViewModel.OnAnswered` does a field read, an `Interlocked.Increment` and a dispatcher `Post`.
  - The `marks.Changed` handlers call `UiThrottle.Request`, which is an `Interlocked.Exchange` plus `Task.Delay().ContinueWith`.
  - `MessageItemViewModel.OnLiveChanged` calls the same throttle.
  - `Announce` fires `_ = FlushAsync()`, an async method whose exceptions are captured in its task.
- The "disposed object after in-app restore" example is speculative; none of these handlers touch disposable state.
- The team already treats this class as a defect: the 2026-11-10 refactor review fixed subscriber exceptions landing in the generation thread for `AnswerFinished`. That fix does not cover `AnswerEnded` or `LiveAnswer.Changed`.

Impact if it ever fires is real (wrong persisted state plus up to one checkpoint interval of lost text for one answer), so the recommendation to guard the invokes or register the write first is sound. With no reachable trigger today, low is the right rating.

*Corrections to the finding.* - Severity should be low, not medium: the finding itself calls the issue latent, and every current synchronous subscriber only does Interlocked operations, a dispatcher post or a throttle request.
- The "handler touching a disposed object after an in-app restore" example is not supported by any existing handler.
- "Missing catch-all" needs qualifying: `GenerationCoordinator.RunAsync` does have a catch-all (line 531) that logs and frees the slot. What is missing is a catch inside `AnswerRunner` that would still perform the terminal write.
- One consequence the finding understates: if `LiveAnswer.Changed` throws from `live.Append` inside the stream loop, the exception also skips lines 148-150. The checkpoint loop is then never cancelled (the `using` only disposes the token source), so it polls for the life of the process, and `FinishAsync` is never called, leaving the in-memory answer in Streaming as well.
- The generation_task row also stays `running` until restart, not only the message row.

### concurrency-5. Page reloads are fire-and-forget with no error path: a failed read is silent, drops the queued refresh, and surfaces only as a GC-time 'Unhandled exception' log

- Final: **low** (confirmed in part); auditor said medium
- Where: `src/Mindweave.Desktop/ViewModels/Conversations/ConversationViewModel.cs:71`; effort M

**Evidence.** `_reload = new UiThrottle(..., () => _ = _load.Request())` discards the task. LoadAsync (368-415) awaits six store reads with no try/catch. The same shape exists as `Loaded = _load.Request()` in ConversationListViewModel.cs:73 and WorkspaceNewsViewModel.cs:82. SerialRefresh.RunAsync (IUiDispatcher.cs:66-82) exits its loop on the first exception, so a request recorded in `_again` during the failed run is lost. The only global hooks are AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException, both logging Critical "Unhandled exception" (Program.cs:36-37); there is no Dispatcher.UIThread.UnhandledException handler. View-model catches target StorageWriteException almost exclusively.

**Failure scenario.** A read fails during a reload triggered by an answer finishing or a title being saved (an I/O error, or the database being closed under an old page). The page silently keeps stale content (old title, missing branch entry, an answer row not replaced by its stored form, so branching from it stays disabled) until some later event happens to trigger another reload. The log shows a misleading Critical 'Unhandled exception' minutes later, when the finalizer runs. The same exception from an awaited command path would instead be unhandled on the UI thread.

**Recommendation.** Decide one policy and put it in SerialRefresh: catch inside the loop, log at the point of failure, keep honouring `_again`, and expose a 'last load failed' state the pages can show with a retry. Add a Dispatcher.UIThread.UnhandledException handler that logs and routes into the exit/flush path deliberately rather than by accident.

**Skeptic: partially confirmed, low.** Every mechanical claim checks out against the code; I could not find a guard, test or recorded decision that refutes it. The severity is overstated because the trigger is rare and the effect is transient and loses no data.

What holds:
- The reload discards its task: `() => _ = _load.Request()` in ConversationViewModel (also NetworkHomeViewModel and SummaryViewModel). ConversationListViewModel and WorkspaceNewsViewModel assign it to `Loaded`, which the next request overwrites, so a faulted task is equally unobserved in production.
- LoadAsync awaits five service reads (the finding says six) with no try/catch. ConversationService.LoadAsync does not translate read errors, so a SqliteException or the `InvalidOperationException("The database is not open.")` propagates raw.
- SerialRefresh.RunAsync has try/finally only. An exception leaves the do/while, `_busy` is reset, and `_again` is cleared at the start of the next run, so a request queued during the failed run is dropped until another event arrives.
- The only global hooks are AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException, both logging Critical "Unhandled exception". There is no Dispatcher.UIThread.UnhandledException handler anywhere in src/Mindweave.Desktop.
- The only SerialRefresh test covers ordering and non-overlap, not a throwing refresh. TRACK.md and docs/design record no read-failure policy; the TRACK 2026-10-27 entry is about out-of-order completion only.

Why I lowered the severity:
- It needs a SQLite read to fail on an already-open database (I/O error or corruption). Reads run in a snapshot read transaction, so this is rare.
- The "database closed under an old page" trigger was already narrowed: the 2026-11-05 fix in s7-ui-polish.md closes the database only after the UI loop ends. I did not check whether a backup-restore session swap can still reach it.
- The consequence is stale page content until the next change event or reopening the page. Nothing is lost or corrupted, and the process does not crash, since UnobservedTaskException does not terminate on modern .NET.
- A disk failure bad enough to break reads will usually break writes too, and those already surface through the StorageWriteException / storage-pause path.

It remains a real gap at the stated quality bar: no read-error policy, a misleading and delayed Critical log line, and no deliberate UI-thread exception handler.

*Corrections to the finding.* - The discard is at ConversationViewModel.cs:72, not 71. LoadAsync is at roughly 376-420, not 368-415, and awaits five service reads, not six.
- SerialRefresh lives in src/Mindweave.Desktop/Services/IUiDispatcher.cs; the finding gave only the file name.
- "View-model catches target StorageWriteException almost exclusively" is overstated: there are 11 StorageWriteException catches against 30 general Exception catches in the view models. None of them wraps a page reload, so the point about reloads still stands.
- The "database closed under an old page" trigger was largely removed for the exit path by the 2026-11-05 AppHost.Run change. It should be limited to genuine read I/O errors, or to a backup-restore session swap if that is shown to be reachable; I did not verify that.
- "Unhandled on the UI thread" for awaited command paths depends on the command library's behaviour. I did not verify it, and the project's own s6-wrapup.md:173 marks the same claim as inferred.
- Severity should be low: rare trigger, transient stale UI, no data loss, no crash.

### concurrency-6. `async void OnAnswerFinished` rests on a wrong assumption: on a worker thread a subscriber exception terminates the process

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Conversations/ConversationService.cs:618`; effort S

**Evidence.** The comment at 616-617 says an exception from a ConversationChanged subscriber "goes to the synchronization context, never back into the generation worker". AnswerFinished is raised on a thread-pool thread (AnswerRunner.cs:163 under Task.Run, GenerationCoordinator.cs:487), where SynchronizationContext.Current is null. An exception escaping an async void method there is rethrown on the thread pool and is fatal to the process (AppDomain.UnhandledException only logs it). `ConversationChanged?.Invoke` at line 620 is the unguarded synchronous part. The titler call after it never throws (ConversationTitler.cs:72-84).

**Failure scenario.** Any ConversationChanged subscriber throwing on the worker thread (all current ones only post to a throttle or dispatcher, so this is latent) kills the application immediately, bypassing the exit sequence. Unflushed drafts inside their debounce window and text since the last checkpoint of other running answers are lost.

**Recommendation.** Make the handler a normal method that invokes ConversationChanged inside try/catch with logging, then starts the titler task explicitly (its method already never throws). Correct the comment.

### concurrency-7. StorageHealth.Changed is raised synchronously inside callers' locks, contradicting its documented 'leaf lock, nothing called back' contract

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Storage/StorageHealth.cs:246`; effort S

**Evidence.** The class remark (56-57) says nothing is called back while its lock is held "so owners may report from inside their own locks". Block() releases the health lock and then invokes `Changed` on the reporting thread (244-247). Owners do call Retain while holding their own locks: SummaryDrafts.Failed (SummaryDrafts.cs:458-470) and ReadingStateService.SaveDraftAsync (ReadingStateService.cs:221-226). So the Changed handlers run under those locks: GenerationCoordinator's handler takes the coordinator gate in Pump and then raises its own Changed event to further subscribers (GenerationCoordinator.cs:54-59), and ConversationMarksService's handler runs too. I traced the current handlers and found no cycle (the coordinator never takes a drafts or reading-state lock under its gate), so this is a hazard, not a present deadlock.

**Failure scenario.** A future Changed subscriber that takes a lock also held around a call into SummaryDrafts or ReadingStateService (for example a service that reads a draft under its own lock) creates a lock inversion that deadlocks only when a storage failure coincides with that call: rare and very hard to reproduce. The documented lock order in SummaryDrafts (node gate, record lock, storage-health lock) does not mention that arbitrary event handlers run at the innermost level.

**Recommendation.** Have the owners call Retain/Report after leaving their own lock (SummaryDrafts.Failed already does this for Report), or raise Changed asynchronously from StorageHealth (queue to the pool). Update the remark to state where handlers run.

### concurrency-8. Debounce timers are not cancelled on dispose; after an in-app restore the old session's delayed writes hit disposed semaphores

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Summaries/SummaryDrafts.cs:144`; effort S

**Evidence.** SummaryDrafts.Set schedules `_ = Task.Delay(SaveDelay).ContinueWith(_ => WriteAsync(node), ...)` (line 144) with no cancellation, and Dispose disposes every node gate (293-302). ReadingStateService.ScheduleSave does the same (ReadingStateService.cs:192) and its Dispose disposes `_flushing` (line 178). An exit flush writes the content but does not cancel a timer already running. AppSession.EndAsync disposes the old provider during an in-app restore while the process keeps running (AppHost.cs:148-174).

**Failure scenario.** A reading position or draft changes within the 0.5-1 s debounce window before an in-app restore completes its hand-over. The exit flush saves the value, the old services are disposed, then the timer fires: SemaphoreSlim.WaitAsync throws ObjectDisposedException in an unobserved task, later logged as Critical 'Unhandled exception' in the new session's log. No data is lost; the cost is a misleading critical log entry and a write attempt against a closed session.

**Recommendation.** Give both services a stop token that Dispose cancels and that the delayed continuation checks before touching the gate, or make the scheduled write a no-op once disposed.

### concurrency-9. Per-answer state is retained for the process lifetime: `_live` is never pruned and per-run CancellationTokenSources are never disposed

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Application/Conversations/GenerationCoordinator.cs:197`; effort S

**Evidence.** `_live[answer.Live.MessageId] = answer.Live` (line 197) is the only write to the dictionary and there is no removal anywhere in the Conversations folder (grep for `_live`). Each LiveAnswer holds the full answer text in a StringBuilder (LiveAnswer.cs:27). Line 545 states the run's CancellationTokenSource is deliberately not disposed. MessageItemViewModel only binds to a live answer while the stored row is unfinished (MessageItemViewModel.cs:44), so terminal entries whose write has landed are never read again.

**Failure scenario.** In a long-lived desktop session (the app is designed to stay open, with in-app restore instead of restart) every answer ever generated stays in memory twice (UTF-16 text plus entry), along with its CTS and any subscriber not yet disposed. Growth is linear in answers produced; this is slow memory creep, not a correctness fault.

**Recommendation.** Remove the entry once the terminal write is applied (keep it only while the write is kept in TerminalWrites), and dispose the CTS in the run's finally after swapping in a completed marker so a late StopAsync becomes a no-op.

### concurrency-10. Single-instance listener can be stalled or killed silently: no read timeout and only IOException is tolerated

- Final: **low** (not verified); auditor said low
- Where: `src/Mindweave.Infrastructure/Hosting/SingleInstance.cs:151`; effort S

**Evidence.** ListenAsync serves one connection at a time (maxNumberOfServerInstances 1, line 146) and awaits `server.ReadAsync(request, cancellationToken)` with only the stop token (151). The loop catches OperationCanceledException and IOException (160-167); any other exception (for example UnauthorizedAccessException from creating the pipe, or one thrown by `onActivate`) ends the listener task, which is observed only at DisposeAsync. A persistent IOException from the constructor would spin the loop with no delay.

**Failure scenario.** A second launch is killed, or hangs, after connecting but before sending its one byte. The primary's listener waits on that read indefinitely. Later launches cannot connect, time out after 5 s, and print 'already running but did not respond' (exit code 2, Program.cs:29-30) instead of bringing the window to the front, until the primary is restarted.

**Recommendation.** Bound the per-connection read with a short linked timeout, catch and log all non-cancellation exceptions inside the loop with a small back-off, and wrap `onActivate` separately.

