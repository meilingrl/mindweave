using System.Text;
using Mindweave.Application.Languages;

namespace Mindweave.Infrastructure.Languages;

/// <summary>
/// The file <c>language</c> in the channel's data folder, holding <c>en</c> or <c>zh-Hans</c> (requirements §10.5).
/// Missing, unreadable or unknown content means English; a write goes through a temporary file and a move.
/// </summary>
public sealed class FileLanguageChoice(string dataFolder) : ILanguageChoice
{
    public const string FileName = "language";

    private const string EnglishTag = "en";
    private const string ChineseTag = "zh-Hans";

    private string FilePath => Path.Combine(dataFolder, FileName);

    public UiLanguage Read()
    {
        try
        {
            return Parse(File.ReadAllText(FilePath, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UiLanguage.English;
        }
    }

    public async Task<UiLanguage> ReadAsync()
    {
        try
        {
            return Parse(await File.ReadAllTextAsync(FilePath, Encoding.UTF8).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UiLanguage.English;
        }
    }

    public async Task<bool> WriteAsync(UiLanguage language)
    {
        var temp = Path.Combine(dataFolder, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(dataFolder);
            await File.WriteAllTextAsync(temp, language == UiLanguage.Chinese ? ChineseTag : EnglishTag, new UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return false;
        }
    }

    private static UiLanguage Parse(string content) =>
        string.Equals(content.Trim(), ChineseTag, StringComparison.OrdinalIgnoreCase) ? UiLanguage.Chinese : UiLanguage.English;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
