using System.Text;
using Mindweave.Application.Share;

namespace Mindweave.Infrastructure.Tests.Share;

/// <summary>The suggested name of a shared file: the title and the date (docs/design/sharing.md S10).</summary>
public sealed class ShareFileNameTests
{
    private static readonly DateOnly Day = new(2026, 11, 25);

    [Theory]
    [InlineData("How trains came to be", "How trains came to be 2026-11-25")]
    [InlineData("期末复习：概率论", "期末复习：概率论 2026-11-25")]
    [InlineData("Why? a/b: \"c\"", "Why ab c 2026-11-25")]
    [InlineData("  .hidden. ", "hidden 2026-11-25")]
    [InlineData("CON", "CON 2026-11-25")]
    [InlineData("CON.1", "CON_.1 2026-11-25")]
    [InlineData("???", "2026-11-25")]
    public void It_is_the_title_made_safe_and_the_date(string title, string expected)
    {
        Assert.Equal(expected, ShareFileName.Suggest(title, Day));
    }

    [Fact]
    public void A_long_title_is_cut_and_the_date_stays()
    {
        var name = ShareFileName.Suggest(new string('火', 300), Day);

        Assert.EndsWith(" 2026-11-25", name, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(name + ".html") <= 255);
    }
}
