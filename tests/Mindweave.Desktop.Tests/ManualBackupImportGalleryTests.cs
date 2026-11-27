using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Languages;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Importing a manual backup, for looking at (MINDWEAVE_SCREENSHOT_DIR, run with <c>--filter FullyQualifiedName~ManualBackupImportGallery</c>):
/// the dialog for a backup into a folder that has none of it, the dialog for one that went on in both places, what it says
/// after importing, the conversation joined by time, and a refused file: backup-import-*.png.
/// </summary>
[Trait("Category", "Gallery")]
[Collection(Text.LanguageTests.Name)]
public class ManualBackupImportGalleryTests
{
    [AvaloniaTheory]
    [InlineData("Light", UiLanguage.English)]
    [InlineData("Dark", UiLanguage.English)]
    [InlineData("Light", UiLanguage.Chinese)]
    [InlineData("Dark", UiLanguage.Chinese)]
    public async Task Manual_backup_import_as_it_looks(string theme, UiLanguage language)
    {
        var shots = Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(shots), "Screenshots only.");
        var before = CurrentLanguage.Value;
        CurrentLanguage.Set(language);
        var dir = Path.Combine(Path.GetTempPath(), "mindweave-backup-import-gallery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var english = language == UiLanguage.English;
            string T(string en, string zh) => english ? en : zh;
            var look = $"{theme.ToLowerInvariant()}-{(english ? "en" : "zh")}";
            var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            var start = Path.Combine(dir, T("Mindweave 2026-11-27.mwbackup", "Mindweave 2026-11-27.mwbackup"));
            var later = Path.Combine(dir, "Mindweave 2026-11-28.mwbackup");

            // There: two workspaces, one conversation in each, saved.
            await using var there = DesktopHarness.Create();
            there.ShowWindow(1100, 760, variant);
            var first = await there.StartWithWorkspaceAsync(T("History of railways", "铁路史"));
            await there.ConfigureModelAsync();
            var second = await there.Services.GetRequiredService<Application.Workspaces.IWorkspaces>().CreateAsync(WorkspaceName.FromStored(T("Cooking", "烹饪")), CancellationToken.None);
            var conversations = there.Services.GetRequiredService<IConversations>();
            there.Client.Reply(T("They grew out of colliery wagonways, which were built for carts about five feet wide.", "它们脱胎于煤矿的马拉轨道车，那些轨道是按约五英尺宽的马车修的。"));
            var trains = await conversations.StartAsync(first.Id, T("Where does the railway gauge come from?", "铁路轨距是怎么来的？"), CancellationToken.None);
            await Until(there, trains.Conversation, trains.Answer!.Value);
            there.Client.Reply(T("Yeast makes gas, and the gluten holds it.", "酵母产生气体，面筋把气体留住。"));
            var bread = await conversations.StartAsync(second.Id, T("Why does bread rise?", "面包为什么会发起来？"), CancellationToken.None);
            await Until(there, bread.Conversation, bread.Answer!.Value);
            await ManualBackupImportUiTests.SaveAsync(there, start);

            // Here: a workspace of its own, then the backup.
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1100, 760, variant);
            await app.StartWithWorkspaceAsync(T("Reading notes", "读书笔记"));
            await app.ConfigureModelAsync();
            var import = app.Root.Import;
            await import.OpenAsync(start);
            window.UpdateLayout();
            Screenshots.Capture(window, $"backup-import-1-dialog-{look}");
            await import.ImportCommand.ExecuteAsync(null);
            window.UpdateLayout();
            Screenshots.Capture(window, $"backup-import-2-done-{look}");
            import.CloseCommand.Execute(null);

            // Both go on in the same conversation; the later backup is joined by time.
            there.Client.Reply(T("Stephenson kept the width for the Liverpool and Manchester Railway.", "斯蒂芬森在利物浦—曼彻斯特铁路上保留了这个宽度。"));
            await ManualBackupImportUiTests.AskAsync(there, trains.Conversation, T("Who kept that width?", "是谁保留了这个宽度？"));
            app.Client.Reply(T("Brunel's Great Western used 7 ft until 1892.", "布鲁内尔的大西部铁路用 7 英尺轨距，直到 1892 年。"));
            await ManualBackupImportUiTests.AskAsync(app, trains.Conversation, T("Did anyone use a wider gauge?", "有人用过更宽的轨距吗？"));
            there.Client.Reply(T("Parliament chose 4 ft 8½ in in 1846.", "议会在 1846 年选定了 4 英尺 8½ 英寸。"));
            await ManualBackupImportUiTests.AskAsync(there, trains.Conversation, T("When did Britain settle it?", "英国是什么时候定下来的？"));
            await ManualBackupImportUiTests.SaveAsync(there, later);
            await import.OpenAsync(later);
            window.UpdateLayout();
            Screenshots.Capture(window, $"backup-import-3-joined-dialog-{look}");
            await import.ImportCommand.ExecuteAsync(null);
            window.UpdateLayout();
            Screenshots.Capture(window, $"backup-import-4-joined-done-{look}");
            import.CloseCommand.Execute(null);

            // The conversation, one line in the order the questions were asked.
            app.Services.GetRequiredService<Mindweave.Desktop.ViewModels.IWorkspaceSwitcher>().SwitchTo(app.Workbench.Workspaces.Single(w => w.Id == first.Id));
            await Task.Delay(200, CancellationToken.None);
            app.Workbench.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(trains.Conversation)));
            await Task.Delay(400, CancellationToken.None);
            window.UpdateLayout();
            Screenshots.Capture(window, $"backup-import-5-joined-conversation-{look}");

            var broken = Path.Combine(dir, T("Not a backup.mwbackup", "不是备份.mwbackup"));
            await File.WriteAllTextAsync(broken, "not a package");
            await import.OpenAsync(broken);
            window.UpdateLayout();
            Screenshots.Capture(window, $"backup-import-6-refused-{look}");
            import.CloseCommand.Execute(null);
        }
        finally
        {
            CurrentLanguage.Set(before);
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task Until(DesktopHarness app, Core.Conversations.ConversationId conversation, Core.Conversations.MessageId answer)
    {
        var store = app.Services.GetRequiredService<Application.Storage.IConversationStore>();
        for (var tries = 0; !(await store.GetMessagesAsync(conversation, CancellationToken.None)).Any(m => m.Id == answer && m.IsFinished); tries++)
        {
            Assert.True(tries < 500, "The answer did not finish.");
            await Task.Delay(10, CancellationToken.None);
        }
    }
}
