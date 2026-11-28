using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Startup;
using Mindweave.Application.Workspaces;
using Mindweave.Desktop.ViewModels;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// What the user sees of an upgrade and of data a restore replaced (D-042, docs/design/diagnostics-and-upkeep.md §3.1):
/// the start-up page says an upgrade is running with a backup first, and one notice afterwards says to which version and
/// where the backup is (gap-schema-4); the data a restore replaced is offered like a backup, on the recovery page and in
/// 设置 › 数据与存储 (gap-schema-5).
/// </summary>
public class UpgradeUiTests
{
    private static readonly int Current = MigrationCatalog.Default.LatestVersion;

    private static MigrationCatalog Next => new([.. MigrationCatalog.Default.Migrations, Migration.Sql(Current + 1, "add note", "CREATE TABLE note (id TEXT PRIMARY KEY) STRICT;")]);

    [AvaloniaFact]
    public async Task An_upgrade_at_start_says_so_then_leaves_one_notice_with_the_version_and_the_backup()
    {
        var directory = NewDirectory();
        await using (var before = DesktopHarness.Create(directory: directory))
        {
            await before.StartWithWorkspaceAsync("Kept");
        }

        await using var app = DesktopHarness.Create(services => services.AddSingleton(Next), directory);
        var window = app.ShowWindow(1100, 720);
        var pages = new List<StartupViewModel>();
        app.Root.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RootViewModel.Current) && app.Root.Current is StartupViewModel page)
            {
                pages.Add(page);
            }
        };

        await app.StartAsync();

        var startup = Assert.Single(pages);
        await UiWait.UntilAsync(() => startup.Text != UiText.StartupOpening);
        Assert.Equal(UiText.StartupUpgrading(new DataUpgradeStarting(Current, Current + 1, BackupFirst: true)), startup.Text);
        Assert.IsType<ShellViewModel>(app.Root.Current);
        var backup = Assert.Single(app.Services.GetRequiredService<IBackupFiles>().ListBackups());
        Assert.Equal(UiText.UpgradedNotice(new DataUpgrade(Current, Current + 1, backup.Path, BackupMadeNow: true)), app.ShellPage.RestoreNotice);
        Assert.False(app.ShellPage.RestoreNoticeIsError);
        window.UpdateLayout();
        Assert.Equal(app.ShellPage.RestoreNotice, Find<TextBlock>(window, "RestoreNotice").Text);
        SqliteConnection.ClearAllPools();
    }

    [AvaloniaFact]
    public async Task A_start_without_an_upgrade_leaves_no_notice()
    {
        var directory = NewDirectory();
        await using (var before = DesktopHarness.Create(directory: directory))
        {
            await before.StartWithWorkspaceAsync("Kept");
        }

        await using var app = DesktopHarness.Create(directory: directory);
        await app.StartAsync();

        Assert.IsType<ShellViewModel>(app.Root.Current);
        Assert.Null(app.ShellPage.RestoreNotice);
    }

    [AvaloniaFact]
    public async Task The_recovery_page_offers_the_data_a_restore_replaced()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("Replaced later");
        var replaced = await KeepAsReplacedAsync(app, "20261120T080000000Z");
        await app.Services.GetRequiredService<SqliteDatabase>().CloseAsync();
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(app.Paths.DatabasePath, "definitely not sqlite, but long enough to be read as a header....................................................", TestContext.Current.CancellationToken);
        File.Delete(app.Paths.DatabasePath + "-wal");
        File.Delete(app.Paths.DatabasePath + "-shm");

        await app.StartAsync();

        var recovery = Assert.IsType<RecoveryViewModel>(app.Root.Current);
        var offered = Assert.Single(recovery.Backups, b => b.Reason == BackupReason.Replaced);
        Assert.Equal(replaced, offered.Path);
        Assert.Contains(UiText.LeftoverDatabase, UiText.BackupDetails(offered), StringComparison.Ordinal);

        recovery.ChooseRestoreCommand.Execute(offered);
        await recovery.ConfirmRestoreCommand.ExecuteAsync(null);

        Assert.IsType<ShellViewModel>(app.Root.Current);
        Assert.Equal(["Replaced later"], app.Workbench.Workspaces.Select(w => w.Name.Value));
        SqliteConnection.ClearAllPools();
    }

    [AvaloniaFact]
    public async Task Data_a_restore_replaced_is_restored_from_the_storage_page_like_a_backup()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("In the replaced data");
        var replaced = await KeepAsReplacedAsync(app, "20261120T080000000Z");
        await app.Services.GetRequiredService<IWorkspaces>().CreateAsync(Core.Workspaces.WorkspaceName.FromStored("Made afterwards"), CancellationToken.None);
        var oldRoot = app.Root;

        var page = await BackupRestoreUiTests.OpenStorageAsync(app, window);
        var row = Assert.Single(page.Rows, r => r.Entry.Leftover is not null);
        Assert.True(row.IsRestorable);
        Assert.True(row.CanRestore);
        Assert.Equal(UiText.SchemaVersionBadge(Current), row.VersionText);
        Assert.Equal(UiText.LeftoverHint, row.Hint);
        window.UpdateLayout();
        Assert.True(Find<Button>(window, "RestoreBackupButton").IsEffectivelyVisible);

        page.RestoreCommand.Execute(row);
        var dialog = app.Root.BackupRestore;
        Assert.True(dialog.IsOpen);
        Assert.Equal(BackupReason.Replaced, dialog.Target!.Reason);
        Assert.Equal(UiText.RestoreMadeAt(dialog.Target), dialog.Points[0]);
        await dialog.RestoreCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => !ReferenceEquals(app.Root, oldRoot));
        await app.Session.LastRestore;

        Assert.IsType<ShellViewModel>(app.Root.Current);
        Assert.Equal(["In the replaced data"], app.Workbench.Workspaces.Select(w => w.Name.Value));
        Assert.True(File.Exists(replaced), "The replaced data stays listed after it is restored, as a backup does.");
        Assert.Contains(app.Services.GetRequiredService<IBackupFiles>().ListBackups(), b => b.Reason == BackupReason.BeforeRestore);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Replaced_data_from_a_newer_build_is_listed_but_not_restorable()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("Now");
        var replaced = await KeepAsReplacedAsync(app, "20261120T080000000Z");
        using (var newer = new SqliteConnection($"Data Source={replaced};Pooling=False"))
        {
            newer.Open();
            using var command = newer.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {Current + 1};";
            command.ExecuteNonQuery();
        }

        var page = await BackupRestoreUiTests.OpenStorageAsync(app, window);
        var row = Assert.Single(page.Rows, r => r.Entry.Leftover is not null);

        Assert.True(row.IsRestorable);
        Assert.False(row.CanRestore);
        Assert.True(row.IsTooNew);
        Assert.Equal(UiText.BackupTooNew(Current + 1, Current), row.Hint);
        window.Close();
    }

    /// <summary>A verified copy of the current data, put beside the database under a "replaced" name.</summary>
    private static async Task<string> KeepAsReplacedAsync(DesktopHarness app, string stamp)
    {
        var database = app.Services.GetRequiredService<SqliteDatabase>();
        var copy = await database.BackupAsync(BackupReason.BeforeRestore, CancellationToken.None);
        var replaced = $"{app.Paths.DatabasePath}.replaced-{stamp}";
        File.Move(copy, replaced);
        return replaced;
    }

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "mindweave-desktop-tests", Guid.NewGuid().ToString("N"));
}
