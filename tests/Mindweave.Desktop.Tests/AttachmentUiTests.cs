using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Resources;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The composer's attachments in the real window (requirements §10.4.3): chips with name, type and size; a long paste
/// becoming a chip and coming back with 转为文字 or undo; 图片 greyed out with its reason; the viewer; a sent question with
/// its chips and the note under an answer that got only parts. Screenshots (MINDWEAVE_SCREENSHOT_DIR) in light and dark.
/// </summary>
public class AttachmentUiTests
{
    private static readonly string LongArticle = string.Concat(Enumerable.Range(0, 24).Select(i => $"第{i + 1}段：蒸汽机车的历史。" + new string('史', 400) + "\n\n"));

    public static TheoryData<string> Themes => new() { "Light", "Dark" };

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task The_composer_shows_chips_for_a_paste_files_an_image_and_a_refused_file(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenNewExplorationAsync(app, theme, images: true);
        var composer = Composer(app);
        var dir = Directory.CreateTempSubdirectory("mindweave-attach").FullName;

        Assert.True(composer.TryTakePaste(LongArticle, 0));
        await composer.AttachFilesAsync([Write(dir, "火车史笔记.md", "# 火车\n\n蒸汽机车的诞生。"), WriteBytes(dir, "铁路年表.docx", Docx("1825 年 斯托克顿—达灵顿铁路通车")), WriteBytes(dir, "资料.zip", [1, 2, 3])]);
        await composer.AttachImageAsync(Png(), "机车照片.png");
        await UiWait.UntilAsync(() => composer.Attachments.All(c => !c.IsWorking));
        composer.Text = "这几份材料里，蒸汽机车是哪年出现的？";
        window.UpdateLayout();

        Assert.Equal(["粘贴的文字", "火车史笔记.md", "铁路年表.docx", "资料.zip", "机车照片.png"], composer.Attachments.Select(c => c.Name));
        Assert.StartsWith("文字 · 约 1 万字", composer.Attachments[0].Detail, StringComparison.Ordinal);
        Assert.Equal("Word", composer.Attachments[2].TypeLabel);
        Assert.Equal("不支持 .zip 文件", composer.Attachments[3].Detail);
        Assert.True(composer.Attachments[3].IsFailed);
        Assert.NotNull(composer.Attachments[4].Thumbnail);
        Assert.False(composer.SendCommand.CanExecute(null)); // the refused file must go first

        Screenshots.Capture(window, $"attachments-01-composer-chips-{theme.ToLowerInvariant()}");
        composer.Attachments[3].RemoveCommand.Execute(null);
        Assert.True(composer.SendCommand.CanExecute(null));
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task A_long_paste_becomes_a_chip_and_comes_back_with_convert_or_undo(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenNewExplorationAsync(app, theme, images: false);
        var composer = Composer(app);
        var input = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "Input");
        input.Focus();
        window.KeyTextInput("请总结：");

        // A short paste goes in as text; a long one becomes 粘贴的文字 and the input keeps what was typed.
        await window.Clipboard!.SetTextAsync("一句话");
        input.Paste();
        await UiWait.UntilAsync(() => composer.Text == "请总结：一句话");
        await window.Clipboard!.SetTextAsync(LongArticle);
        input.Paste();
        await UiWait.UntilAsync(() => composer.Attachments.Count == 1 && composer.Attachments[0].IsReady);
        Assert.Equal("请总结：一句话", composer.Text);
        window.UpdateLayout();
        Screenshots.Capture(window, $"attachments-02-long-paste-{theme.ToLowerInvariant()}");

        // ⌘Z / Ctrl+Z right after: the text comes back where it was pasted.
        window.KeyPress(Key.Z, RawInputModifiers.Meta, PhysicalKey.Z, null);
        await UiWait.UntilAsync(() => composer.Attachments.Count == 0);
        Assert.Equal("请总结：一句话" + LongArticle, composer.Text);
        window.UpdateLayout();
        Screenshots.Capture(window, $"attachments-03-undo-{theme.ToLowerInvariant()}");

        // 转为文字 on the chip does the same.
        composer.Text = "再来：";
        Assert.True(composer.TryTakePaste(LongArticle, 3));
        await UiWait.UntilAsync(() => composer.Attachments.Count == 1 && composer.Attachments[0].CanConvert);
        composer.Attachments[0].ConvertToTextCommand.Execute(null);
        Assert.Equal("再来：" + LongArticle, composer.Text);
        Assert.Empty(composer.Attachments);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task Without_image_input_the_image_option_is_greyed_out_with_its_reason_and_a_pasted_image_is_refused(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenNewExplorationAsync(app, theme, images: false);
        var composer = Composer(app);

        Assert.False(composer.CanAttachImages);
        Assert.Equal(UiText.ImagesUnavailable, composer.ImageUnavailableReason);
        await composer.AttachImageAsync(Png(), null);
        Assert.Empty(composer.Attachments);
        Assert.Equal(UiText.PastedImageRefused, composer.AttachmentNotice);
        window.UpdateLayout();
        Screenshots.Capture(window, $"attachments-04a-pasted-image-refused-{theme.ToLowerInvariant()}");

        var plus = Find<Button>(window, "AttachButton");
        plus.Flyout!.ShowAt(plus);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var image = window.GetVisualDescendants().OfType<MenuItem>().Concat(TopLevel.GetTopLevel(plus)!.GetVisualDescendants().OfType<MenuItem>()).FirstOrDefault(m => m.Name == "AttachImageItem");
        Screenshots.Capture(window, $"attachments-04b-no-image-input-menu-{theme.ToLowerInvariant()}");
        if (image is not null)
        {
            Assert.False(image.IsEffectivelyEnabled);
        }

        plus.Flyout.Hide();
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task The_viewer_shows_the_extracted_text_to_select_and_copy(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenNewExplorationAsync(app, theme, images: false);
        var composer = Composer(app);
        var dir = Directory.CreateTempSubdirectory("mindweave-attach").FullName;
        await composer.AttachFilesAsync([WriteBytes(dir, "铁路年表.docx", Docx("1804 年 特里维西克的蒸汽机车\n1825 年 斯托克顿—达灵顿铁路通车\n1829 年 “火箭号”在雨山试验中胜出"))]);

        composer.Attachments[0].OpenCommand.Execute(null);
        await UiWait.UntilAsync(() => composer.Viewer is { IsOpen: true, HasText: true });
        window.UpdateLayout();

        Assert.Equal("铁路年表.docx", composer.Viewer.Title);
        var title = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Single(t => t.Name == "ViewerTitle");
        Assert.Equal("铁路年表.docx", title.Text);
        Assert.Contains("1825 年 斯托克顿—达灵顿铁路通车", composer.Viewer.Text, StringComparison.Ordinal);
        Assert.True(window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "ViewerText").IsEffectivelyVisible);
        Screenshots.Capture(window, $"attachments-05-viewer-{theme.ToLowerInvariant()}");

        composer.Viewer.CloseCommand.Execute(null);
        Assert.False(composer.Viewer.IsOpen);
        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task A_sent_question_shows_its_attachments_and_the_answer_says_only_parts_went_out(string theme)
    {
        await using var app = DesktopHarness.Create();
        var window = await OpenNewExplorationAsync(app, theme, images: false, contextWindow: 12_000);
        var composer = Composer(app);
        var dir = Directory.CreateTempSubdirectory("mindweave-attach").FullName;
        app.Client.Reply("根据附上的材料，第一台在轨道上行驶的蒸汽机车出现在 **1804 年**；附件只发送了与问题相关的部分，详见下方说明。");
        Assert.True(composer.TryTakePaste(LongArticle + LongArticle, 0));
        await composer.AttachFilesAsync([Write(dir, "火车史笔记.md", "# 火车\n\n1804 年，特里维西克的机车。")]);
        await UiWait.UntilAsync(() => composer.Attachments.All(c => c.IsReady));
        composer.Text = "第一台蒸汽机车是哪年出现的？";
        await composer.SendCommand.ExecuteAsync(null);

        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is ConversationViewModel { Messages.Count: 2 } p && p.Messages[1].State == MessageState.Complete && p.Messages[1].HasAttachmentNote, timeoutMs: 10_000);
        var page = (ConversationViewModel)app.Workbench.CurrentPage!;
        Assert.Equal(["粘贴的文字", "火车史笔记.md"], page.Messages[0].Attachments.Select(a => a.Name));
        Assert.Equal("第一台蒸汽机车是哪年出现的？", page.Messages[0].Body);
        Assert.Equal(UiText.AttachmentReducedNote, page.Messages[1].AttachmentNote);
        page.Messages[1].ToggleAttachmentNoteCommand.Execute(null);
        Assert.Contains("《粘贴的文字》：摘要，以及全文", page.Messages[1].AttachmentNoteDetails, StringComparison.Ordinal);
        Assert.Empty(page.Composer.Attachments);
        window.UpdateLayout();
        await Task.Delay(100);
        window.UpdateLayout();
        Screenshots.Capture(window, $"attachments-06-sent-question-{theme.ToLowerInvariant()}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_save_shows_on_the_chip_with_retry_and_the_typed_text_stays()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // A read-only folder is a POSIX permission here; the storage failure itself is covered on every platform.
        }

        await using var app = DesktopHarness.Create();
        await OpenNewExplorationAsync(app, "Light", images: false);
        var composer = Composer(app);
        composer.Text = "我写的问题";
        var folder = app.Paths.ResourceDirectory;
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.True(composer.TryTakePaste(LongArticle, 0));
            await UiWait.UntilAsync(() => composer.Attachments is [{ IsFailed: true }]);
            Assert.Equal((UiText.AttachmentSaveFailed, true), (composer.Attachments[0].Problem, composer.Attachments[0].CanRetry));
            Assert.Equal("我写的问题", composer.Text);
            Assert.False(composer.SendCommand.CanExecute(null));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Removing it leaves the question to be sent without it (once storage is back).
        composer.Attachments[0].RemoveCommand.Execute(null);
        Assert.Empty(composer.Attachments);
    }

    [AvaloniaFact]
    public async Task Draft_attachments_come_back_after_a_restart()
    {
        await using var app = DesktopHarness.Create();
        await OpenNewExplorationAsync(app, "Light", images: false);
        Assert.True(Composer(app).TryTakePaste(LongArticle, 0));
        await UiWait.UntilAsync(() => Composer(app).Attachments is [{ IsReady: true }]);

        await app.RestartAsync();
        app.ShellPage.Conversations.NewCommand.Execute(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is NewConversationViewModel { Composer.Attachments.Count: 1 });
        Assert.True(Composer(app).SendCommand.CanExecute(null));
    }

    private static Composer Composer(DesktopHarness app) => ((NewConversationViewModel)app.Workbench.CurrentPage!).Composer;

    private static async Task<MainWindow> OpenNewExplorationAsync(DesktopHarness app, string theme, bool images, int? contextWindow = null)
    {
        var window = app.ShowWindow(1000, 700, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
        await app.StartWithWorkspaceAsync("火车历史");
        var capabilities = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ImageInput = images };
        await app.Services.GetRequiredService<ModelSettingsService>().SaveAsync(
            new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", contextWindow, capabilities), CancellationToken.None);
        app.ShellPage.Conversations.NewCommand.Execute(null);
        await UiWait.UntilAsync(() => app.Workbench.CurrentPage is NewConversationViewModel { Composer.CanAttach: true } p && p.Composer.CanAttachImages == images);
        window.UpdateLayout();
        return window;
    }

    private static string Write(string dir, string name, string text) => WriteBytes(dir, name, Encoding.UTF8.GetBytes(text));

    private static string WriteBytes(string dir, string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A minimal DOCX: one paragraph per line.</summary>
    private static byte[] Docx(string text)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            var paragraphs = string.Concat(text.Split('\n').Select(l => $"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(l)}</w:t></w:r></w:p>"));
            writer.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{paragraphs}</w:body></w:document>");
        }

        return stream.ToArray();
    }

    /// <summary>A small picture: a sky, a hill and a sun.</summary>
    private static byte[] Png()
    {
        using var bitmap = new SkiaSharp.SKBitmap(240, 160);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(new SkiaSharp.SKColor(0x9C, 0xC9, 0xF0));
            using var hill = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0x5E, 0x9E, 0x6B), IsAntialias = true };
            canvas.DrawOval(new SkiaSharp.SKRect(-40, 100, 280, 260), hill);
            using var sun = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xF6, 0xC2, 0x4A), IsAntialias = true };
            canvas.DrawCircle(180, 45, 22, sun);
        }

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
