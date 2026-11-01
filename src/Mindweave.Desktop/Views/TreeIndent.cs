using Avalonia;
using Avalonia.Data.Converters;

namespace Mindweave.Desktop.Views;

/// <summary>
/// Turns a branch level into the left margin of a tree row (the sidebar tree and the archived page). View models only
/// expose the level as a number (architecture review L5).
/// </summary>
public static class TreeIndent
{
    /// <summary>Indentation per level.</summary>
    public const double Step = 14;

    /// <summary>Deeper levels share the last indentation so long chains stay readable in a narrow sidebar.</summary>
    public const int MaxIndentedDepth = 6;

    public static FuncValueConverter<int, Thickness> Margin { get; } = new(depth => new Thickness(Offset(depth), 0, 0, 0));

    public static double Offset(int depth) => Math.Clamp(depth, 0, MaxIndentedDepth) * Step;
}
