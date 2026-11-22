using Mindweave.Core.Text;

namespace Mindweave.Core.Tests;

/// <summary>
/// Length limits for text a model writes, by the text's own script (review finding 2026-11-22): CJK text in characters
/// as before, other text in words with a backstop in characters.
/// </summary>
public class TextLimitTests
{
    private static readonly TextLimit Term = new(16, 6);

    [Theory]
    [InlineData("热力学效率", true)]
    [InlineData("一二三四五六七八九十一二三四五六", true)]
    [InlineData("一二三四五六七八九十一二三四五六七", false)]
    [InlineData("Thermodynamic efficiency", true)]
    [InlineData("Practical limits of early steam designs", true)]
    [InlineData("one two three four five six seven", false)]
    [InlineData("《Dune》 and the desert ecology of Arrakis", false)]
    public void Text_with_CJK_is_measured_in_characters_and_other_text_in_words(string text, bool fits) =>
        Assert.Equal(fits, Term.Fits(text));

    [Fact]
    public void Text_without_spaces_is_bounded_by_the_backstop()
    {
        var unspaced = new string('a', Term.Words * TextLimit.MaxWordLength + 1);

        Assert.False(Term.Fits(unspaced));
        Assert.Equal(Term.Words * TextLimit.MaxWordLength, Term.Cut(unspaced).Length);
    }

    [Theory]
    [InlineData("Steam engine", "Steam engine")]
    [InlineData("one  two three four five six seven eight", "one  two three four five six")]
    [InlineData("一二三四五六七八九十一二三四五六七八", "一二三四五六七八九十一二三四五六")]
    public void Cut_keeps_whole_words_or_whole_characters(string text, string kept) =>
        Assert.Equal(kept, Term.Cut(text));

    [Fact]
    public void Plus_widens_both_measures() =>
        Assert.Equal(new TextLimit(17, 7), Term.Plus(1));
}
