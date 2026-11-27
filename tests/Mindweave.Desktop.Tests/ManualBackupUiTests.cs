using System.Globalization;
using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Export;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Infrastructure.ManualBackup;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The manual backup's dialog (docs/design/sharing-backup.md §5), through the real services: opened from the workspace menu,
/// every workspace ticked, the size, Save through the save panel with "Mindweave" and the date, and the file written.
/// </summary>
public sealed class ManualBackupUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-backup-ui", Guid.NewGuid().ToString("N"));

    public ManualBackupUiTests() => Directory.CreateDirectory(_dir);

    private string Target => Path.Combine(_dir, "Mindweave.mwbackup");

    [AvaloniaFact]
    public async Task The_workspace_menu_opens_the_dialog_with_every_workspace_ticked_and_the_size()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var (first, second) = await SeedAsync(app);

        app.ShellPage.IsWorkspaceMenuOpen = true;
        window.UpdateLayout();
        var entry = Find<Button>(window, "ManualBackupEntry");
        Assert.Equal(UiText.ManualBackupEntry, entry.GetValue(Avalonia.Automation.AutomationProperties.NameProperty));
        entry.Command!.Execute(null);
        var backup = app.Root.ManualBackup;
        await backup.Loading;
        window.UpdateLayout();

        Assert.False(app.ShellPage.IsWorkspaceMenuOpen);
        Assert.True(Find<Border>(window, "ManualBackupDialog").IsEffectivelyVisible);
        Assert.Equal(new[] { first.Name.Value, second.Name.Value }.Order(StringComparer.Ordinal), backup.Workspaces.Select(w => w.Name).Order(StringComparer.Ordinal));
        Assert.All(backup.Workspaces, w => Assert.True(w.IsTicked));
        Assert.All(backup.Workspaces, w => Assert.True(w.Bytes > 0));
        Assert.Equal(UiText.ManualBackupSize(backup.Workspaces.Sum(w => w.Bytes)), backup.Size);
        backup.Workspaces.Single(w => w.Id == second.Id).IsTicked = false;
        Assert.Equal(UiText.ManualBackupSize(backup.Workspaces.Single(w => w.Id == first.Id).Bytes), backup.Size);
        Assert.True(backup.SaveCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task With_nothing_ticked_it_cannot_be_saved_and_says_why()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var backup = await OpenAsync(app);

        foreach (var workspace in backup.Workspaces)
        {
            workspace.IsTicked = false;
        }

        Assert.False(backup.SaveCommand.CanExecute(null));
        Assert.Equal(UiText.ManualBackupNothingTicked, backup.Notice);
        backup.Workspaces[0].IsTicked = true;
        Assert.True(backup.SaveCommand.CanExecute(null));
        Assert.Null(backup.Notice);
    }

    [AvaloniaFact]
    public async Task Saving_suggests_Mindweave_and_the_date_and_writes_only_the_ticked_workspaces()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        var (first, second) = await SeedAsync(app);
        var backup = await OpenAsync(app);
        backup.Workspaces.Single(w => w.Id == second.Id).IsTicked = false;

        // Cancelled in the panel: nothing is written or said.
        await backup.SaveCommand.ExecuteAsync(null);
        Assert.Equal(["Mindweave " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)], app.Shell.BackupRequests);
        Assert.Null(backup.Notice);
        Assert.False(File.Exists(Target));

        app.Shell.NextBackup = _ => new ExportDestination("Mindweave.mwbackup", Target, null);
        await backup.SaveCommand.ExecuteAsync(null);

        Assert.Equal(UiText.ManualBackupSaved("Mindweave.mwbackup"), backup.Notice);
        Assert.False(backup.NoticeIsError);
        Assert.False(backup.IsBusy);
        using var zip = ZipFile.OpenRead(Target);
        var text = string.Join("\n", zip.Entries.Select(e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return e.FullName + "\n" + reader.ReadToEnd();
        }));
        Assert.Contains("A question in the first workspace?", text, StringComparison.Ordinal);
        Assert.DoesNotContain("A question in the second workspace?", text, StringComparison.Ordinal);
        Assert.DoesNotContain(second.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(ManualBackupPackage.PageEntry(first.Id), text, StringComparison.Ordinal);

        backup.CloseCommand.Execute(null);
        Assert.False(backup.IsOpen);
    }

    [AvaloniaFact]
    public async Task A_failed_save_is_said_and_leaves_no_file()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var backup = await OpenAsync(app);
        var missing = Path.Combine(_dir, "none", "Mindweave.mwbackup");
        app.Shell.NextBackup = _ => new ExportDestination("Mindweave.mwbackup", missing, null);

        await backup.SaveCommand.ExecuteAsync(null);

        Assert.True(backup.NoticeIsError);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }

    [AvaloniaFact]
    public async Task Cancel_while_saving_stops_it_and_leaves_no_file()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var backup = await OpenAsync(app);
        app.Shell.NextBackup = _ =>
        {
            // The panel has closed; the save starts right after and is cancelled at once.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => backup.CancelCommand.Execute(null));
            return new ExportDestination("Mindweave.mwbackup", Target, null);
        };

        await backup.SaveCommand.ExecuteAsync(null);

        Assert.Equal(UiText.ManualBackupCancelled, backup.Notice);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }

    /// <summary>The save panel and the package writer name the same extension.</summary>
    [Fact]
    public void The_save_panel_offers_the_packages_own_extension() =>
        Assert.Equal(ManualBackupPackage.FileExtension, SystemShell.BackupExtension);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<ManualBackupViewModel> OpenAsync(DesktopHarness app)
    {
        app.ShellPage.BackUpCommand.Execute(null);
        await app.Root.ManualBackup.Loading;
        return app.Root.ManualBackup;
    }

    /// <summary>Two workspaces with an answered question each.</summary>
    internal static async Task<(Workspace First, Workspace Second)> SeedAsync(DesktopHarness app, string firstName = "Trains", string secondName = "Cooking")
    {
        var first = await app.StartWithWorkspaceAsync(firstName);
        await app.ConfigureModelAsync();
        var second = await app.Services.GetRequiredService<IWorkspaces>().CreateAsync(WorkspaceName.FromStored(secondName), CancellationToken.None);
        var conversations = app.Services.GetRequiredService<IConversations>();
        foreach (var (workspace, question) in new[] { (first.Id, "A question in the first workspace?"), (second.Id, "A question in the second workspace?") })
        {
            var sent = await conversations.StartAsync(workspace, question, CancellationToken.None);
            Assert.True(sent.Accepted);
            var store = app.Services.GetRequiredService<Application.Storage.IConversationStore>();
            for (var tries = 0; !(await store.GetMessagesAsync(sent.Conversation, CancellationToken.None)).Any(m => m.Id == sent.Answer && m.IsFinished); tries++)
            {
                Assert.True(tries < 500, "The answer did not finish.");
                await Task.Delay(10, CancellationToken.None);
            }
        }

        return (first, second);
    }
}
