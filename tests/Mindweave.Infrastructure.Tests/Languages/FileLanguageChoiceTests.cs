using Mindweave.Application.Languages;
using Mindweave.Infrastructure.Languages;

namespace Mindweave.Infrastructure.Tests.Languages;

/// <summary>The interface language is kept in the file <c>language</c> in the data folder (docs/design/bilingual-ui.md §7).</summary>
public sealed class FileLanguageChoiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mw-language-" + Guid.NewGuid().ToString("N"));

    public FileLanguageChoiceTests() => Directory.CreateDirectory(_folder);

    private string FilePath => Path.Combine(_folder, "language");

    [Fact]
    public async Task Missing_file_means_English()
    {
        var choice = new FileLanguageChoice(_folder);

        Assert.Equal(UiLanguage.English, choice.Read());
        Assert.Equal(UiLanguage.English, await choice.ReadAsync());
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_yet_means_English()
    {
        var choice = new FileLanguageChoice(Path.Combine(_folder, "not-yet"));

        Assert.Equal(UiLanguage.English, await choice.ReadAsync());
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("")]
    [InlineData("zh")]
    [InlineData("\0\u0001ÿ")]
    public async Task Garbage_means_English(string content)
    {
        await File.WriteAllTextAsync(FilePath, content, TestContext.Current.CancellationToken);

        Assert.Equal(UiLanguage.English, await new FileLanguageChoice(_folder).ReadAsync());
    }

    [Fact]
    public async Task Surrounding_whitespace_and_a_newline_are_allowed()
    {
        await File.WriteAllTextAsync(FilePath, " zh-Hans\n", TestContext.Current.CancellationToken);

        Assert.Equal(UiLanguage.Chinese, new FileLanguageChoice(_folder).Read());
    }

    [Fact]
    public async Task Folder_in_its_place_means_English_and_write_returns_false()
    {
        Directory.CreateDirectory(FilePath);
        var choice = new FileLanguageChoice(_folder);

        Assert.Equal(UiLanguage.English, choice.Read());
        Assert.False(await choice.WriteAsync(UiLanguage.Chinese));
        Assert.Equal(UiLanguage.English, await choice.ReadAsync());
    }

    [Fact]
    public async Task Round_trip()
    {
        var choice = new FileLanguageChoice(_folder);

        Assert.True(await choice.WriteAsync(UiLanguage.Chinese));
        Assert.Equal(UiLanguage.Chinese, await new FileLanguageChoice(_folder).ReadAsync());
        Assert.Equal("zh-Hans", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken));

        Assert.True(await choice.WriteAsync(UiLanguage.English));
        Assert.Equal(UiLanguage.English, choice.Read());
        Assert.Equal("en", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_creates_the_data_folder()
    {
        var folder = Path.Combine(_folder, "fresh");

        Assert.True(await new FileLanguageChoice(folder).WriteAsync(UiLanguage.Chinese));
        Assert.Equal(UiLanguage.Chinese, new FileLanguageChoice(folder).Read());
    }

    [Fact]
    public async Task Write_is_atomic()
    {
        await File.WriteAllTextAsync(FilePath, "en", TestContext.Current.CancellationToken);
        var choice = new FileLanguageChoice(_folder);

        Assert.True(await choice.WriteAsync(UiLanguage.Chinese));

        // Written to a temporary file in the same folder, then moved over the old one: no temporary file is left behind.
        Assert.Equal(["language"], Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName));
        Assert.Equal(UiLanguage.Chinese, choice.Read());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
