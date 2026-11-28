using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Languages;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The upkeep rows the owner judges by eye (D-042, §3.1), for looking at (MINDWEAVE_SCREENSHOT_DIR, run with
/// <c>--filter FullyQualifiedName~UpkeepGallery</c>): upkeep-1 the start-up page during an upgrade, upkeep-2 the notice
/// after it, upkeep-3 the "newer version" page with replaced data offered, upkeep-4 replaced data in 数据与存储,
/// upkeep-5 the exit prompt after a failed retry.
/// </summary>
[Trait("Category", "Gallery")]
[Collection(Text.LanguageTests.Name)]
public class UpkeepGalleryTests
{
    private static readonly int Current = MigrationCatalog.Default.LatestVersion;

    [AvaloniaTheory]
    [InlineData("Light", UiLanguage.English)]
    [InlineData("Dark", UiLanguage.English)]
    [InlineData("Light", UiLanguage.Chinese)]
    [InlineData("Dark", UiLanguage.Chinese)]
    public async Task Upkeep_as_it_looks(string theme, UiLanguage language)
    {
        var shots = Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(shots), "Screenshots only.");
        var before = CurrentLanguage.Value;
        CurrentLanguage.Set(language);
        try
        {
            var look = $"{theme.ToLowerInvariant()}-{(language == UiLanguage.English ? "en" : "zh")}";
            var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            var english = language == UiLanguage.English;
            var failing = new FailingDrafts();
            await using var app = DesktopHarness.Create(s => s.AddSingleton<IReadingStateStore>(sp => failing.Wrap(sp.GetRequiredService<SqliteDatabase>())));
            var window = app.ShowWindow(1100, 720, variant);

            var upgrading = new StartupViewModel();
            upgrading.Upgrading(new DataUpgradeStarting(Current - 1, Current, BackupFirst: true));
            app.Root.Show(upgrading);
            window.UpdateLayout();
            Screenshots.Capture(window, $"upkeep-1-upgrading-{look}");

            var page = await app.StartWithConversationAsync(waitForTitle: false);
            var backup = Path.Combine(app.Paths.BackupDirectory, $"mindweave-v{Current - 1}-20261127T090000000Z.db");
            app.ShellPage.ShowRestoreNotice(UiText.UpgradedNotice(new DataUpgrade(Current - 1, Current, backup, BackupMadeNow: true)), isError: false);
            window.UpdateLayout();
            await Task.Delay(300, CancellationToken.None); // The notice fades in.
            Screenshots.Capture(window, $"upkeep-2-notice-{look}");
            app.ShellPage.CloseRestoreNoticeCommand.Execute(null);

            var database = app.Services.GetRequiredService<SqliteDatabase>();
            var copy = await database.BackupAsync(BackupReason.BeforeRestore, CancellationToken.None);
            var replaced = $"{app.Paths.DatabasePath}.replaced-20261120T080000000Z";
            File.Move(copy, replaced);
            await database.BackupAsync(BackupReason.BeforeConversationDelete, CancellationToken.None);

            var storage = await BackupRestoreUiTests.OpenStorageAsync(app, window);
            storage.Refresh();
            window.UpdateLayout();
            Screenshots.Capture(window, $"upkeep-4-storage-{look}");

            var tooNew = new DatabaseOpenResult(DatabaseOpenStatus.TooNew, app.Paths.DataDirectory, app.Paths.DatabasePath, Current + 1, Current);
            var shell = app.Root.Current;
            app.Root.Show(new RecoveryViewModel(tooNew, database.ListRestorable(), app.Services.GetRequiredService<AppStartup>(), app.Shell));
            window.UpdateLayout();
            Screenshots.Capture(window, $"upkeep-3-recovery-{look}");
            app.Root.Show(shell!);
            window.UpdateLayout();

            failing.Fail = true;
            page.Composer.Text = english ? "A question not sent yet" : "还没发出去的问题";
            window.Close();
            await app.Root.Exit.Running;
            app.Root.Exit.RetryCommand.Execute(null);
            await app.Root.Exit.Running;
            window.UpdateLayout();
            Screenshots.Capture(window, $"upkeep-5-exit-{look}");
            failing.Fail = false;
            app.Root.Exit.RetryCommand.Execute(null);
            await app.Root.Exit.Running;
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            CurrentLanguage.Set(before);
        }
    }
}
