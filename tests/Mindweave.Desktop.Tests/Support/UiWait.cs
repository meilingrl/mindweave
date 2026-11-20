using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
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
    /// pointer and the click is lost.
    /// </summary>
    public static Task AnswersRenderedAsync(
        Window window,
        int timeoutMs = 5000,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0) =>
        UntilAsync(
            () => window.GetVisualDescendants().OfType<AnswerView>().All(a => a.IsLayoutCurrent),
            timeoutMs,
            "every answer rendered",
            file,
            line);
}
