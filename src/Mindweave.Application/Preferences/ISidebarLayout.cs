namespace Mindweave.Application.Preferences;

/// <summary>The workbench sidebar's width and whether it is collapsed (S7 round 3, the user's acceptance review 2026-11-02).</summary>
public sealed record SidebarLayout(double Width, bool IsCollapsed)
{
    public const double MinWidth = 200;

    public const double MaxWidth = 420;

    public const double DefaultWidth = 240;

    public static SidebarLayout Default { get; } = new(DefaultWidth, IsCollapsed: false);

    /// <summary>A width inside the bounds; anything that is not a finite number is the default.</summary>
    public static double ClampWidth(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, MinWidth, MaxWidth) : DefaultWidth;
}

/// <summary>The sidebar layout as the workbench uses it (course requirement C3): restored at start, kept when changed.</summary>
public interface ISidebarLayout
{
    Task<SidebarLayout> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(SidebarLayout layout, CancellationToken cancellationToken);
}
