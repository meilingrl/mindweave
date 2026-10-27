using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Testing;

namespace Mindweave.Desktop.Tests;

/// <summary>A running answer shows a waiting indicator only until its first text; growing text is its own progress.</summary>
public class StreamingIndicatorTests
{
    [AvaloniaFact]
    public async Task The_waiting_indicator_shows_until_the_first_text_and_never_under_streaming_text()
    {
        await using var app = DesktopHarness.Create();
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold();
        var page = await AskAsync(app, held);

        await UiWait.UntilAsync(() => VisibleWaitingBars(window) == 1);
        Assert.True(page.Messages[1].IsWaiting);
        Assert.Equal(UiText.WaitingForAnswer, page.Messages[1].WaitingLabel);

        held.Release.TrySetResult();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Streaming_text_has_no_progress_bar_under_it()
    {
        await using var app = DesktopHarness.Create();
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold("第一句话。\n\n第二段，", "还在继续写。");
        var page = await AskAsync(app, held);

        await UiWait.UntilAsync(() => page.Messages[1].Body.Length > 0);
        window.UpdateLayout();
        Assert.False(page.Messages[1].IsWaiting);
        Assert.Equal(0, VisibleWaitingBars(window));
        Assert.True(page.Messages[1].IsInProgress);

        held.Release.TrySetResult();
        window.Close();
    }

    private static async Task<ConversationViewModel> AskAsync(DesktopHarness app, FakeChatClient.HeldReply held)
    {
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "问";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2);
        return page;
    }

    private static int VisibleWaitingBars(Window window)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<ConversationView>().Single().GetVisualDescendants().OfType<ProgressBar>()
            .Count(b => b.Name == "WaitingBar" && b.IsEffectivelyVisible);
    }
}
