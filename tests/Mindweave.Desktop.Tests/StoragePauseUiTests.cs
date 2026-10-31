using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Requirements §11 reliability row "core content fails to save", through the real window and a real full disk on the
/// application's own writer (no store wrapper): the page says storage is paused, asking, retrying, branching and
/// summarizing are disabled with the reason, everything shown stays readable and selectable, and it all comes back
/// by itself once the failed answer is written.
/// </summary>
public class StoragePauseUiTests
{
    private const string FirstAnswer = "蒸汽机车由斯蒂芬森改良，铁路随后在英国普及。\n\n第二段说明后来的发展。";

    [AvaloniaFact]
    public async Task A_failed_answer_write_pauses_the_page_keeps_content_readable_and_lifts_when_storage_recovers()
    {
        await using var app = DesktopHarness.Create();
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        await app.StartWithWorkspaceAsync("火车历史");
        await app.ConfigureModelAsync();
        var page = await StartConversationAsync(app, window);

        // A second answer is streaming when the disk fills up; its long final text cannot be written.
        var held = app.Client.Hold("第二个回答");
        page.Composer.Text = "后来呢？";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.Messages.Count == 4 && page.Messages[3].Body == "第二个回答");
        var database = app.Services.GetRequiredService<SqliteDatabase>();
        await FillDiskAsync(database);
        var tail = new string('a', 70_000);
        held.Finish(tail);

        await UiWait.UntilAsync(() => page.IsStorageBlocked && page.Messages[3] is { State: MessageState.Complete, CanRetry: true }, timeoutMs: 10_000);
        window.UpdateLayout();
        var view = CurrentView(window);

        // The reason is shown; the entries that need storage are disabled.
        var notice = Find<Avalonia.Controls.TextBlock>(view, "StorageNotice");
        Assert.True(notice.IsEffectivelyVisible);
        Assert.Equal(UiText.StoragePaused, notice.Text);
        page.Composer.Text = "暂停时写下的问题";
        window.UpdateLayout();
        Assert.False(Find<Button>(view, "SendButton").IsEffectivelyEnabled);
        Assert.Equal(UiText.SendRejected(SendRejection.StorageUnavailable), Find<Avalonia.Controls.TextBlock>(view, "Notice").Text);

        // Said once (S7): the page's notice explains the pause; the composer does not repeat it.
        Assert.False(Find<Avalonia.Controls.Border>(view, "NoticePanel").IsEffectivelyVisible);
        Assert.Single(view.GetVisualDescendants().OfType<Avalonia.Controls.Border>(), b => b.Classes.Contains("notice") && b.IsEffectivelyVisible);
        Assert.False(Find<Button>(view, "SummarizeButton").IsEffectivelyEnabled);
        var retry = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RetryButton" && b.IsVisible);
        Assert.False(retry.IsEffectivelyEnabled);

        // What arrived stays readable: the stored answers and the unsaved one, from memory.
        Assert.Equal("第二个回答" + tail, page.Messages[3].Body);
        Assert.Equal(MessageState.Complete, page.Messages[3].State);

        // Text can still be selected (to copy); the branch entry appears for it, disabled, with the reason.
        // The unsaved answer's long text is laid out by the live renderer in the background; positions measured before
        // that are not trustworthy (the page would follow the growing text to the end under the pointer).
        await UiWait.UntilAsync(() => view.GetVisualDescendants().OfType<AnswerView>().All(a => a.IsLayoutCurrent), timeoutMs: 10_000);
        window.UpdateLayout();
        Find<ScrollViewer>(view, "Scroller").Offset = default;
        window.UpdateLayout();
        var first = view.GetVisualDescendants().OfType<AnswerView>().First(a => a.DataContext is MessageItemViewModel { IsAnswer: true, CanBranch: true });
        Select(window, first, "铁路随后");
        var branch = Find<Button>(first, "BranchButton");
        Assert.True(branch.IsVisible);
        Assert.False(branch.IsEffectivelyEnabled);
        Assert.Equal(UiText.StoragePausedReason, ToolTip.GetTip(branch));
        Assert.Equal("暂停时写下的问题", page.Composer.Text);

        // Space returns: the kept answer is written, and everything comes back without a restart.
        await FreeDiskAsync(database);
        await UiWait.UntilAsync(() => !page.IsStorageBlocked, timeoutMs: 15_000);
        window.UpdateLayout();
        Assert.False(notice.IsEffectivelyVisible);
        Assert.True(Find<Button>(view, "SendButton").IsEffectivelyEnabled);
        Assert.True(Find<Button>(view, "SummarizeButton").IsEffectivelyEnabled);
        Assert.True(branch.IsEffectivelyEnabled);
        Assert.Null(ToolTip.GetTip(branch));
        Assert.Null(page.Composer.Notice);
        var stored = (await app.Services.GetRequiredService<ConversationService>().LoadAsync(page.Id, CancellationToken.None))!.Messages[3];
        Assert.Equal(MessageState.Complete, stored.State);
        Assert.Equal("第二个回答" + tail, stored.Text);

        await page.Composer.SendCommand.ExecuteAsync(null);
        Assert.Null(page.Composer.Rejection);
        await UiWait.UntilAsync(() => page.Messages.Count == 6 && page.Messages[5].State == MessageState.Complete);
        window.Close();
    }

    private static async Task<ConversationViewModel> StartConversationAsync(DesktopHarness app, MainWindow window)
    {
        app.Client.Reply(FirstAnswer);
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "火车是怎么发明的？";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        await UiWait.UntilAsync(() => page.Title == Mindweave.Testing.FakeChatClient.DefaultTitle);
        await CurrentView(window).Restored;
        window.UpdateLayout();
        return page;
    }

    /// <summary>A real full disk on the writer connection: the file may not grow by a single page from now on.</summary>
    internal static Task FillDiskAsync(SqliteDatabase database) =>
        database.WriteAsync(
            (c, t) =>
            {
                if (c.Scalar<long>(t, "PRAGMA freelist_count;") is var free and > 0)
                {
                    c.Execute(t, $"INSERT OR REPLACE INTO app_state (key, value) VALUES ('test-filler', hex(zeroblob({free * 4096 / 2})));");
                }

                var pages = c.Scalar<long>(t, "PRAGMA page_count;");
                return c.Scalar<long>(t, $"PRAGMA max_page_count = {pages};");
            },
            CancellationToken.None);

    internal static Task FreeDiskAsync(SqliteDatabase database) =>
        database.WriteAsync((c, t) => c.Scalar<long>(t, "PRAGMA max_page_count = 4294967294;"), CancellationToken.None);

    private static void Select(Window window, AnswerView answer, string quote)
    {
        var block = SourceAnchors.Blocks(answer.Renderer).First(b => b.LayoutText.Contains(quote, StringComparison.Ordinal));
        block.BringIntoView();
        window.UpdateLayout();
        var start = block.LayoutText.IndexOf(quote, StringComparison.Ordinal);
        var end = start + quote.Length;
        var from = block.GetTextRangeBoundsInControl(start, 1)[0];
        var to = block.GetTextRangeBoundsInControl(end, 1)[0];
        var down = block.TranslatePoint(new Point(from.X + 1, from.Center.Y), window)!.Value;
        var up = block.TranslatePoint(new Point(to.X + 1, to.Center.Y), window)!.Value;
        window.MouseDown(down, MouseButton.Left);
        window.MouseMove(up, RawInputModifiers.LeftMouseButton);
        window.MouseUp(up, MouseButton.Left);
        window.UpdateLayout();
        Assert.Equal(quote, block.LayoutText[Math.Min(block.SelectionStart, block.SelectionEnd)..Math.Max(block.SelectionStart, block.SelectionEnd)]);
    }

    private static ConversationView CurrentView(Window window) => window.GetVisualDescendants().OfType<ConversationView>().Single();

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
}
