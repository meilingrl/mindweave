namespace Mindweave.Desktop.SummaryNetwork;

/// <summary>
/// Switches on the session network (docs/design/session-network.md): every conversation is a dot and the layouts are
/// computed, never saved. Off unless the environment variable <c>MINDWEAVE_SESSION_NETWORK</c> is <c>1</c>.
/// </summary>
public sealed record SessionNetworkGate(bool Enabled)
{
    public const string Variable = "MINDWEAVE_SESSION_NETWORK";

    public static SessionNetworkGate FromEnvironment() => new(Environment.GetEnvironmentVariable(Variable) == "1");
}

/// <summary>The two views of the session network.</summary>
public enum SummaryNetworkViewMode
{
    Spatial,
    Timeline,
}
