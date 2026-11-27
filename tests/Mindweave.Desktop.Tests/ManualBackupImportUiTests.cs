using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Export;
using Mindweave.Application.Import;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.ViewModels;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Importing a manual backup through the import dialog (docs/design/sharing-backup.md §5), with the real services: the same
/// "Import…" and drop as a share, per workspace what will happen, and after it what was done.
/// </summary>
public sealed class ManualBackupImportUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-backup-import-ui", Guid.NewGuid().ToString("N"));

    public ManualBackupImportUiTests() => Directory.CreateDirectory(_dir);

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

    [AvaloniaFact]
    public async Task Import_takes_a_backup_shows_each_workspace_and_then_names_what_was_done()
    {
        var package = Path.Combine(_dir, "Mindweave.mwbackup");
        Workspace first, second;
        await using (var there = DesktopHarness.Create())
        {
            there.ShowWindow(1100, 720);
            (first, second) = await ManualBackupUiTests.SeedAsync(there);
            await SaveAsync(there, package);
        }

        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("Here");
        var import = app.Root.Import;
        Assert.True(import.Takes([package]));
        app.Shell.NextSharedFile = () => package;
        import.Pick();
        await import.Opening;
        window.UpdateLayout();

        Assert.True(import.IsOpen);
        Assert.True(import.IsBackup);
        Assert.Equal(UiText.ImportBackupTitle, import.Title);
        Assert.Null(import.Problem);
        Assert.False(import.ShowsTargets);
        Assert.False(Find<StackPanel>(window, "ImportTargets").IsVisible);
        Assert.True(import.ImportCommand.CanExecute(null));
        Assert.Equal(
            new[] { (UiText.ImportBackupWorkspace(first.Name.Value, true), UiText.ImportNew(1)), (UiText.ImportBackupWorkspace(second.Name.Value, true), UiText.ImportNew(1)) }.Order(),
            import.BackupWorkspaces.Select(w => (w.Heading, Assert.Single(w.Lines))).Order());

        await import.ImportCommand.ExecuteAsync(null);
        window.UpdateLayout();

        Assert.True(import.IsDone);
        Assert.True(import.IsOpen);
        Assert.Equal(UiText.ImportBackupDoneHeading, import.CountsHeading);
        Assert.Equal(UiText.ShareClose, import.CloseText);
        Assert.False(import.ImportCommand.CanExecute(null));
        Assert.Contains(import.BackupWorkspaces, w => w.Heading == UiText.ImportBackupWorkspace(first.Name.Value, true) && w.Lines.SequenceEqual([UiText.ImportBackupDoneNew(1)]));
        Assert.Contains(app.Workbench.Workspaces, w => w.Id == first.Id);
        Assert.Contains(app.Workbench.Workspaces, w => w.Id == second.Id);

        // The same backup again: everything is here.
        import.CloseCommand.Execute(null);
        await import.OpenAsync(package);
        Assert.True(import.IsAllHere);
        Assert.Equal(UiText.ImportBackupAllHere, import.AllHereText);
        Assert.False(import.ImportCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task A_conversation_asked_in_on_both_sides_is_said_to_be_joined_by_time()
    {
        var start = Path.Combine(_dir, "start.mwbackup");
        var later = Path.Combine(_dir, "later.mwbackup");
        await using var there = DesktopHarness.Create();
        there.ShowWindow(1100, 720);
        var (first, _) = await ManualBackupUiTests.SeedAsync(there);
        await SaveAsync(there, start);

        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("Here");
        await app.ConfigureModelAsync();
        await ImportAsync(app, start);
        var conversation = (await app.Services.GetRequiredService<IConversations>().ListAsync(first.Id, CancellationToken.None)).Single().Conversation.Id;
        await AskAsync(there, conversation, "Asked there?");
        await AskAsync(app, conversation, "Asked here?");
        await SaveAsync(there, later);

        var import = app.Root.Import;
        await import.OpenAsync(later);

        var workspace = import.BackupWorkspaces.Single(w => w.Heading == UiText.ImportBackupWorkspace(first.Name.Value, false));
        Assert.Contains(UiText.ImportBackupJoined(1), workspace.Lines);
        await import.ImportCommand.ExecuteAsync(null);
        Assert.Contains(UiText.ImportBackupDoneJoined(1), import.BackupWorkspaces.SelectMany(w => w.Lines));
        var messages = await app.Services.GetRequiredService<Application.Storage.IConversationStore>().GetMessagesAsync(conversation, CancellationToken.None);
        Assert.Equal(["A question in the first workspace?", "Asked there?", "Asked here?"], messages.Where(m => m.Role == MessageRole.User).Select(m => m.Text));
    }

    [AvaloniaFact]
    public async Task While_an_answer_is_written_where_the_backup_adds_it_asks_to_finish_it_first()
    {
        var start = Path.Combine(_dir, "start.mwbackup");
        var later = Path.Combine(_dir, "later.mwbackup");
        await using var there = DesktopHarness.Create();
        there.ShowWindow(1100, 720);
        var (first, _) = await ManualBackupUiTests.SeedAsync(there);
        await SaveAsync(there, start);
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("Here");
        await app.ConfigureModelAsync();
        await ImportAsync(app, start);
        var conversation = (await app.Services.GetRequiredService<IConversations>().ListAsync(first.Id, CancellationToken.None)).Single().Conversation.Id;
        await AskAsync(there, conversation, "Asked there?");
        await SaveAsync(there, later);
        var held = app.Client.Hold("Being written");
        await app.Services.GetRequiredService<IConversations>().AskAsync(conversation, "Asked here?", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var import = app.Root.Import;
        await import.OpenAsync(later);

        Assert.Equal(UiText.ImportBackupRunning, import.Problem);
        Assert.False(import.ImportCommand.CanExecute(null));
        held.Release.SetResult();
    }

    [AvaloniaFact]
    public async Task A_file_that_is_no_backup_is_refused_and_says_why()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("Here");
        var path = Path.Combine(_dir, "broken.mwbackup");
        await File.WriteAllTextAsync(path, "not a package");

        var import = app.Root.Import;
        await import.OpenAsync(path);

        Assert.True(import.IsOpen);
        Assert.Equal(UiText.ImportBackupRefused(ImportRefusal.NotAShare), import.Problem);
        Assert.False(import.ShowsChoice);
        Assert.Empty(import.BackupWorkspaces);
    }

    internal static async Task SaveAsync(DesktopHarness app, string path)
    {
        app.ShellPage.BackUpCommand.Execute(null);
        var backup = app.Root.ManualBackup;
        await backup.Loading;
        app.Shell.NextBackup = _ => new ExportDestination(Path.GetFileName(path), path, null);
        await backup.SaveCommand.ExecuteAsync(null);
        Assert.Equal(UiText.ManualBackupSaved(Path.GetFileName(path)), backup.Notice);
        backup.CloseCommand.Execute(null);
    }

    internal static async Task ImportAsync(DesktopHarness app, string path)
    {
        await app.Root.Import.OpenAsync(path);
        await app.Root.Import.ImportCommand.ExecuteAsync(null);
        Assert.True(app.Root.Import.IsDone);
        app.Root.Import.CloseCommand.Execute(null);
    }

    internal static async Task AskAsync(DesktopHarness app, ConversationId conversation, string question)
    {
        var sent = await app.Services.GetRequiredService<IConversations>().AskAsync(conversation, question, CancellationToken.None);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        var store = app.Services.GetRequiredService<Application.Storage.IConversationStore>();
        for (var tries = 0; !(await store.GetMessagesAsync(conversation, CancellationToken.None)).Any(m => m.Id == sent.Answer && m.IsFinished); tries++)
        {
            Assert.True(tries < 500, "The answer did not finish.");
            await Task.Delay(10, CancellationToken.None);
        }
    }
}
