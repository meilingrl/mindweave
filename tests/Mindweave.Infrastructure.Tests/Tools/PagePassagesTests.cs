using System.Globalization;
using System.Text;
using Mindweave.Application.Tools;
using Mindweave.Core.Context;
using Mindweave.Core.Resources;

namespace Mindweave.Infrastructure.Tests.Tools;

/// <summary>
/// A long page's opening and best-matching paragraphs (requirements §10.9, docs/design/reply-quality.md Q8): what is chosen,
/// in which order, with which marks, within the size; and when the page keeps its start instead.
/// </summary>
public class PagePassagesTests
{
    private const int Size = 2_000;

    private const string Header = "[1] 南溟航天研究院 2025 年度报告\n网址：https://example.org/report\n获取于：2026-11-28 10:00 UTC\n\n";

    private const string Needle = "青鸾-7 气象卫星于本年度完成在轨测试并交付使用。卫星由研究院第二总体部研制，设计寿命为 9 年。";

    private static string Filler(int n) => string.Create(
        CultureInfo.InvariantCulture,
        $"在第 {n} 次专题会议上，研究院回顾了上一阶段的进展，并确定了下一阶段的工作重点。会议认为，工作总体按计划推进，部分环节因设备交付推迟而有所延后。与会人员就经费安排、人员配置与进度管理提出了意见，形成了会议纪要，由相关部门分头落实。");

    /// <summary>A page of <paramref name="count"/> paragraphs of filler, with the given ones in place of some.</summary>
    private static string Page(int count, params (int At, string Text)[] special)
    {
        var text = new StringBuilder(Header);
        for (var n = 1; n <= count; n++)
        {
            text.Append(special.FirstOrDefault(s => s.At == n).Text ?? Filler(n)).Append("\n\n");
        }

        return text.ToString();
    }

    [Fact]
    public void A_long_page_gives_its_opening_then_the_paragraph_that_matches_with_marks_where_text_is_left_out()
    {
        var page = Page(40, (37, Needle));

        var excerpt = PagePassages.Select(page, ["南溟航天研究院的 2025 年度报告里，青鸾-7 卫星的设计寿命是几年？"], Size);

        Assert.NotNull(excerpt);
        Assert.True(TokenEstimator.Estimate(excerpt) <= Size, $"{TokenEstimator.Estimate(excerpt)} tokens");

        // The opening: the title, the address and the lead, about 300 tokens.
        Assert.StartsWith(Header.TrimEnd() + "\n\n" + Filler(1), excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain(Filler(3), excerpt, StringComparison.Ordinal);

        // Then the paragraph deep in the page, after a mark that names its part, and a mark for the rest.
        var parts = ResourceChunker.Split(page);
        var part = parts.ToList().FindIndex(c => c.End > page.IndexOf(Needle, StringComparison.Ordinal)) + 1;
        Assert.Contains(PagePassages.Gap(part, parts.Count) + "\n\n" + Needle, excerpt, StringComparison.Ordinal);
        Assert.EndsWith(PagePassages.Rest, excerpt, StringComparison.Ordinal);

        // Paragraphs that share only what every paragraph says (研究院, 会议) are not sent.
        Assert.DoesNotContain(Filler(20), excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_matching_paragraphs_come_in_page_order_and_the_best_are_kept_within_the_size()
    {
        const string Launch = "青鸾-7 卫星于三月发射，进入预定轨道。";
        var page = Page(40, (12, Needle), (30, Launch), (35, "青鸾-7 卫星的设计寿命为 9 年，比青鸾-6 的设计寿命长两年，设计上换用了新的电池。"));

        var excerpt = PagePassages.Select(page, ["青鸾-7 卫星的设计寿命"], Size)!;

        var needle = excerpt.IndexOf(Needle, StringComparison.Ordinal);
        var launch = excerpt.IndexOf(Launch, StringComparison.Ordinal);
        var longer = excerpt.IndexOf("比青鸾-6", StringComparison.Ordinal);
        Assert.True(needle > 0 && launch > needle && longer > launch, $"{needle} {launch} {longer}");
        Assert.True(TokenEstimator.Estimate(excerpt) <= Size);

        // The best match is taken first: with just enough room for it, it is the only one.
        var (room, small) = Enumerable.Range(300, 500).Select(r => (r, PagePassages.Select(page, ["青鸾-7 卫星的设计寿命"], r)))
            .First(e => e.Item2?.Contains("比青鸾-6", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(Needle, small, StringComparison.Ordinal);
        Assert.DoesNotContain(Launch, small, StringComparison.Ordinal);
        Assert.True(TokenEstimator.Estimate(small!) <= room);
    }

    [Fact]
    public void Nothing_matching_well_leaves_the_start_to_the_caller()
    {
        var page = Page(40, (37, Needle));

        // No term of the question is on the page; or only terms every paragraph has.
        Assert.Null(PagePassages.Select(page, ["灯塔岛的开放时间？"], Size));
        Assert.Null(PagePassages.Select(page, ["研究院的会议"], Size));
        Assert.Null(PagePassages.Select(page, [null, " "], Size));
    }

    [Fact]
    public void The_search_that_led_to_the_page_counts_as_much_as_the_question()
    {
        var page = Page(40, (37, Needle));

        var excerpt = PagePassages.Select(page, ["它能用多久？", "青鸾-7 设计寿命"], Size);

        Assert.Contains(Needle, excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Chinese_matches_by_two_character_pieces_and_words_match_whatever_their_case()
    {
        var chinese = Page(40, (25, "据介绍，这颗卫星的寿命设计为九年。"));
        Assert.Contains("寿命设计为九年", PagePassages.Select(chinese, ["设计寿命是几年"], Size), StringComparison.Ordinal);

        var english = new StringBuilder("[1] Annual report\nURL: https://example.org/report\n\n");
        for (var n = 1; n <= 40; n++)
        {
            english.Append(n == 33
                ? "The Qingluan-7 weather satellite entered service this year; its design life is nine years."
                : string.Create(CultureInfo.InvariantCulture, $"At meeting {n} the institute reviewed the progress of the last stage and set the priorities of the next one, noting delays in delivery and agreeing on budgets and staff."))
                .Append("\n\n");
        }

        var excerpt = PagePassages.Select(english.ToString(), ["What is the DESIGN LIFE of qingluan-7?"], Size);
        Assert.Contains("its design life is nine years", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Data_keeps_its_start()
    {
        var json = "[" + string.Join(",\n", Enumerable.Range(1, 400).Select(i => string.Create(CultureInfo.InvariantCulture, $"{{\"satellite\": \"青鸾-{i}\", \"life\": \"{i % 12} 年\"}}"))) + "]";
        var table = string.Join("\n", Enumerable.Range(1, 400).Select(i => string.Create(CultureInfo.InvariantCulture, $"| 青鸾-{i} | 设计寿命 | {i % 12} 年 |")));

        Assert.True(PagePassages.LooksLikeData(json));
        Assert.True(PagePassages.LooksLikeData(table));
        Assert.False(PagePassages.LooksLikeData(Page(40, (37, Needle))));
        Assert.Null(PagePassages.Select(json, ["青鸾-7 设计寿命"], Size));
        Assert.Null(PagePassages.Select(table, ["青鸾-7 设计寿命"], Size));
    }

    [Fact]
    public void A_page_that_fits_whole_is_not_cut()
    {
        Assert.Null(PagePassages.Select(Page(3, (2, Needle)), ["青鸾-7 设计寿命"], Size));
    }

    [Fact]
    public void A_page_on_one_line_is_ranked_in_pieces_and_stays_within_the_size()
    {
        // No line breaks at all: the text is cut at sentence ends into pieces of at most about 400 tokens.
        var line = string.Concat(Enumerable.Range(1, 120).Select(n => n == 90 ? Needle : Filler(n)));
        var page = "[1] 年度报告\n" + line;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var excerpt = PagePassages.Select(page, ["青鸾-7 的设计寿命"], Size)!;

        Assert.Contains("设计寿命为 9 年", excerpt, StringComparison.Ordinal);
        Assert.True(TokenEstimator.Estimate(excerpt) <= Size);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
    }

    [Fact]
    public void A_very_long_page_is_chosen_from_quickly()
    {
        var page = Page(4_000, (3_777, Needle));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var excerpt = PagePassages.Select(page, ["青鸾-7 的设计寿命是几年？"], Size)!;

        Assert.Contains(Needle, excerpt, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), watch.Elapsed.ToString());
    }
}
