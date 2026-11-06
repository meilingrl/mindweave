using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Desktop.Views.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>Lookups in a shown window's visual tree, shared by the UI tests (import with <c>using static</c>).</summary>
internal static class UiTree
{
    /// <summary>
    /// The one control of type <typeparamref name="T"/> named <paramref name="name"/> under <paramref name="root"/>. A
    /// second match fails the lookup: a test acting on "the first one" can silently act on the wrong control.
    /// </summary>
    public static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// <summary>A named part of the shell (sidebar, navigation, splitter), looked up below the one <see cref="ShellView"/>.</summary>
    public static T Part<T>(Window window, string name)
        where T : Control =>
        Find<T>(window.GetVisualDescendants().OfType<ShellView>().Single(), name);

    /// <summary>The conversation page currently shown.</summary>
    public static ConversationView CurrentView(Window window) => window.GetVisualDescendants().OfType<ConversationView>().Single();

    /// <summary>The current conversation page's message scroller.</summary>
    public static ScrollViewer Scroller(Window window) => Find<ScrollViewer>(CurrentView(window), "Scroller");

    /// <summary>The answer views shown, in order (the message template also hosts a hidden one for each question).</summary>
    public static List<AnswerView> Answers(Window window) =>
        [.. window.GetVisualDescendants().OfType<AnswerView>().Where(a => a.DataContext is MessageItemViewModel { IsAnswer: true })];

    /// <summary>The knowledge network's canvas.</summary>
    public static NetworkCanvas CanvasOf(Window window) => window.GetVisualDescendants().OfType<NetworkCanvas>().Single();

    /// <summary>The colour of the application's brush resource <paramref name="key"/> in <paramref name="theme"/>.</summary>
    public static Color ThemeColour(string key, ThemeVariant theme)
    {
        Assert.True(Avalonia.Application.Current!.Resources.TryGetResource(key, theme, out var value));
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
