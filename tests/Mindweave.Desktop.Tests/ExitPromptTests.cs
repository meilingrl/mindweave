using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.ViewModels;
using static Mindweave.Desktop.Tests.ExitScenario;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The exit prompts (architecture §12.3, requirement §9.2–9.3) through both entry points: every cancelable prompt before
/// anything irreversible, the layout first, and cancelling keeps everything as it was.
/// </summary>
public class ExitPromptTests
{
    [AvaloniaTheory]
    [InlineData(ExitEntry.Window)]
    [InlineData(ExitEntry.Lifetime)]
    public async Task An_unsaved_layout_alone_is_asked_about_and_dont_save_discards_only_once_the_exit_commits(ExitEntry entry)
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var network = await DirtyLayoutAsync(app, window);

        Assert.True(Quit(app, window, entry));
        Assert.True(app.Root.LayoutPrompt.IsOpen);
        Assert.Equal(ExitStage.LayoutPrompt, app.Root.Exit.Stage);
        Assert.False(app.Root.Exit.IsTaskPromptOpen);

        app.Root.LayoutPrompt.DontSaveCommand.Execute(null);
        await app.Root.Exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        Assert.False(network.IsDirty);

        // The coordinator's own shutdown passes through either hook.
        Assert.False(Quit(app, window, entry));
    }

    [AvaloniaTheory]
    [InlineData(ExitEntry.Window)]
    [InlineData(ExitEntry.Lifetime)]
    public async Task A_running_answer_alone_is_asked_about_and_stopping_keeps_what_arrived(ExitEntry entry)
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var answer = await RunningAnswerAsync(app);

        Assert.True(Quit(app, window, entry));
        Assert.False(app.Root.LayoutPrompt.IsOpen);
        Assert.True(app.Root.Exit.IsTaskPromptOpen);
        Assert.Equal(UiText.ExitWithUnfinished(1, 0), app.Root.Exit.TaskMessage);

        app.Root.Exit.StopAndExitCommand.Execute(null);
        await app.Root.Exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        var stored = await StoredAsync(app, answer);
        Assert.Equal(MessageState.Stopped, stored.State);
        Assert.Equal("进行中", stored.Text);
        Assert.False(Quit(app, window, entry));
    }

    [AvaloniaTheory]
    [InlineData(ExitEntry.Window)]
    [InlineData(ExitEntry.Lifetime)]
    public async Task With_both_the_layout_is_asked_first_and_cancelling_the_task_prompt_keeps_it_unsaved(ExitEntry entry)
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var network = await DirtyLayoutAsync(app, window);
        var answer = await RunningAnswerAsync(app);

        Assert.True(Quit(app, window, entry));
        Assert.True(app.Root.LayoutPrompt.IsOpen);
        Assert.False(app.Root.Exit.IsTaskPromptOpen);

        // "Don't save" is only recorded: the task prompt that follows can still be cancelled.
        app.Root.LayoutPrompt.DontSaveCommand.Execute(null);
        Assert.True(app.Root.Exit.IsTaskPromptOpen);
        Assert.True(network.IsDirty);

        app.Root.Exit.KeepRunningCommand.Execute(null);
        Assert.Equal(ExitStage.Idle, app.Root.Exit.Stage);
        Assert.True(network.IsDirty);
        Assert.Equal(0, app.Shell.ExitRequests);
        Assert.Equal(1, app.Services.GetRequiredService<GenerationCoordinator>().UnfinishedUserWork.Answers);

        // Asking again starts over with the layout, which is still unsaved.
        Assert.True(Quit(app, window, entry));
        Assert.True(app.Root.LayoutPrompt.IsOpen);
        app.Root.LayoutPrompt.DontSaveCommand.Execute(null);
        app.Root.Exit.StopAndExitCommand.Execute(null);
        await app.Root.Exit.Running;

        Assert.Equal(1, app.Shell.ExitRequests);
        Assert.False(network.IsDirty);
        Assert.Equal(MessageState.Stopped, (await StoredAsync(app, answer)).State);
    }

    [AvaloniaFact]
    public async Task The_task_prompt_names_answers_and_summaries_separately()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var first = await conversations.StartAsync(app.Workbench.CurrentWorkspace!.Id, "火车是怎么发明的？", CancellationToken.None);
        await UiWait.UntilAsync(() => conversations.LoadAsync(first.Conversation, CancellationToken.None).Result!.Messages.All(m => m.IsFinished));
        app.Client.KnowledgeGate = new TaskCompletionSource();
        Assert.True((await app.Services.GetRequiredService<Application.Summaries.SummaryService>().SummarizeAsync(first.Conversation, CancellationToken.None)).Accepted);
        await RunningAnswerAsync(app);

        Assert.True(app.Root.Exit.InterceptClose());

        Assert.Contains("1 个回答、1 个总结", app.Root.Exit.TaskMessage, StringComparison.Ordinal);
        app.Root.Exit.KeepRunningCommand.Execute(null);
        app.Client.KnowledgeGate.TrySetResult();
    }
}
