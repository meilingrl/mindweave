using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Mindweave.Application.Ai;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;

namespace Mindweave.Desktop.Tests;

/// <summary>LaTeX in answers renders as formulas, and passages containing formulas can still be branched from.</summary>
public class MathRenderingTests
{
    private const string Answer = "质能方程 $E = mc^2$ 说明能量与质量等价。\n\n$$\\int_0^1 x^2\\,dx = \\frac{1}{3}$$\n\n之后的段落照常显示。";

    [AvaloniaFact]
    public async Task Inline_and_block_formulas_render_as_math_not_as_source()
    {
        await using var app = DesktopHarness.Create();
        var (window, page) = await OpenAnswerAsync(app);
        var answer = AnswerOf(window);

        var text = string.Concat(SourceAnchors.Blocks(answer.Renderer).Select(b => b.LayoutText));
        Assert.DoesNotContain("$", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\frac", text, StringComparison.Ordinal);
        Assert.Equal(2, answer.Renderer.GetVisualDescendants().Count(v => v.GetType().Name == "FormulaControl"));
        Assert.Contains("之后的段落照常显示。", text, StringComparison.Ordinal);
        ViewTests.Capture(window, "05-math");
        _ = page;
        window.Close();
    }

    /// <summary>
    /// Models often write LaTeX with backslash delimiters (seen in the S3 trial); plain Markdown would read <c>\[</c>
    /// as an escaped bracket and show the source.
    /// </summary>
    [AvaloniaFact]
    public async Task Backslash_delimited_formulas_render_as_math()
    {
        const string backslash = "OTDR 计算距离：\n\n\\[\n距离 = \\frac{光速 \\times 时间}{2 \\times 折射率}\n\\]\n\n其中 \\(n \\approx 1.47\\) 是折射率。";
        await using var app = DesktopHarness.Create();
        var (window, page) = await OpenAnswerAsync(app, backslash);
        var answer = AnswerOf(window);

        var text = string.Concat(SourceAnchors.Blocks(answer.Renderer).Select(b => b.LayoutText));
        Assert.DoesNotContain("\\frac", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\approx", text, StringComparison.Ordinal);
        Assert.Equal(2, answer.Renderer.GetVisualDescendants().Count(v => v.GetType().Name == "FormulaControl"));

        var block = SourceAnchors.Blocks(answer.Renderer).First(b => b.LayoutText.StartsWith("其中", StringComparison.Ordinal));
        block.SelectionStart = 0;
        block.SelectionEnd = block.LayoutText.Length;
        var selection = Assert.IsType<Mindweave.Core.Branches.BranchSelection>(answer.CaptureSelection());
        Assert.Equal("其中 \\(n \\approx 1.47\\) 是折射率。", selection.Quote);
        ViewTests.Capture(window, "05-math-backslash");
        _ = page;
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_passage_with_a_formula_is_quoted_with_its_latex_and_its_source_is_found_again()
    {
        await using var app = DesktopHarness.Create();
        var (window, page) = await OpenAnswerAsync(app);
        var answer = AnswerOf(window);
        var block = SourceAnchors.Blocks(answer.Renderer).First(b => b.LayoutText.StartsWith("质能方程", StringComparison.Ordinal));
        block.SelectionStart = 0;
        block.SelectionEnd = block.LayoutText.IndexOf("说明", StringComparison.Ordinal) + 2;

        var selection = Assert.IsType<Mindweave.Core.Branches.BranchSelection>(answer.CaptureSelection());
        Assert.Equal("质能方程 $E = mc^2$ 说明", selection.Quote);
        await page.CreateBranchAsync(selection);
        var child = await UiWaitForPageAsync(app, c => c.Id != page.Id);

        child.Composer.Text = "为什么？";
        await child.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => child.Messages.Count == 2 && child.Messages[1].CanBranch);
        var sent = app.Client.Requests.Last(r => r.Turns[0].Role != ChatRole.System);
        Assert.Contains("> 质能方程 $E = mc^2$ 说明", sent.Turns[^1].Content, StringComparison.Ordinal);

        child.ReturnToSourceCommand.Execute(null);
        var parent = await UiWaitForPageAsync(app, c => c.Id == page.Id);
        await UiWait.UntilAsync(() => window.GetVisualDescendants().OfType<ConversationView>().Single().Restored.IsCompleted);
        window.UpdateLayout();
        Assert.Null(parent.SourceNotice);
        var highlighted = window.GetVisualDescendants().OfType<MarkdownTextBlock>().Single(b => b.Highlights.TryGetValue("branch-source", out _));
        Assert.StartsWith("质能方程", highlighted.LayoutText, StringComparison.Ordinal);
        window.Close();
    }

    private static async Task<(MainWindow Window, ConversationViewModel Page)> OpenAnswerAsync(DesktopHarness app, string answer = Answer)
    {
        var window = new MainWindow { DataContext = app.Root, Width = 1000, Height = 700 };
        window.Show();
        await app.StartWithWorkspaceAsync("物理");
        await app.ConfigureModelAsync();
        app.Client.Reply(answer);
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "质能方程是什么？";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        await UiWait.UntilAsync(() => window.GetVisualDescendants().OfType<AnswerView>().Any(a => a.DataContext is MessageItemViewModel { CanBranch: true }));
        window.UpdateLayout();
        return (window, page);
    }

    private static AnswerView AnswerOf(Window window) =>
        window.GetVisualDescendants().OfType<AnswerView>().Last(a => a.DataContext is MessageItemViewModel { IsAnswer: true });

    private static async Task<ConversationViewModel> UiWaitForPageAsync(DesktopHarness app, Func<ConversationViewModel, bool> match)
    {
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel page && match(page));
        var page = (ConversationViewModel)app.Workbench.CurrentPage!;
        await page.Loaded;
        return page;
    }
}
