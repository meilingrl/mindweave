using System.Text;
using Mindweave.Application.Export;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Tests.Export;

/// <summary>The exported file's exact bytes (§8.3) and the suggested file name.</summary>
public class SummaryMarkdownTests
{
    private static readonly ExportSource Source = new("火车是怎么发明的", 4, 2, SummaryOrigin.Edited, new DateTimeOffset(2026, 10, 28, 14, 3, 0, TimeSpan.FromHours(8)));

    [Fact]
    public void The_file_is_a_heading_a_source_quote_and_the_body_verbatim_as_lf_utf8_without_bom()
    {
        var body = "能量 \\(E=mc^2\\) 与\r\n\\[a^2+b^2=c^2\\]\r\n$$x$$ 和 $y$\n\n- 列表\\_项";

        var bytes = SummaryMarkdown.Encode(new SummaryExportDocument("质能关系", body, Source));

        var expected = "# 质能关系\n\n> 来源会话：火车是怎么发明的 · 覆盖到第 4 条消息 · 第 2 版（手改） · 创建于 2026-10-28 14:03 · 导出自 Mindweave\n\n"
            + "能量 \\(E=mc^2\\) 与\n\\[a^2+b^2=c^2\\]\n$$x$$ 和 $y$\n\n- 列表\\_项\n";
        Assert.Equal(new UTF8Encoding(false).GetBytes(expected), bytes);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Fact]
    public void Without_the_source_note_only_the_heading_and_body_remain()
    {
        var text = SummaryMarkdown.Format(new SummaryExportDocument("质能关系", "正文\n", null));

        Assert.Equal("# 质能关系\n\n正文\n", text);
    }

    [Fact]
    public void A_generated_version_says_so_and_line_breaks_in_names_do_not_break_the_heading_or_the_quote()
    {
        var source = Source with { ConversationName = "「引用的\n一段」", Origin = SummaryOrigin.Generated, VersionSeq = 1 };

        var text = SummaryMarkdown.Format(new SummaryExportDocument("两行\r\n标题", "正文", source));

        Assert.Equal("# 两行 标题\n\n> 来源会话：「引用的 一段」 · 覆盖到第 4 条消息 · 第 1 版（生成） · 创建于 2026-10-28 14:03 · 导出自 Mindweave\n\n正文\n", text);
    }

    [Theory]
    [InlineData("蒸汽机车", "蒸汽机车")]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "abcdefghij")]
    [InlineData("\u0001\t换行\n标题\u007f", "换行 标题")]
    [InlineData("CON", "CON_")]
    [InlineData("con", "con_")]
    [InlineData("Nul.txt", "Nul_.txt")]
    [InlineData("aux.tar.gz", "aux_.tar.gz")]
    [InlineData("com1", "com1_")]
    [InlineData("LPT9.md", "LPT9_")]
    [InlineData("AUX .txt", "AUX_ .txt")]
    [InlineData("笔记.md", "笔记")]
    [InlineData("笔记.MD", "笔记")]
    [InlineData(".md", "md")]
    [InlineData("COM0", "COM0")]
    [InlineData("CONSOLE", "CONSOLE")]
    [InlineData("标题.. . ", "标题")]
    [InlineData("  前后空格  ", "前后空格")]
    [InlineData("...隐藏", "隐藏")]
    [InlineData("", "summary-v3")]
    [InlineData("   ", "summary-v3")]
    [InlineData("???***", "summary-v3")]
    [InlineData("..", "summary-v3")]
    public void File_names_are_safe_on_windows(string title, string expected)
    {
        Assert.Equal(expected, ExportFileName.Suggest(title, 3));
    }

    [Fact]
    public void Long_titles_are_cut_at_whole_characters_and_not_left_ending_in_a_dot()
    {
        Assert.Equal(new string('长', ExportFileName.MaxLength), ExportFileName.Suggest(new string('长', 100), 1));
        Assert.Equal(new string('a', 79), ExportFileName.Suggest(new string('a', 79) + ". b", 1));

        // Four UTF-8 bytes each: the byte cap (file systems count bytes) cuts before the character cap.
        var emoji = string.Concat(Enumerable.Repeat("😀", 90));
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", ExportFileName.MaxBytes / 4)), ExportFileName.Suggest(emoji, 1));
    }
}
