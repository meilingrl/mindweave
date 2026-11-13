using System.Windows.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Models;
using Mindweave.Application.Storage;
using Mindweave.Core.Models;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Health check 2026-11-12, item 3: an exception that leaves a UI command must not end the application. The paths
/// found by the audit say what happened on their page; anything else is caught at the dispatcher, logged and named.
/// </summary>
public class UnexpectedUiErrorTests
{
    [AvaloniaFact]
    public async Task An_exception_leaving_the_dispatcher_is_reported_on_the_shell_and_the_loop_goes_on()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        using var guard = new UiExceptionGuard(Dispatcher.UIThread, () => app.ShellPage, NullLogger.Instance);

        Dispatcher.UIThread.Post(() => throw new InvalidOperationException("boom"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(UiText.UnexpectedError, app.ShellPage.RestoreNotice);
        Assert.True(app.ShellPage.RestoreNoticeIsError);
        var ran = false;
        Dispatcher.UIThread.Post(() => ran = true);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ran);
    }

    [AvaloniaFact]
    public async Task Renaming_on_the_conversation_page_while_storage_refuses_keeps_the_typed_name()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "火车是怎么发明的？";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        // A real write failure reports itself and pauses storage; the next write is refused before it is tried.
        app.Services.GetRequiredService<StorageHealth>().Report(new StorageWriteException(StorageFailureKind.Full, "simulated full disk", null));

        page.BeginRenameCommand.Execute(null);
        page.RenameText = "新的名字";
        ((ICommand)page.CommitRenameCommand).Execute(null);
        await UiWait.UntilAsync(() => !page.CommitRenameCommand.IsRunning);
        Dispatcher.UIThread.RunJobs();

        Assert.True(page.IsRenaming);
        Assert.Equal("新的名字", page.RenameText);
    }

    [AvaloniaFact]
    public async Task Saving_model_settings_while_storage_refuses_says_so()
    {
        var refusing = new RefusingSettingsStore();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IModelSettingsStore>(sp => refusing.Wrap(sp.GetRequiredService<SqliteDatabase>())));
        app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        var settings = new ModelSettingsViewModel(app.Services.GetRequiredService<IModelSettings>());
        await settings.Loaded;
        settings.BaseUrl = "https://fake.example/v1";
        settings.Model = "m";
        settings.ApiKey = "sk-test";
        refusing.Refuse = true;

        ((ICommand)settings.SaveCommand).Execute(null);
        await UiWait.UntilAsync(() => !settings.SaveCommand.IsRunning);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(UiText.RowStoragePaused, settings.Status);
        Assert.True(settings.StatusIsError);
    }
}
