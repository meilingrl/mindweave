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
        MainSection.SummaryNetwork => SummaryNetworkHomeRoute.Instance,
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

    public string Key => Mindweave.Application.Storage.PageLocation.ConversationKey(Id);

    public static ConversationRoute? FromKey(string? key) =>
        key is not null && key.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(key[Prefix.Length..], "D", out var id)
            ? new ConversationRoute(new Mindweave.Core.Conversations.ConversationId(id))
            : null;
}

/// <summary>A summary node's page; which version is shown is page state, not part of the route.</summary>
public sealed record SummaryRoute(Mindweave.Core.Summaries.SummaryNodeId Node) : ContentRoute, IRestorableRoute
{
    private const string Prefix = "summary:";

    public string Key => Mindweave.Application.Storage.PageLocation.SummaryKey(Node);

    public static SummaryRoute? FromKey(string? key) =>
        key is not null && key.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(key[Prefix.Length..], "D", out var id)
            ? new SummaryRoute(new Mindweave.Core.Summaries.SummaryNodeId(id))
            : null;
}

/// <summary>
/// 新探索: the first question of a new root conversation. Opened from a recommended node (requirements §10.3.2) it carries
/// that recommendation's identity and question, put in the input unsent; the plain page has neither.
/// </summary>
public sealed record NewConversationRoute(
    Mindweave.Application.Recommendations.RecommendationId? From = null,
    string? Question = null,
    Mindweave.Core.Summaries.SummaryNodeId? Basis = null) : ContentRoute
{
    public static NewConversationRoute Instance { get; } = new();
}

public sealed record ExploreHomeRoute : ContentRoute
{
    public static ExploreHomeRoute Instance { get; } = new();
}

public sealed record SummaryNetworkHomeRoute : ContentRoute
{
    public static SummaryNetworkHomeRoute Instance { get; } = new();
}

/// <summary>The archived conversations of the workspace (S6), in the explore section; restored as the last page.</summary>
public sealed record ArchivedRoute : ContentRoute, IRestorableRoute
{
    public const string RouteKey = "archived";

    public static ArchivedRoute Instance { get; } = new();

    public string Key => RouteKey;
}

public sealed record SettingsHomeRoute : ContentRoute
{
    public static SettingsHomeRoute Instance { get; } = new();
}

public sealed record StorageDetailsRoute : ContentRoute
{
    public static StorageDetailsRoute Instance { get; } = new();
}

/// <summary>设置 › 诊断 (D2 §2.2): recent requests, the log and the diagnostic package. Not restored as the last page.</summary>
public sealed record DiagnosticsRoute : ContentRoute
{
    public static DiagnosticsRoute Instance { get; } = new();
}

/// <summary>设置 › 使用统计 (2026-11-12): token use and activity facts. Not restored as the last page.</summary>
public sealed record UsageStatisticsRoute : ContentRoute
{
    public static UsageStatisticsRoute Instance { get; } = new();
}

/// <summary>设置 › 工作区: every workspace, to switch to, rename or delete (2026-11-10). Not restored as the last page.</summary>
public sealed record WorkspacesRoute : ContentRoute
{
    public static WorkspacesRoute Instance { get; } = new();
}
