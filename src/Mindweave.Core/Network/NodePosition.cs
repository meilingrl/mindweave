namespace Mindweave.Core.Network;

/// <summary>A node center in absolute canvas coordinates, independent of the viewport.</summary>
public readonly record struct NodePosition(double X, double Y)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && Math.Abs(X) <= 1e9 && Math.Abs(Y) <= 1e9;

    public void Validate(string? paramName = null)
    {
        if (!IsValid) throw new ArgumentOutOfRangeException(paramName, this, "节点坐标必须有限且绝对值不超过 1e9。");
    }
}
