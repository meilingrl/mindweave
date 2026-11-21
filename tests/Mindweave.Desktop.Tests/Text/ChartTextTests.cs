using Mindweave.Application.Languages;

namespace Mindweave.Desktop.Tests.Text;

/// <summary>
/// The usage chart's and the calendar's cards say a bar's or a day's request count in one phrase: "3 requests" in
/// English, "3 次请求" in Chinese (an English count once ran into the column label: "3 requestsRequests").
/// </summary>
[Collection(LanguageTests.Name)]
public class ChartTextTests
{
    [Fact]
    public void The_request_count_reads_as_one_phrase_in_English()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.English);
            Assert.Equal("3 requests", UiText.ChartRequests(3));
            Assert.Equal("1 request", UiText.ChartRequests(1));
            Assert.Equal("1,200 requests", UiText.ChartRequests(1200));
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public void The_request_count_reads_as_before_in_Chinese()
    {
        CurrentLanguage.Set(UiLanguage.Chinese);
        Assert.Equal("3 次请求", UiText.ChartRequests(3));
    }
}
