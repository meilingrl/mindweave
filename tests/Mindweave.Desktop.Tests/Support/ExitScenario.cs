using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;

namespace Mindweave.Desktop.Tests;

/// <summary>How the exit is asked for: closing the window, or the application lifetime (⌘Q, the dock's "quit").</summary>
public enum ExitEntry
{
    Window,
    Lifetime,
}

/// <summary>Quitting, and the unsaved work that makes the exit ask first, shared by the exit tests.</summary>
internal static class ExitScenario
{
    /// <summary>Asks to quit through <paramref name="entry"/>; true when the request was held (cancelled) for the exit sequence.</summary>
    public static bool Quit(DesktopHarness app, MainWindow window, ExitEntry entry)
    {
        if (entry == ExitEntry.Window)
        {
            var closed = false;
            void OnClosed(object? sender, EventArgs e) => closed = true;
            window.Closed += OnClosed;
            window.Close();
            window.Closed -= OnClosed;
            return !closed;
        }

        var request = new ShutdownRequestedEventArgs();
        App.OnShutdownRequested(app.Root, request);
        return request.Cancel;
    }

    /// <summary>Starts an answer in a new conversation that streams "进行中" and then waits.</summary>
    public static async Task<MessageId> RunningAnswerAsync(DesktopHarness app)
    {
        var held = app.Client.Hold("进行中");
        var sent = await app.Services.GetRequiredService<ConversationService>().StartAsync(app.Workbench.CurrentWorkspace!.Id, "问", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return sent.Answer!.Value;
    }

    public static async Task<Message> StoredAsync(DesktopHarness app, MessageId answer)
    {
        var store = app.Services.GetRequiredService<IConversationStore>();
        var conversations = await store.ListAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None);
        foreach (var conversation in conversations)
        {
            if ((await store.GetMessagesAsync(conversation.Conversation.Id, CancellationToken.None)).FirstOrDefault(m => m.Id == answer) is { } found)
            {
                return found;
            }
        }

        throw new InvalidOperationException($"Answer {answer} not found.");
    }
}
