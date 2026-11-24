using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Export;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;
using Mindweave.Infrastructure.Sqlite;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The share dialog (docs/design/sharing.md §6.3), through the real services: opened from a conversation's menu or a dot,
/// the dots with ticks, the list of what leaves, and the file saved. The seed is root › middle › leaf and root › sibling,
/// with summaries of root, leaf and sibling, and a second exploration with one summarized conversation.
/// </summary>
public sealed class ShareUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-share-ui", Guid.NewGuid().ToString("N"));

    public ShareUiTests() => Directory.CreateDirectory(_dir);

    private string Target => Path.Combine(_dir, "share.html");

    [AvaloniaFact]
    public async Task Share_in_a_rows_menu_opens_the_dialog_with_the_rows_exploration_ticked()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);

        var share = await OpenAsync(app, "leaf");

        Assert.True(share.IsOpen);
        Assert.True(Find<Border>(window, "ShareDialog").IsEffectivelyVisible);
        Assert.Equal(new[] { "火车起源", UiText.UntitledConversation, "火箭号速度", "铁轨" }.Order(StringComparer.Ordinal), Ticked(share).Order(StringComparer.Ordinal));
        Assert.Equal("火车起源", share.Title);

        // Every conversation of the workspace is offered; the other exploration is not ticked.
        Assert.Equal(5, share.Dots.Count);
        Assert.False(Dot(share, "蒸汽机").IsTicked);
    }

    [AvaloniaFact]
    public async Task Share_on_a_dot_of_the_Mindweave_page_opens_it_too()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        network.Select(network.Nodes.Single(n => n.ShortTitle == "蒸汽机").Id);
        window.UpdateLayout();
        var button = Find<Button>(window, "ShareSelectedButton");
        Assert.True(button.IsEffectivelyVisible);

        button.Command!.Execute(null);
        await app.Root.Share.Loading;

        Assert.Equal(["蒸汽机"], Ticked(app.Root.Share));
        Assert.Equal("蒸汽机", app.Root.Share.Title);
    }

    [AvaloniaFact]
    public async Task A_conversation_without_a_title_is_offered_as_untitled_never_by_its_quote_or_question()
    {
        await using var app = DesktopHarness.Create();
        app.Client.Titles.FailNext(GenerationErrorKind.Unauthorized, 20);
        app.ShowWindow(1100, 720);
        var page = await app.StartWithConversationAsync(waitForTitle: false);

        var share = await OpenAsync(app, page.Id);

        Assert.Equal(UiText.UntitledConversation, Assert.Single(share.Dots).Name);
        Assert.Equal(UiText.UntitledConversation, share.Title);
    }

    [AvaloniaFact]
    public async Task Unticking_a_conversation_takes_its_branches_out_of_the_ticks_the_counts_and_the_dot_sizes()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app, "root");
        var root = Dot(share, "火车起源");
        var before = root.Radius;
        Assert.Contains(UiText.ShareCounts(1, 4, 4), share.Contents);

        Dot(share, "火箭号速度").Parent!.IsTicked = false;

        Assert.Equal(new HashSet<string> { "火车起源", "铁轨" }, Ticked(share).ToHashSet());
        Assert.Contains(UiText.ShareCounts(1, 2, 2), share.Contents);
        Assert.True(root.Radius < before);

        // Ticking the leaf brings the conversations above it back.
        Dot(share, "火箭号速度").IsTicked = true;
        Assert.Equal(4, share.Dots.Count(d => d.IsTicked));
        Assert.Equal(before, root.Radius);
    }

    [AvaloniaFact]
    public async Task With_nothing_ticked_or_no_title_it_cannot_be_saved()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app, "root");
        Assert.True(share.SaveCommand.CanExecute(null));

        share.Title = "  ";
        Assert.False(share.SaveCommand.CanExecute(null));
        share.Title = "火车";
        Dot(share, "火车起源").IsTicked = false;
        Assert.False(share.SaveCommand.CanExecute(null));
        Dot(share, "蒸汽机").IsTicked = true;
        Assert.True(share.SaveCommand.CanExecute(null));
    }

    /// <summary>The dialog brings the exploration into sight once laid out; by then nothing of it may be ticked.</summary>
    [AvaloniaFact]
    public async Task Unticking_everything_right_away_leaves_the_dialog_as_it_is()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app, "root");

        Dot(share, "火车起源").IsTicked = false;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        window.UpdateLayout();

        Assert.Empty(Ticked(share));
        Assert.True(share.IsOpen);
    }

    [AvaloniaFact]
    public async Task Saving_suggests_the_title_and_the_date_writes_the_file_and_says_so()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app, "root");
        share.Title = "火车是怎么来的";
        Dot(share, "铁轨").IsTicked = false;

        // Cancelled: nothing is written or said.
        await share.SaveCommand.ExecuteAsync(null);
        Assert.Equal(["火车是怎么来的 " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".html"], app.Shell.ShareRequests);
        Assert.Null(share.Notice);
        Assert.True(share.IsOpen);

        app.Shell.NextShare = _ => new ExportDestination("share.html", Target, null);
        await share.SaveCommand.ExecuteAsync(null);

        Assert.Equal(UiText.ShareSaved("share.html"), share.Notice);
        Assert.False(share.NoticeIsError);
        var file = await File.ReadAllTextAsync(Target);
        Assert.Contains("火车是怎么来的", file, StringComparison.Ordinal);
        Assert.Contains("火箭号速度", file, StringComparison.Ordinal);
        Assert.DoesNotContain("铁轨", file, StringComparison.Ordinal);
        Assert.DoesNotContain("蒸汽机", file, StringComparison.Ordinal);
        Assert.DoesNotContain("谁造了第一台", file, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_failed_write_is_said_in_the_dialog()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app, "root");
        app.Shell.NextShare = _ => new ExportDestination("x.html", Path.Combine(_dir, "none", "x.html"), null);

        await share.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith(UiText.ExportFailed(ExportFailure.Failed, string.Empty)[..6], share.Notice, StringComparison.Ordinal);
        Assert.True(share.NoticeIsError);
        Assert.True(share.IsOpen);
        Assert.True(share.SaveCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task A_ticked_conversation_deleted_meanwhile_is_left_out_and_the_user_is_told()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app, "root");
        var sibling = Dot(share, "铁轨").Id;
        app.Shell.NextShare = _ =>
        {
            DeleteAsync(app, sibling).GetAwaiter().GetResult();
            return new ExportDestination("share.html", Target, null);
        };

        await share.SaveCommand.ExecuteAsync(null);

        Assert.Contains(UiText.ShareLeftOut(["铁轨"]), share.Notice, StringComparison.Ordinal);
        Assert.Contains(UiText.ShareSaved("share.html"), share.Notice, StringComparison.Ordinal);
        var file = await File.ReadAllTextAsync(Target);
        Assert.Contains("火车起源", file, StringComparison.Ordinal);
        Assert.DoesNotContain("铁轨", file, StringComparison.Ordinal);
        Assert.Equal(4, share.Dots.Count);
    }

    /// <summary>Nothing leaves that the user did not see listed: a name that changed meanwhile is shown first.</summary>
    [AvaloniaFact]
    public async Task What_changed_while_the_save_dialog_was_open_is_shown_before_it_leaves()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app, "root");
        var untitled = Dot(share, "火箭号速度").Parent!;
        app.Shell.NextShare = _ =>
        {
            app.Services.GetRequiredService<ConversationService>().RenameAsync(untitled.Id, "新名字", CancellationToken.None).GetAwaiter().GetResult();
            return new ExportDestination("share.html", Target, null);
        };

        await share.SaveCommand.ExecuteAsync(null);

        Assert.Equal(UiText.ShareChanged, share.Notice);
        Assert.True(share.NoticeIsError);
        Assert.False(File.Exists(Target));
        Assert.Contains("新名字", Ticked(share));

        await share.SaveCommand.ExecuteAsync(null);
        Assert.Contains("新名字", await File.ReadAllTextAsync(Target), StringComparison.Ordinal);
    }

    /// <summary>Acceptance 6.</summary>
    [AvaloniaFact]
    public async Task An_answer_still_being_generated_is_said_while_its_conversation_is_ticked()
    {
        await using var app = DesktopHarness.Create();
        app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var sibling = await IdAsync(app, "sibling");
        var held = app.Client.Hold("…");
        await conversations.AskAsync(sibling, "还有呢？", CancellationToken.None);
        var share = await OpenAsync(app, "root");

        Assert.Equal(UiText.ShareAnswering(["铁轨"]), share.AnsweringNote);
        Assert.Contains(UiText.ShareCounts(1, 4, 4), share.Contents);

        Dot(share, "铁轨").IsTicked = false;
        Assert.Null(share.AnsweringNote);
        Dot(share, "铁轨").IsTicked = true;

        held.Release.TrySetResult();
        await UiWait.UntilAsync(() => share.AnsweringNote is null);
        Assert.Contains(UiText.ShareCounts(1, 4, 5), share.Contents);
    }

    [AvaloniaFact]
    public async Task A_click_on_a_dot_or_its_title_ticks_it_and_escape_closes_the_dialog()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await SeedAsync(app);
        var share = await OpenAsync(app, "root");
        window.UpdateLayout();
        var dots = Find<ItemsControl>(window, "ShareDots").GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(5, dots.Count);
        var sibling = dots.Single(d => ReferenceEquals(d.DataContext, Dot(share, "铁轨")));
        Assert.Equal("铁轨", Avalonia.Automation.AutomationProperties.GetName(sibling));

        sibling.BringIntoView();
        window.UpdateLayout();
        var at = sibling.TranslatePoint(new Point(sibling.Bounds.Width - 12, 12), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.False(Dot(share, "铁轨").IsTicked);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(share.IsOpen);
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

    private static List<string> Ticked(ShareViewModel share) => [.. share.Dots.Where(d => d.IsTicked).Select(d => d.Name)];

    private static ShareDotViewModel Dot(ShareViewModel share, string name) => share.Dots.Single(d => d.Name == name);

    /// <summary>The seed's conversations have no titles of their own, so only the summarized ones have a name.</summary>
    private static async Task SeedAsync(DesktopHarness app)
    {
        app.Client.Titles.FailNext(GenerationErrorKind.Unauthorized, 100);
        await SummaryNetworkScenario.SeedSeenAsync(app, secondExploration: true);
    }

    private static async Task<ConversationId> IdAsync(DesktopHarness app, string which)
    {
        var listed = await app.Services.GetRequiredService<ConversationService>().ListAsync(app.Workbench.CurrentWorkspace!.Id, CancellationToken.None);
        var question = which switch { "root" => "火车是怎么发明的？", "leaf" => "它跑多快？", _ => "铁轨用什么做？" };
        return listed.Single(c => c.FirstQuestion == question).Conversation.Id;
    }

    private static async Task<ShareViewModel> OpenAsync(DesktopHarness app, string which) => await OpenAsync(app, await IdAsync(app, which));

    /// <summary>As the row's menu entry does.</summary>
    private static async Task<ShareViewModel> OpenAsync(DesktopHarness app, ConversationId id)
    {
        var list = app.ShellPage.Conversations;
        await UiWait.UntilAsync(() => list.Items.Any(i => i.Id == id));
        list.ShareRowCommand.Execute(list.Items.Single(i => i.Id == id));
        await app.Root.Share.Loading;
        return app.Root.Share;
    }

    private static async Task DeleteAsync(DesktopHarness app, ConversationId id)
    {
        var deletion = app.Services.GetRequiredService<IConversationDeletion>();
        var scope = await deletion.InspectAsync([id], CancellationToken.None).ConfigureAwait(false);
        var outcome = await deletion.DeleteAsync(scope, CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(ConversationDeleteStatus.Deleted, outcome.Status);
    }
}
