using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Export;
using Mindweave.Application.Languages;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The manual backup's menu entry and dialog, for looking at (MINDWEAVE_SCREENSHOT_DIR, run with
/// <c>--filter FullyQualifiedName~ManualBackupGallery</c>): backup-*.png, and backup-sample-*.mwbackup to unpack by hand.
/// </summary>
[Trait("Category", "Gallery")]
[Collection(Text.LanguageTests.Name)]
public class ManualBackupGalleryTests
{
    [AvaloniaTheory]
    [InlineData("Light", UiLanguage.English)]
    [InlineData("Dark", UiLanguage.English)]
    [InlineData("Light", UiLanguage.Chinese)]
    [InlineData("Dark", UiLanguage.Chinese)]
    public async Task Manual_backup_as_it_looks(string theme, UiLanguage language)
    {
        var shots = Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(shots), "Screenshots only.");
        var before = CurrentLanguage.Value;
        CurrentLanguage.Set(language);
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1100, 720, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
            var look = $"{theme.ToLowerInvariant()}-{(language == UiLanguage.English ? "en" : "zh")}";
            var english = language == UiLanguage.English;
            var first = await app.StartWithWorkspaceAsync(english ? "History of railways" : "铁路史");
            await app.ConfigureModelAsync();
            var workspaces = app.Services.GetRequiredService<IWorkspaces>();
            var conversations = app.Services.GetRequiredService<IConversations>();
            var asked = new List<SendOutcome>();
            foreach (var (name, questions) in new[]
            {
                (english ? "Cooking" : "烹饪", english ? new[] { "Why does bread rise?", "What does salt do to dough?" } : ["面包为什么会发起来？", "盐对面团有什么作用？"]),
                (english ? "Reading notes" : "读书笔记", english ? new[] { "What is the argument of chapter 3?" } : ["第三章的论点是什么？"]),
            })
            {
                var workspace = await workspaces.CreateAsync(WorkspaceName.FromStored(name), CancellationToken.None);
                app.Workbench.Workspaces.Add(workspace); // As the menu's own "New workspace…" would, without switching to it.
                foreach (var question in questions)
                {
                    asked.Add(await conversations.StartAsync(workspace.Id, question, CancellationToken.None));
                }
            }

            asked.Add(await conversations.StartAsync(first.Id, english ? "How did trains come to be?" : "火车是怎么来的？", CancellationToken.None));
            var store = app.Services.GetRequiredService<Application.Storage.IConversationStore>();
            foreach (var sent in asked)
            {
                for (var tries = 0; !(await store.GetMessagesAsync(sent.Conversation, CancellationToken.None)).Any(m => m.Id == sent.Answer && m.IsFinished); tries++)
                {
                    Assert.True(tries < 500, "The answer did not finish.");
                    await Task.Delay(10, CancellationToken.None);
                }
            }

            app.ShellPage.IsWorkspaceMenuOpen = true;
            await Task.Delay(400, CancellationToken.None); // The menu fades in.
            Screenshots.Capture(window, $"backup-1-menu-{look}");
            app.ShellPage.BackUpCommand.Execute(null);
            var backup = app.Root.ManualBackup;
            await backup.Loading;
            window.UpdateLayout();
            Screenshots.Capture(window, $"backup-2-dialog-{look}");
            backup.Workspaces[^1].IsTicked = false;
            window.UpdateLayout();
            Screenshots.Capture(window, $"backup-3-unticked-{look}");
            var file = Path.Combine(shots!, $"backup-sample-{(english ? "en" : "zh")}.mwbackup");
            app.Shell.NextBackup = _ => new ExportDestination(Path.GetFileName(file), file, null);
            await backup.SaveCommand.ExecuteAsync(null);
            window.UpdateLayout();
            Screenshots.Capture(window, $"backup-4-saved-{look}");
            backup.CloseCommand.Execute(null);
        }
        finally
        {
            CurrentLanguage.Set(before);
        }
    }
}
