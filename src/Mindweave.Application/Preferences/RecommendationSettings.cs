using Mindweave.Application.Storage;

namespace Mindweave.Application.Preferences;

/// <summary>
/// The recommendation choices (requirements §10.3 共同规则, beside 自动准备工作区名称建议 in 设置 › 提示): recommended
/// questions on 新探索 and recommended nodes in the network can each be turned off, and 自动准备推荐 off means they are
/// only made when the user asks (换一批). All three are on unless turned off.
/// </summary>
public interface IRecommendationSettings
{
    Task<bool> GetQuestionsAsync(CancellationToken cancellationToken);

    /// <summary>Applies at once for this run, even when the write then fails.</summary>
    Task SetQuestionsAsync(bool enabled, CancellationToken cancellationToken);

    Task<bool> GetNodesAsync(CancellationToken cancellationToken);

    /// <summary>Applies at once for this run, even when the write then fails.</summary>
    Task SetNodesAsync(bool enabled, CancellationToken cancellationToken);

    Task<bool> GetAutoPrepareAsync(CancellationToken cancellationToken);

    /// <summary>Applies at once for this run, even when the write then fails.</summary>
    Task SetAutoPrepareAsync(bool enabled, CancellationToken cancellationToken);
}

/// <summary>One preference key each (no migration), held in memory once read or made, as for the other hints.</summary>
public sealed class RecommendationSettingsService(IPreferenceStore store) : IRecommendationSettings
{
    public const string QuestionsKey = "recommend.questions";

    public const string NodesKey = "recommend.nodes";

    public const string AutoPrepareKey = "recommend.auto-prepare";

    private readonly PreferenceChoice _questions = new(store, QuestionsKey, fallback: true);
    private readonly PreferenceChoice _nodes = new(store, NodesKey, fallback: true);
    private readonly PreferenceChoice _autoPrepare = new(store, AutoPrepareKey, fallback: true);

    public Task<bool> GetQuestionsAsync(CancellationToken cancellationToken) => _questions.GetAsync(cancellationToken);

    public Task SetQuestionsAsync(bool enabled, CancellationToken cancellationToken) => _questions.SetAsync(enabled, cancellationToken);

    public Task<bool> GetNodesAsync(CancellationToken cancellationToken) => _nodes.GetAsync(cancellationToken);

    public Task SetNodesAsync(bool enabled, CancellationToken cancellationToken) => _nodes.SetAsync(enabled, cancellationToken);

    public Task<bool> GetAutoPrepareAsync(CancellationToken cancellationToken) => _autoPrepare.GetAsync(cancellationToken);

    public Task SetAutoPrepareAsync(bool enabled, CancellationToken cancellationToken) => _autoPrepare.SetAsync(enabled, cancellationToken);
}
