using System.Runtime.CompilerServices;
using Mindweave.Application.Languages;

namespace Mindweave.Architecture.Tests;

/// <summary>
/// The app starts in English, but these tests were written against the Chinese text: they run in Chinese unless a test
/// sets the language itself (docs/design/bilingual-ui.md §8). Tests that change it run in the Language collection.
/// </summary>
internal static class PinChinese
{
#pragma warning disable CA2255 // A test assembly's module initializer is the intended use.
    [ModuleInitializer]
    internal static void Pin() => CurrentLanguage.Set(UiLanguage.Chinese);
#pragma warning restore CA2255
}
