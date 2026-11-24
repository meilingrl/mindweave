# Failure handling

**Status:** current rules, 2026-11-22. Implementation-level; product behaviour on failure stays in [requirements §9](../requirements.md) and the [architecture](../architecture.md). Checked by `tests/Mindweave.Architecture.Tests/CatchPolicyTests.cs`.

Most of the code sits on boundaries that fail in normal use: the network, AI services, files, other processes, SQLite. So catches are expected. What these rules prevent is a failure that leaves no trace, and an expected state that hides a programmer error.

## Rules

1. **Translate specific exceptions where they cross a boundary.** Catch the type you expect (`IOException`, `HttpRequestException`, `JsonException`, `StorageWriteException`, …) and turn it into a result, a state or a user message.
2. **A broad catch (`catch`, `catch (Exception)`) belongs at a boundary only:** a background task or run, a command or event handler, a third-party parser or native codec, or cleanup that rethrows (`catch { …; throw; }`). A `when (ex is A or B)` filter that names types counts as specific.
3. **A broad catch that does not rethrow leaves a trace.** In the desktop layer, call `DesktopLog.Swallowed(ex)`. Elsewhere, log through a `LoggerMessage` method, or hand the exception on (`TrySetException`, a failure path such as `FailUnexpectedlyAsync`, a `Failed` event, an argument or a field). Showing a message to the user is not a substitute for the log line.
4. **Quiet on purpose is listed.** The few catches that may stay silent (the logger itself, code that runs before the logger exists, a codec's "not an image") are listed with their reason in `CatchPolicyTests.QuietOnPurpose`. A new one needs a reason there, which is a reviewable decision.
5. **Cancellation is a cancellation only when the caller's token asked for it.** Use `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)`; any other cancellation (a timeout, a disposed source) is a failure.
6. **Never catch `NullReferenceException` or `IndexOutOfRangeException`, and do not use `InvalidOperationException` as a signal.** An expected state such as "shutting down" or "database closed" gets its own exception type, so a real defect of the same generic type is not reported as "unavailable". The existing uses are listed in `CatchPolicyTests.InvalidOperationAsSignal`, to be replaced; none may be added.
7. **`async void` only for event handlers in `Desktop/Views`.** Everything else returns a `Task` that its caller observes. A handler that touches the clipboard or the file system guards its own body; the dispatcher guard (`UiExceptionGuard`) is the last resort, not the plan.
8. **Every `CA1031` suppression states its reason** on the `#pragma` line, in a sentence that stands on its own (not "As above").

## Not covered by the test

- Where a broad catch may be (rule 2) and whether a cancellation is told apart by its token (rule 5): that is review.
- Whether a specific catch is narrow enough, or whether its message to the user is right: that is review.
- Fire-and-forget calls (`_ = SomethingAsync()`): their target must guard itself. Review checks this; the test does not.
- Events raised from worker threads (`GenerationCoordinator`): to be guarded when that code is next changed (maintainability review 2026-11-22, EXC-4).
