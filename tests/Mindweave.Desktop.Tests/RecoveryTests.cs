using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Startup;
using Mindweave.Desktop.ViewModels;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Desktop.Tests;

public class RecoveryTests
{
    [AvaloniaFact]
    public async Task Unreadable_database_shows_recovery_and_retry_reopens_after_the_cause_is_fixed()
    {
        await using var app = DesktopHarness.Create();
        Directory.CreateDirectory(app.Paths.DataDirectory);
        await File.WriteAllTextAsync(app.Paths.DatabasePath, "definitely not sqlite, but long enough to be read as a header....................................................", TestContext.Current.CancellationToken);

        await app.StartAsync();

        var recovery = Assert.IsType<RecoveryViewModel>(app.Root.Current);
        Assert.Equal(DatabaseOpenStatus.Unreadable, recovery.Result.Status);
        recovery.OpenDataFolderCommand.Execute(null);
        Assert.Equal([app.Paths.DataDirectory], app.Shell.OpenedFolders);

        File.Move(app.Paths.DatabasePath, app.Paths.DatabasePath + ".moved-away");
        await recovery.RetryCommand.ExecuteAsync(null);

        Assert.IsType<OnboardingViewModel>(app.Root.Current);
    }

    [AvaloniaFact]
    public async Task Failed_upgrade_offers_the_backup_and_restoring_requires_confirmation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mindweave-desktop-tests", Guid.NewGuid().ToString("N"));
        await using (var before = DesktopHarness.Create(directory: directory))
        {
            await before.StartWithWorkspaceAsync("Kept");
        }

        var broken = new MigrationCatalog(
        [
            .. MigrationCatalog.Default.Migrations,
            new Migration(MigrationCatalog.Default.LatestVersion + 1, "broken", (_, _) => throw new InvalidOperationException("boom")),
        ]);
        await using var app = DesktopHarness.Create(services => services.AddSingleton(broken), directory);

        await app.StartAsync();

        var recovery = Assert.IsType<RecoveryViewModel>(app.Root.Current);
        Assert.Equal(DatabaseOpenStatus.MigrationFailed, recovery.Result.Status);
        var backup = Assert.Single(recovery.Backups);
        Assert.False(recovery.IsConfirmingRestore);

        recovery.ChooseRestoreCommand.Execute(backup);
        Assert.True(recovery.IsConfirmingRestore);
        Assert.Contains("不会删除", recovery.RestoreConsequence, StringComparison.Ordinal);
        recovery.CancelRestoreCommand.Execute(null);
        Assert.False(recovery.IsConfirmingRestore);
        Assert.Empty(Directory.GetFiles(app.Paths.DataDirectory, "mindweave.db.replaced-*"));

        recovery.ChooseRestoreCommand.Execute(backup);
        await recovery.ConfirmRestoreCommand.ExecuteAsync(null);

        // The restored backup still needs the same broken upgrade, so recovery is shown again —
        // but the pre-restore file was kept, not deleted.
        Assert.IsType<RecoveryViewModel>(app.Root.Current);
        Assert.NotEmpty(Directory.GetFiles(app.Paths.DataDirectory, "mindweave.db.replaced-*"));
        SqliteConnection.ClearAllPools();
    }

    [AvaloniaFact]
    public async Task Exit_on_the_recovery_page_asks_the_shell_to_quit()
    {
        await using var app = DesktopHarness.Create();
        Directory.CreateDirectory(app.Paths.DataDirectory);
        await File.WriteAllBytesAsync(app.Paths.DatabasePath, new byte[512], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(app.Paths.DatabasePath, new string('x', 600), TestContext.Current.CancellationToken);

        await app.StartAsync();
        var recovery = Assert.IsType<RecoveryViewModel>(app.Root.Current);
        recovery.ExitCommand.Execute(null);
        await app.Root.Exit.Running;

        // Through the one exit sequence, not a forced shutdown that skips it.
        Assert.Equal(ExitStage.Releasing, app.Root.Exit.Stage);
        Assert.Equal(1, app.Shell.ExitRequests);
    }
}
