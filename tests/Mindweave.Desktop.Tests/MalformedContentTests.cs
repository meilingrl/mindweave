using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Languages;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views.Conversations;
using RaTeX;
using RaTeX.Avalonia;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// D2 §2.5 (dependencies-4): formulas and Markdown a model wrote wrongly never take the app down. A few hundred malformed
/// inputs go through the formula library and the renderer the app draws messages with; when drawing a message does throw,
/// it is shown as plain text with a note. Found 2026-11-27: none of the inputs throws or crashes in the native formula
/// library or in drawing, so the crash note of §2.5's third point is not built.
/// </summary>
[Collection(Text.LanguageTests.Name)] // The gallery switches the language.
public class MalformedContentTests
{
    private const int FormulasPerDocument = 30;
    private const int TextsPerTest = 10;

    public static TheoryData<int> FormulaBatches => Batches(MalformedContent.Formulas.Count, FormulasPerDocument);

    public static TheoryData<int> MarkdownBatches => Batches(MalformedContent.Markdown.Count, TextsPerTest);

    [Fact]
    public void Every_malformed_formula_is_refused_or_laid_out_by_the_native_library()
    {
        var refused = 0;
        var thrown = new List<string>();
        foreach (var formula in MalformedContent.Formulas)
        {
            foreach (var mode in new[] { RatexMode.Inline, RatexMode.Display })
            {
                try
                {
                    if (!Ratex.TryParseAndLayout(formula, new RatexOptions { Mode = mode }, out _, out _))
                    {
                        refused++;
                    }
                }
                catch (ArgumentException)
                {
                    thrown.Add(formula);
                }
            }
        }

        Assert.True(MalformedContent.Formulas.Count >= 300, "A few hundred formulas.");
        Assert.InRange(refused, 1, 2 * MalformedContent.Formulas.Count);

        // Only text that is not valid (a NUL, half a surrogate pair) is refused by throwing, before the native call; drawing
        // a message never hands a NUL over, as the Markdown parser reads it as U+FFFD (CommonMark).
        Assert.All(thrown, f => Assert.True(f.Contains('\0', StringComparison.Ordinal) || f.Any(char.IsSurrogate)));
        Assert.DoesNotContain('\0', AnswerMarkdown.Parse("Inline $a\0b$").Descendants<Markdig.Extensions.Mathematics.MathInline>().Single().Content.ToString());
    }

    /// <summary>Each formula inline, as a block and with backslash delimiters, in documents of a few dozen.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(FormulaBatches))]
    public async Task Malformed_formulas_are_drawn_without_an_exception_leaving_the_message(int batch)
    {
        var formulas = MalformedContent.Formulas.Skip(batch * FormulasPerDocument).Take(FormulasPerDocument);
        var text = string.Join("\n\n", formulas.Select(f => $"Inline ${f}$ text.\n\n$$\n{f}\n$$\n\n\\[{f}\\]\n\n- \\({f}\\)"));

        await DrawAsync([text]);
    }

    [AvaloniaTheory]
    [MemberData(nameof(MarkdownBatches))]
    public async Task Malformed_markdown_is_drawn_without_an_exception_leaving_the_message(int batch) =>
        await DrawAsync([.. MalformedContent.Markdown.Skip(batch * TextsPerTest).Take(TextsPerTest)]);

    [AvaloniaFact]
    public void A_message_whose_layout_throws_is_shown_as_plain_text_with_a_note_and_the_page_goes_on()
    {
        var renderer = new ThrowingRenderer();
        var guard = new RenderGuard(renderer);
        var neighbour = new TextBlock { Text = "next message" };
        var window = new Window { Content = new StackPanel { Children = { guard, neighbour } }, Width = 600, Height = 400 };
        window.Show();
        renderer.Throws = true;

        guard.Show("Broken $\\frac{$ formula", r => r.DocumentUpdate = new MarkdownDocumentUpdate.Full(AnswerMarkdown.Parse("Broken")));
        window.UpdateLayout();

        Assert.True(guard.IsPlain);
        Assert.Equal(UiText.MessageShownAsPlainText, Find<TextBlock>(guard, "PlainNote").Text);
        Assert.Equal("Broken $\\frac{$ formula", Find<SelectableTextBlock>(guard, "PlainBody").Text);
        Assert.True(Find<SelectableTextBlock>(guard, "PlainBody").IsEffectivelyVisible);
        Assert.True(neighbour.Bounds.Height > 0 && neighbour.Bounds.Y >= guard.Bounds.Bottom);
        window.Close();
    }

    [AvaloniaFact]
    public void A_document_that_cannot_be_built_is_shown_as_plain_text()
    {
        var guard = new RenderGuard(new MarkdownRenderer());
        var window = new Window { Content = guard, Width = 600, Height = 400 };
        window.Show();

        guard.Show("text", _ => throw new InvalidOperationException("the renderer refused"));
        window.UpdateLayout();

        Assert.True(guard.IsPlain);
        window.Close();
    }

    /// <summary>A streaming answer that cannot be drawn stays plain while it grows; a replaced text is tried in Markdown again.</summary>
    [AvaloniaFact]
    public void Plain_text_follows_a_growing_answer_and_a_new_text_is_drawn_again()
    {
        var renderer = new ThrowingRenderer { Throws = true };
        var guard = new RenderGuard(renderer);
        var window = new Window { Content = guard, Width = 600, Height = 400 };
        window.Show();
        guard.Show("$$\\begin{", r => r.DocumentUpdate = new MarkdownDocumentUpdate.Full(AnswerMarkdown.Parse("x")));
        window.UpdateLayout();
        Assert.True(guard.IsPlain);

        renderer.Throws = false;
        guard.Source = "$$\\begin{matrix} and more";
        window.UpdateLayout();
        Assert.True(guard.IsPlain);
        Assert.Equal("$$\\begin{matrix} and more", Find<SelectableTextBlock>(guard, "PlainBody").Text);

        guard.Show("A new answer.", r => r.DocumentUpdate = new MarkdownDocumentUpdate.Full(AnswerMarkdown.Parse("A new answer.")));
        window.UpdateLayout();
        Assert.False(guard.IsPlain, guard.LastFailure?.ToString());
        Assert.Same(renderer, guard.Child);
        window.Close();
    }

    /// <summary>
    /// A control inside the renderer laid out on its own later (a formula prepared in the background) throws past the guard to
    /// the UI guard, which finds the message whose layout was left unfinished: that message turns plain, no other does, and
    /// the shell shows no "did not complete" notice. An exception from elsewhere is left to the usual notice.
    /// </summary>
    [AvaloniaFact]
    public async Task A_late_failure_inside_a_message_turns_only_that_message_plain()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.Client.Reply("First answer with $x^2$.");
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        draft.Composer.Text = "一";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
        app.Client.Reply("Second answer.");
        page.Composer.Text = "二";
        await page.Composer.SendCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => page.Messages.Count == 4 && page.Messages[3].CanBranch);
        // Questions are drawn by the same view.
        await UiWait.UntilAsync(() => window.GetVisualDescendants().OfType<AnswerView>().Count(a => a.IsRendered && a.Markdown.Contains("answer", StringComparison.Ordinal)) == 2);
        window.UpdateLayout();
        using var guard = new UiExceptionGuard(Dispatcher.UIThread, () => app.ShellPage, NullLogger.Instance, watchRefreshes: false);
        var answers = window.GetVisualDescendants().OfType<AnswerView>().ToList();
        var broken = answers.Single(a => a.Markdown.StartsWith("First", StringComparison.Ordinal));
        var other = answers.Single(a => a.Markdown.StartsWith("Second", StringComparison.Ordinal));
        var brokenText = broken.Markdown;

        // Elsewhere: the usual notice, and no message changes.
        Dispatcher.UIThread.Post(() => throw new InvalidOperationException("boom"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(UiText.UnexpectedError, app.ShellPage.RestoreNotice);
        app.ShellPage.CloseRestoreNoticeCommand.Execute(null);
        Assert.False(broken.Guard.IsPlain);

        // From the renderer, with the first answer's layout left unfinished.
        var formula = broken.Renderer.GetVisualDescendants().OfType<Avalonia.Layout.Layoutable>().Last();
        formula.InvalidateMeasure();
        // Ahead of the layout pass, as if thrown inside it.
        Dispatcher.UIThread.Post(() => throw new InvalidOperationException("thrown in LiveMarkdown.Avalonia while measuring"), DispatcherPriority.Send);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.True(broken.Guard.IsPlain);
        Assert.False(other.Guard.IsPlain);
        Assert.Null(app.ShellPage.RestoreNotice);
        Assert.Equal(brokenText, Find<SelectableTextBlock>(broken.Guard, "PlainBody").Text);
        window.Close();
    }

    /// <summary>
    /// For looking at (MINDWEAVE_SCREENSHOT_DIR, <c>--filter FullyQualifiedName~MalformedContentTests.Gallery</c>):
    /// plain-1-answer in each theme and language, an answer that could not be drawn between two that could.
    /// </summary>
    [AvaloniaTheory]
    [Trait("Category", "Gallery")]
    [InlineData("Light", UiLanguage.English)]
    [InlineData("Dark", UiLanguage.English)]
    [InlineData("Light", UiLanguage.Chinese)]
    [InlineData("Dark", UiLanguage.Chinese)]
    public async Task Gallery(string theme, UiLanguage language)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR")), "Screenshots only.");
        var before = CurrentLanguage.Value;
        CurrentLanguage.Set(language);
        try
        {
            var english = language == UiLanguage.English;
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1100, 720, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
            await app.StartWithWorkspaceAsync(english ? "Physics" : "物理");
            await app.ConfigureModelAsync();
            var broken = english
                ? "The energy follows from\n\n$$\\int_0^1 \\frac{x^2 \\, dx = \\left( \\frac{1}{3}$$\n\nso the **work done** is one third of the unit."
                : "能量可以这样算：\n\n$$\\int_0^1 \\frac{x^2 \\, dx = \\left( \\frac{1}{3}$$\n\n所以**做的功**是单位的三分之一。";
            app.Client.Reply(broken);
            app.ShellPage.Conversations.NewCommand.Execute(null);
            var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
            draft.Composer.Text = english ? "How much work is done?" : "做了多少功？";
            await draft.Composer.SendCommand.ExecuteAsync(null);
            var page = Assert.IsType<ConversationViewModel>(app.Workbench.CurrentPage);
            await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].CanBranch);
            await UiWait.UntilAsync(() => window.GetVisualDescendants().OfType<AnswerView>().Any(a => a.IsRendered && a.Markdown == broken));
            var answer = window.GetVisualDescendants().OfType<AnswerView>().Single(a => a.Markdown == broken);

            // As when drawing it throws (none of the malformed inputs above does).
            answer.Guard.Show(broken, _ => throw new InvalidOperationException("simulated"));
            window.UpdateLayout();
            await Task.Delay(300); // The page settles.
            window.UpdateLayout();
            Screenshots.Capture(window, $"plain-1-answer-{theme.ToLowerInvariant()}-{(english ? "en" : "zh")}");
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(before);
        }
    }

    private static TheoryData<int> Batches(int count, int size)
    {
        var data = new TheoryData<int>();
        for (var i = 0; i * size < count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    /// <summary>
    /// Draws each text as a finished message is drawn, waits for its formulas to be prepared, lays it out and renders a frame.
    /// No exception may leave the message: the guard either draws it or shows it plainly.
    /// </summary>
    private static async Task DrawAsync(IReadOnlyList<string> texts)
    {
        MarkdownSetup.EnsureRegistered();
        var escaped = new List<Exception>();
        void Record(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            escaped.Add(e.Exception);
            e.Handled = true;
        }

        Dispatcher.UIThread.UnhandledException += Record;
        var guard = new RenderGuard(new MarkdownRenderer());
        var window = new Window { Content = new ScrollViewer { Content = guard }, Width = 700, Height = 500 };
        window.Show();
        try
        {
            foreach (var text in texts)
            {
                guard.Show(text, r => r.DocumentUpdate = new MarkdownDocumentUpdate.Full(AnswerMarkdown.Parse(text)));
                window.UpdateLayout();
                for (var tries = 0; tries < 40 && guard.GetVisualDescendants().OfType<FormulaControl>().Any(f => f.DisplayList is null && f.PreparationError is null); tries++)
                {
                    await Task.Delay(5);
                    Dispatcher.UIThread.RunJobs();
                }

                window.UpdateLayout();
                window.CaptureRenderedFrame()?.Dispose();
                Assert.True(guard.Bounds.Height > 0 || text.Trim().Length == 0);
            }
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= Record;
            window.Close();
        }

        Assert.Empty(escaped);
    }

    /// <summary>A renderer whose document cannot be laid out while <see cref="Throws"/>; with no document there is nothing to lay out.</summary>
    private sealed class ThrowingRenderer : MarkdownRenderer
    {
        public bool Throws { get; set; }

        protected override Size MeasureOverride(Size availableSize) =>
            Throws && DocumentUpdate is not null
                ? throw new InvalidOperationException("simulated: a formula the renderer cannot lay out")
                : base.MeasureOverride(availableSize);
    }
}
