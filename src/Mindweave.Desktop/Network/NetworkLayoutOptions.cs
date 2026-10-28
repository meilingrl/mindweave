namespace Mindweave.Desktop.Network;

/// <summary>Measured axis-aligned node bounds in world units, including its title.</summary>
public readonly record struct NodeExtent(double Width, double Height)
{
    internal static NodeExtent Default => new(200, 112);

    internal void Validate(string paramName)
    {
        if (!double.IsFinite(Width) || !double.IsFinite(Height) || Width <= 0 || Height <= 0 || Width > 1e9 || Height > 1e9)
            throw new ArgumentOutOfRangeException(paramName, this, "节点边界必须为有限正数且不超过 1e9。");
    }
}

public sealed record NetworkLayoutOptions(double RadialGap = 200, double GroupGap = 320,
    double CollisionGap = 24, int CollisionPasses = 24)
{
    internal void Validate()
    {
        if (!double.IsFinite(RadialGap) || RadialGap <= 0 || RadialGap > 1e9)
            throw new ArgumentOutOfRangeException(nameof(RadialGap));
        if (!double.IsFinite(GroupGap) || GroupGap < 0 || GroupGap > 1e9)
            throw new ArgumentOutOfRangeException(nameof(GroupGap));
        if (!double.IsFinite(CollisionGap) || CollisionGap < 0 || CollisionGap > 1e9)
            throw new ArgumentOutOfRangeException(nameof(CollisionGap));
        if (CollisionPasses is < 0 or > 24)
            throw new ArgumentOutOfRangeException(nameof(CollisionPasses));
    }
}
