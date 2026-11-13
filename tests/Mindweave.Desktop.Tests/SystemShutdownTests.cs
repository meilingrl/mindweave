using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Storage;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Health check 2026-11-12, gap-lifecycle-2: a logout, restart or shutdown of the system is vetoed only when something
/// must be asked; otherwise everything is saved and the system goes ahead.
/// </summary>
public class SystemShutdownTests
{
    [AvaloniaFact]
    public async Task With_nothing_to_ask_a_system_shutdown_saves_the_draft_and_goes_ahead()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var page = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        await page.DraftLoaded;
        page.Composer.Text = "关机前写下的问题";

        var request = SystemRequest();
        App.OnShutdownRequested(app.Root, request);

        Assert.False(request.Cancel);
        Assert.Equal(ExitStage.Releasing, app.Root.Exit.Stage);
        var stored = await app.Services.GetRequiredService<IPreferenceStore>().GetAsync(Application.Reading.ReadingStateService.NewDraftKey(app.Workbench.CurrentWorkspace!.Id), CancellationToken.None);
        Assert.Equal("关机前写下的问题", stored);
    }

    [AvaloniaFact]
    public async Task With_an_answer_running_a_system_shutdown_is_held_and_asked_about()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        await ExitScenario.RunningAnswerAsync(app);

        var request = SystemRequest();
        App.OnShutdownRequested(app.Root, request);

        Assert.True(request.Cancel);
        Assert.True(app.Root.Exit.IsTaskPromptOpen);
    }

    /// <summary>Review 2026-11-13: a save that outlasts the system's wait holds the request, and the app quits itself once saved.</summary>
    [AvaloniaFact]
    public async Task A_save_that_outlasts_the_wait_holds_the_request_and_quits_once_saved()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IPreferenceStore>(sp => new GatedPreferences(new Infrastructure.Sqlite.SqlitePreferenceStore(sp.GetRequiredService<Infrastructure.Sqlite.SqliteDatabase>()), gate)));
        app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var page = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        await page.DraftLoaded;
        page.Composer.Text = "慢慢保存的问题";
        var limit = App.SystemShutdownSaveLimit;
        App.SystemShutdownSaveLimit = TimeSpan.FromMilliseconds(200);
        try
        {
            var request = SystemRequest();
            App.OnShutdownRequested(app.Root, request);

            Assert.True(request.Cancel);
            Assert.Equal(0, app.Shell.ExitRequests);
            gate.SetResult();
            await UiWait.UntilAsync(() => app.Shell.ExitRequests == 1);
            Assert.Equal(ExitStage.Releasing, app.Root.Exit.Stage);
        }
        finally
        {
            App.SystemShutdownSaveLimit = limit;
        }
    }

    /// <summary>The real preference store, whose writes wait for <paramref name="gate"/>.</summary>
    private sealed class GatedPreferences(IPreferenceStore inner, TaskCompletionSource gate) : IPreferenceStore
    {
        private IPreferenceStore Inner => inner;

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Inner.GetAsync(key, cancellationToken);

        public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            await gate.Task;
            await Inner.SetAsync(key, value, cancellationToken);
        }

        public async Task RemoveAsync(string key, CancellationToken cancellationToken)
        {
            await gate.Task;
            await Inner.RemoveAsync(key, cancellationToken);
        }
    }

    private static ShutdownRequestedEventArgs SystemRequest()
    {
        var request = new ShutdownRequestedEventArgs();
        var property = typeof(ShutdownRequestedEventArgs).GetProperty("IsOSShutdown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
        property.SetValue(request, true);
        Assert.True(App.IsSystemShutdown(request));
        return request;
    }
}
