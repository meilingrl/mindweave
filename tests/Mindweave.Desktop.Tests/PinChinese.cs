using System.Runtime.CompilerServices;
using Mindweave.Application.Languages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The app starts in English, but these tests were written against the Chinese text: they run in Chinese unless a test
/// sets the language itself (docs/design/bilingual-ui.md §8). Tests that change it run in the Language collection.
/// A gallery run (MINDWEAVE_SCREENSHOT_DIR, --filter Category=Gallery) renders in English with
/// MINDWEAVE_GALLERY_LANGUAGE=English; only gallery scenes may be run that way.
/// </summary>
internal static class PinChinese
{
#pragma warning disable CA2255 // A test assembly's module initializer is the intended use.
    [ModuleInitializer]
    internal static void Pin() => CurrentLanguage.Set(
        string.Equals(Environment.GetEnvironmentVariable("MINDWEAVE_GALLERY_LANGUAGE"), "English", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.English
            : UiLanguage.Chinese);
#pragma warning restore CA2255
}
