using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Testing;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views.Conversations;

namespace Mindweave.Desktop.Tests;

internal static class UiWait
{
    /// <summary>Pumps the UI dispatcher until <paramref name="condition"/> holds; fails after a timeout instead of racing.</summary>
    public static async Task UntilAsync(
        Func<bool> condition,
        int timeoutMs = 5000,
        [CallerArgumentExpression(nameof(condition))] string? expression = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0)
    {
        timeoutMs = TestWaits.Ms(timeoutMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                // Which wait it was: a full Windows run reports only the test name and this message.
                throw new TimeoutException($"Condition not reached on the UI thread within {timeoutMs} ms: {expression} ({Path.GetFileName(file)}:{line}).");
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Pumps the UI dispatcher until every assertion in <paramref name="assertions"/> passes in the same round; on timeout
    /// the last failure is reported. Use it where what is checked lands in more than one step: waiting for one part
    /// (a count) and then asserting another (the notice a later recount sets) passes alone and fails under load
    /// (docs/design/testing.md, "Intermittent failures"). Only assertion failures are retried; any other exception ends
    /// the wait at once.
    /// </summary>
    public static async Task SettledAsync(
        Action assertions,
        int timeoutMs = 5000,
        [CallerArgumentExpression(nameof(assertions))] string? expression = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0)
    {
        ArgumentNullException.ThrowIfNull(assertions);
        timeoutMs = TestWaits.Ms(timeoutMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            try
            {
                assertions();
                return;
            }
            catch (Exception failure) when (failure is Xunit.Sdk.IAssertionException)
            {
                if (DateTime.UtcNow > deadline)
                {
                    // Which wait it was and what still failed: a full Windows run reports only the test name and this message.
                    throw new TimeoutException(
                        $"Not settled on the UI thread within {timeoutMs} ms ({Path.GetFileName(file)}:{line}): {failure.Message}{Environment.NewLine}{expression}",
                        failure);
                }
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Waits until every answer of <paramref name="conversation"/> is finished as stored. An answer ends in three steps: its
    /// live state turns terminal (the page row and <see cref="IConversations.HasUnfinishedAnswer"/> change here), then
    /// it is written, then its slot is freed (AnswerRunner.FinishAsync). Archiving, deleting, branching and summarizing
    /// check what is stored and refuse an answer still running there, so wait for this before any of them, not for the
    /// page or the running flag.
    /// </summary>
    public static Task AnswersStoredAsync(
        IConversations conversations,
        ConversationId conversation,
        int timeoutMs = 5000,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0) =>
        UntilAsync(
            () => conversations.LoadAsync(conversation, CancellationToken.None).Result!.Messages.All(m => m.IsFinished),
            timeoutMs,
            "every answer finished as stored",
            file,
            line);

    /// <summary>
    /// Waits until the answer at <paramref name="index"/> is shown from storage in <paramref name="state"/>. An answer
    /// that ends shows its end state in the live row first; the row is replaced by the stored one only after the
    /// terminal write and a page reload. A control taken from the live row and clicked while that reload lands is gone
    /// before the click arrives (headless input runs pending dispatcher jobs first), so act on a finished answer's row
    /// only after this.
    /// </summary>
    public static Task StoredAsync(
        ConversationViewModel page,
        int index,
        MessageState state,
        int timeoutMs = 5000,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0) =>
        UntilAsync(
            () => page.Messages.Count > index && page.Messages[index] is { IsLive: false } item && item.State == state,
            timeoutMs,
            $"answer {index} stored as {state}",
            file,
            line);

    /// <summary>
    /// Waits until every answer shown in <paramref name="window"/> is rendered and laid out. A finished answer on a page
    /// that is still placing itself first shows a placeholder of about its height and is rendered a moment later
    /// (health check 2026-11-12, performance-2); the controls under it move when it is. A button under an answer is
    /// clicked only after this: rendered between the press and the release, the answer moves the button away from the
    /// pointer and the click is lost. The same holds for a streaming answer: its text is parsed in the background and
    /// lands a moment after the message has it.
    /// </summary>
    /// <remarks>
    /// Each round lays the window out first: until then a new answer's row, or the text it was just given, is not in the
    /// visual tree, and "every answer laid out" would hold for answers not there yet. An answer counts only once it shows
    /// its message's current text.
    /// </remarks>
    public static Task AnswersRenderedAsync(
        Window window,
        int timeoutMs = 5000,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0) =>
        UntilAsync(
            () =>
            {
                window.UpdateLayout();
                return window.GetVisualDescendants().OfType<AnswerView>().All(a =>
                    a.DataContext is not MessageItemViewModel { IsAnswer: true } item || (a.IsLayoutCurrent && a.Markdown == item.Body));
            },
            timeoutMs,
            "every answer rendered",
            file,
            line);
}
