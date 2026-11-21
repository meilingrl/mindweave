using Mindweave.Application.Languages;

namespace Mindweave.Infrastructure.Tests.Languages;

/// <summary>The interface language and text written in both languages (docs/design/bilingual-ui.md §4, requirements §10.5).</summary>
[Collection(LanguageTests.Name)]
public class BilingualTests
{
    [Fact]
    public void T_returns_the_current_language()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.English);
            Assert.Equal("Cancel", Bilingual.T("Cancel", "取消"));
            CurrentLanguage.Set(UiLanguage.Chinese);
            Assert.Equal("取消", Bilingual.T("Cancel", "取消"));
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public void Plural_picks_singular_for_one()
    {
        Assert.Equal("1 conversation", Bilingual.Plural(1, "conversation", "conversations"));
        Assert.Equal("3 conversations", Bilingual.Plural(3, "conversation", "conversations"));
        Assert.Equal("0 conversations", Bilingual.Plural(0, "conversation", "conversations"));
    }

    [Fact]
    public void BilingualText_follows_a_later_switch()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
            var text = new BilingualText("Enter the server's name.", "请填写服务器的名称。");
            Assert.Equal("请填写服务器的名称。", text.ToString());
            CurrentLanguage.Set(UiLanguage.English);
            Assert.Equal("Enter the server's name.", text.ToString());
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public void Culture_follows_the_language()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.English);
            Assert.Equal("en-US", Bilingual.Culture.Name);
            CurrentLanguage.Set(UiLanguage.Chinese);
            Assert.Equal("zh-CN", Bilingual.Culture.Name);
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }
}

/// <summary>Tests that change the process-wide language run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageTests
{
    public const string Name = "Language";
}
