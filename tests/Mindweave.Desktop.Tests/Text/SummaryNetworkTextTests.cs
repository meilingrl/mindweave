using Mindweave.Application.Languages;
using Mindweave.Desktop.Text;

namespace Mindweave.Desktop.Tests.Text;

/// <summary>
/// The network's dates under English: invariant English month abbreviations whatever the system's culture, the
/// year only when it is not this year. The Chinese forms are in <see cref="SummaryNetworkDotCanvasTests"/>.
/// </summary>
[Collection(LanguageTests.Name)]
public class SummaryNetworkTextTests
{
    [Fact]
    public void Day_labels_and_hover_labels_are_written_in_english_under_english()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.English);

            Assert.Equal("Nov 20", UiText.DayLabel(new DateOnly(2026, 11, 20), today: new DateOnly(2026, 11, 21)));
            Assert.Equal("Nov 20, 2025", UiText.DayLabel(new DateOnly(2025, 11, 20), today: new DateOnly(2026, 1, 2)));
            var local = new DateTime(2026, 11, 20, 21, 38, 0, DateTimeKind.Unspecified);
            var at = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
            Assert.Equal("蒸汽机 · Nov 20 21:38", UiText.DotHoverLabel("蒸汽机", at));
            Assert.Equal("Mindweave", UiText.SummaryNetworkTitle);
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public void The_started_line_shows_the_year_only_when_it_is_not_this_year_and_the_legend_says_dotted()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.English);
            static DateTimeOffset Local(int year, int month, int day) => new(new DateTime(year, month, day, 1, 7, 0, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day, 1, 7, 0)));
            var today = new DateOnly(2026, 11, 22);

            Assert.Equal("Started Nov 22 01:07", UiText.ConversationStarted(Local(2026, 11, 22), today));
            Assert.Equal("Started Nov 22, 2025 01:07", UiText.ConversationStarted(Local(2025, 11, 22), today));
            Assert.Equal("Dotted arrow: order in time", UiText.LegendTime);
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public void The_question_toggle_reads_unlike_the_toolbars_show_all_in_english()
    {
        // Final review M9: two controls on one page must not read alike, for a screen reader above all.
        try
        {
            CurrentLanguage.Set(UiLanguage.English);

            Assert.Equal("Show the whole question", UiText.ShowWholeQuestion);
            Assert.Equal("Show less", UiText.HideWholeQuestion);
            Assert.NotEqual(UiText.ShowAll, UiText.ShowWholeQuestion);
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public void The_started_line_shows_the_year_only_when_it_is_not_this_year__in_chinese()
    {
        CurrentLanguage.Set(UiLanguage.Chinese);
        static DateTimeOffset Local(int year, int month, int day) => new(new DateTime(year, month, day, 1, 7, 0, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day, 1, 7, 0)));
        var today = new DateOnly(2026, 11, 22);

        Assert.Equal("开始于 11月22日 01:07", UiText.ConversationStarted(Local(2026, 11, 22), today));
        Assert.Equal("开始于 2025年11月22日 01:07", UiText.ConversationStarted(Local(2025, 11, 22), today));
        Assert.Equal("虚线箭头：时间先后", UiText.LegendTime);
    }
}
