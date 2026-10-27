namespace Mindweave.Desktop.Navigation;

/// <summary>
/// What to show in the content area, by stable identity only — never a control or a view model.
/// Routes are compared by value, so pushing the page already on top is a no-op.
/// </summary>
public abstract record ContentRoute
{
    public static ContentRoute HomeOf(MainSection section) => section switch
    {
        MainSection.Explore => ExploreHomeRoute.Instance,
        MainSection.Network => NetworkHomeRoute.Instance,
        MainSection.Settings => SettingsHomeRoute.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };
}

/// <summary>A route that can be restored after restart as the last valid page.</summary>
public interface IRestorableRoute
{
    string Key { get; }
}

/// <summary>
/// A conversation page. <paramref name="SourceOf"/> asks the page to locate and highlight the passage that branch
/// was created from (return to source); without it the page restores its own reading position.
/// </summary>
public sealed record ConversationRoute(Mindweave.Core.Conversations.ConversationId Id, Mindweave.Core.Conversations.ConversationId? SourceOf = null)
    : ContentRoute, IRestorableRoute
{
    private const string Prefix = "conversation:";

    public string Key => Prefix + Id;

    public static ConversationRoute? FromKey(string? key) =>
        key is not null && key.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(key[Prefix.Length..], "D", out var id)
            ? new ConversationRoute(new Mindweave.Core.Conversations.ConversationId(id))
            : null;
}

/// <summary>A summary node's page; which version is shown is page state, not part of the route.</summary>
public sealed record SummaryRoute(Mindweave.Core.Summaries.SummaryNodeId Node) : ContentRoute, IRestorableRoute
{
    private const string Prefix = "summary:";

    public string Key => Prefix + Node;

    public static SummaryRoute? FromKey(string? key) =>
        key is not null && key.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(key[Prefix.Length..], "D", out var id)
            ? new SummaryRoute(new Mindweave.Core.Summaries.SummaryNodeId(id))
            : null;
}

public sealed record NewConversationRoute : ContentRoute
{
    public static NewConversationRoute Instance { get; } = new();
}

public sealed record ExploreHomeRoute : ContentRoute
{
    public static ExploreHomeRoute Instance { get; } = new();
}

public sealed record NetworkHomeRoute : ContentRoute
{
    public static NetworkHomeRoute Instance { get; } = new();
}

public sealed record SettingsHomeRoute : ContentRoute
{
    public static SettingsHomeRoute Instance { get; } = new();
}

public sealed record StorageDetailsRoute : ContentRoute
{
    public static StorageDetailsRoute Instance { get; } = new();
}
