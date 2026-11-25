using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Export;
using Mindweave.Application.Resources;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Desktop.ViewModels;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>The choice "include conversation content" in the share dialog (docs/design/sharing.md S6, S7, §6.3, §9), through the real services.</summary>
public sealed class ShareContentUiTests : IDisposable
{
    private const string RootQuestion = "火车是怎么发明的？";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-share-content-ui", Guid.NewGuid().ToString("N"));

    public ShareContentUiTests() => Directory.CreateDirectory(_dir);

    private string Target => Path.Combine(_dir, "share.html");

    [AvaloniaFact]
    public async Task Content_is_off_unless_chosen_each_time_the_dialog_opens()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app);
        window.UpdateLayout();
        var box = Find<CheckBox>(window, "ShareIncludeContent");

        Assert.False(share.IncludesContent);
        Assert.False(box.IsChecked);
        Assert.Equal(UiText.ShareLeavesOut, share.LeavesOut);

        box.IsChecked = true;
        await share.Building;
        Assert.True(share.IncludesContent);
        Assert.Equal(UiText.ShareContentLeavesOut, share.LeavesOut);

        share.CloseCommand.Execute(null);
        Assert.False((await OpenAsync(app)).IncludesContent);
    }

    [AvaloniaFact]
    public async Task With_content_the_list_names_the_attachments_and_the_number_of_web_citations()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app, attachments: true);
        var share = await OpenAsync(app);
        Assert.DoesNotContain(share.Contents, c => c.Contains("gauge.txt", StringComparison.Ordinal));

        await IncludeAsync(share);

        Assert.Contains(UiText.ShareHoldsAttachments(["gauge.txt"], 1), share.Contents);
        Assert.Contains(UiText.ShareHoldsMessages, share.Contents);
        Assert.Contains(UiText.ShareHoldsSummaries(3), share.Contents);
        Assert.DoesNotContain(share.Contents, c => c == UiText.ShareHoldsWebCitations(0));

        // The structure again: the list goes back with it.
        share.IncludesContent = false;
        Assert.DoesNotContain(UiText.ShareHoldsMessages, share.Contents);
    }

    [AvaloniaFact]
    public async Task Names_are_shown_as_they_leave_the_apps_names_only_with_content()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app);
        Assert.Contains(UiText.UntitledConversation, share.Dots.Select(d => d.Name));

        await IncludeAsync(share);

        Assert.DoesNotContain(UiText.UntitledConversation, share.Dots.Select(d => d.Name));
        var rows = (await app.Services.GetRequiredService<ConversationService>().ListAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None)).ToDictionary(c => c.Conversation.Id);
        var untitled = share.Dots.Single(d => !d.HasSummary);
        Assert.Equal(ConversationNames.Of(null, rows[untitled.Id].Quote, rows[untitled.Id].FirstQuestion), untitled.Name);
    }

    [AvaloniaFact]
    public async Task The_saved_file_holds_the_questions_and_answers_of_what_is_ticked_only()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app);
        await IncludeAsync(share);
        share.Dots.Single(d => d.Name == "铁轨").IsTicked = false;
        Assert.False(share.SaveCommand.CanExecute(null)); // Not before the list shows what is ticked now.
        await share.Building;
        Assert.True(share.SaveCommand.CanExecute(null));

        app.Shell.NextShare = _ => new ExportDestination("share.html", Target, null);
        await share.SaveCommand.ExecuteAsync(null);

        Assert.Equal(UiText.ShareSaved("share.html"), share.Notice);
        var file = await File.ReadAllTextAsync(Target);
        Assert.Contains(RootQuestion, file, StringComparison.Ordinal);
        Assert.Contains("它跑多快？", file, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"content\"", file, StringComparison.Ordinal);
        Assert.Contains("id=\"page-drawn\"", file, StringComparison.Ordinal);
        Assert.DoesNotContain("铁轨用什么做", file, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Without_the_choice_the_saved_file_still_holds_no_message_text()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app);
        await IncludeAsync(share);
        share.IncludesContent = false;
        app.Shell.NextShare = _ => new ExportDestination("share.html", Target, null);

        await share.SaveCommand.ExecuteAsync(null);

        var file = await File.ReadAllTextAsync(Target);
        Assert.Contains("\"kind\":\"structure\"", file, StringComparison.Ordinal);
        Assert.DoesNotContain("火车是怎么发明", file, StringComparison.Ordinal);
        Assert.DoesNotContain("它跑多快", file, StringComparison.Ordinal);
    }

    /// <summary>Nothing leaves that was not listed: an answer that came meanwhile is shown first, and nothing is written.</summary>
    [AvaloniaFact]
    public async Task Content_that_changed_while_the_save_dialog_was_open_is_not_saved_unseen()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app);
        await IncludeAsync(share);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var root = share.Dots.Single(d => d.Name == "火车起源").Id;
        app.Shell.NextShare = _ =>
        {
            var answer = conversations.LoadAsync(root, CancellationToken.None).GetAwaiter().GetResult()!.Messages[^1];
            var retry = conversations.RetryAsync(root, answer.Id, CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(retry.Accepted);
            SpinWait.SpinUntil(() => conversations.LoadAsync(root, CancellationToken.None).GetAwaiter().GetResult()!.Messages.All(m => m.IsFinished), TimeSpan.FromSeconds(10));
            return new ExportDestination("share.html", Target, null);
        };

        await share.SaveCommand.ExecuteAsync(null);

        Assert.Equal(UiText.ShareChanged, share.Notice);
        Assert.False(File.Exists(Target));

        app.Shell.NextShare = _ => new ExportDestination("share.html", Target, null);
        await UiWait.UntilAsync(() => share.SaveCommand.CanExecute(null));
        await share.SaveCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Target));
    }

    [AvaloniaFact]
    public async Task An_answer_still_being_generated_is_said_to_be_left_out()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var root = (await conversations.ListAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None)).Single(c => c.FirstQuestion == RootQuestion).Conversation.Id;
        var held = app.Client.Hold("…");
        await conversations.AskAsync(root, "还没回答的问题？", CancellationToken.None);
        var share = await OpenAsync(app);
        await IncludeAsync(share);
        Assert.Equal(UiText.ShareContentAnswering(["火车起源"]), share.AnsweringNote);

        app.Shell.NextShare = _ => new ExportDestination("share.html", Target, null);
        await share.SaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain("还没回答的问题", await File.ReadAllTextAsync(Target), StringComparison.Ordinal);
        held.Release.TrySetResult();
    }

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

    private async Task SeedAsync(DesktopHarness app, bool attachments = false)
    {
        app.Client.Titles.FailNext(GenerationErrorKind.Unauthorized, 100);
        await SummaryNetworkScenario.SeedSeenAsync(app, secondExploration: true);
        if (!attachments)
        {
            return;
        }

        var conversations = app.Services.GetRequiredService<ConversationService>();
        var resources = app.Services.GetRequiredService<IResources>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var root = (await conversations.ListAsync(workspace, CancellationToken.None)).Single(c => c.FirstQuestion == RootQuestion).Conversation.Id;
        var path = Path.Combine(_dir, "gauge.txt");
        await File.WriteAllTextAsync(path, "1435");
        var slot = new DraftSlot(workspace, root, "main");
        var file = (await resources.AttachFileAsync(slot, path, CancellationToken.None)).Resource!.Resource.Id;
        var paste = (await resources.AttachPasteAsync(slot, "pasted", CancellationToken.None)).Resource!.Resource.Id;
        Assert.True((await conversations.AskAsync(root, "附件里写了什么？", [file, paste], CancellationToken.None)).Accepted);
        await UiWait.UntilAsync(() => conversations.LoadAsync(root, CancellationToken.None).Result!.Messages.All(m => m.IsFinished));
    }

    private static async Task<ShareViewModel> OpenAsync(DesktopHarness app)
    {
        var list = app.ShellPage.Conversations;
        await UiWait.UntilAsync(() => list.Items.Count > 0);
        var id = (await app.Services.GetRequiredService<ConversationService>().ListAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None)).Single(c => c.FirstQuestion == RootQuestion).Conversation.Id;
        list.ShareRowCommand.Execute(list.Items.Single(i => i.Id == id));
        await app.Root.Share.Loading;
        return app.Root.Share;
    }

    private static async Task IncludeAsync(ShareViewModel share)
    {
        share.IncludesContent = true;
        await share.Building;
    }
}
