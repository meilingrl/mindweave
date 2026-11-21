using Mindweave.Application.Languages;
using Mindweave.Desktop.Text;

namespace Mindweave.Desktop.Tests.Text;

/// <summary>
/// The session network's dates under English: invariant English month abbreviations whatever the system's culture, the
/// year only when it is not this year. The Chinese forms are in <see cref="SessionNetworkCanvasTests"/>.
/// </summary>
[Collection(LanguageTests.Name)]
public class SessionNetworkTextTests
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
            Assert.Equal("Mindweave", UiText.SessionNetworkTitle);
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }
}
