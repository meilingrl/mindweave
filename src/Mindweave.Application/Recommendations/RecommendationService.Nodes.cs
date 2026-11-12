using System.Globalization;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Recommendations;

/// <summary>
/// What the summary network shows (requirements §10.3.2): whether nodes are on, how many summaries the workspace has,
/// the pending nodes (at most <see cref="RecommendationService.NodeTarget"/> of that count), whether they are hidden
/// (隐藏推荐), whether a request runs for them, and the last background failure to mention quietly.
/// </summary>
public sealed record NodeOffer(bool Enabled, int SummaryCount, IReadOnlyList<Recommendation> Items, bool Hidden, bool IsPreparing, RecommendationOutcome? Failure, bool AutoPrepares = true)
{
    public static NodeOffer Off { get; } = new(false, 0, [], false, false, null);
}

/// <summary>
/// The tier a network was last filled for and from when (§10.3.2): a node used or ignored since then keeps its slot
/// empty, one whose basis summary went frees it.
/// </summary>
internal readonly record struct NodePlan(int Tier, DateTimeOffset Since)
{
    public static NodePlan None { get; } = new(0, DateTimeOffset.MinValue);

    public string Format() => Tier.ToString(CultureInfo.InvariantCulture) + "|" + Since.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    public static NodePlan? Parse(string? text)
    {
        var parts = text?.Split('|');
        return parts is [var tier, var since]
            && int.TryParse(tier, NumberStyles.None, CultureInfo.InvariantCulture, out var t)
            && long.TryParse(since, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms)
            && t is >= 0 and <= RecommendationService.MaxNodes
            && ms >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds() && ms <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? new NodePlan(t, DateTimeOffset.FromUnixTimeMilliseconds(ms))
            : null;
    }
}

public sealed partial class RecommendationService
{
    /// <summary>The most nodes a network shows (over 30 summaries).</summary>
    public const int MaxNodes = 3;

    /// <summary>Preference key prefix: the tier a workspace's network was last filled for, and since when.</summary>
    public const string NodePlanKeyPrefix = "recommend.nodes.plan:";

    /// <summary>Preference key prefix: 隐藏推荐 is on for the workspace (absent: shown).</summary>
    public const string NodesHiddenKeyPrefix = "recommend.nodes.hidden:";

    /// <summary>How many nodes a network with <paramref name="summaries"/> saved summaries shows: 0; 1–10 → 1; 11–30 → 2; more → 3.</summary>
    public static int NodeTarget(int summaries) => summaries switch
    {
        <= 0 => 0,
        <= 10 => 1,
        <= 30 => 2,
        _ => MaxNodes,
    };

    public async Task<NodeOffer> GetNodesAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        if (!await _settings.GetNodesAsync(cancellationToken).ConfigureAwait(false))
        {
            return NodeOffer.Off;
        }

        var bases = await _store.ListBasesAsync(workspace, 0, 1, cancellationToken).ConfigureAwait(false);
        var rows = await _store.ListAsync(workspace, RecommendationKind.Node, cancellationToken).ConfigureAwait(false);

        // Below a tier the extra ones are not shown even before they are removed.
        var pending = rows.Where(r => r.State == RecommendationState.Pending).Take(NodeTarget(bases.Count)).ToList();
        var hidden = await IsHiddenAsync(workspace, cancellationToken).ConfigureAwait(false);
        var auto = await _settings.GetAutoPrepareAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var preparing = _running.ContainsKey((workspace, RecommendationKind.Node));
            return new NodeOffer(true, bases.Count, pending, hidden, preparing, preparing ? null : _failures.GetValueOrDefault((workspace, RecommendationKind.Node)), auto);
        }
    }

    public void NetworkOpened(WorkspaceId workspace) => Arm(workspace, RecommendationKind.Node, opened: false);

    public async Task<RecommendationOutcome> RefreshNodesAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        if (!await _settings.GetNodesAsync(cancellationToken).ConfigureAwait(false))
        {
            return RecommendationOutcome.Refused(RecommendationProblem.Disabled);
        }

        var tier = NodeTarget((await _store.ListBasesAsync(workspace, 0, 1, cancellationToken).ConfigureAwait(false)).Count);
        if (tier == 0)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.NoContent);
        }

        return await RunAsync(workspace, RecommendationKind.Node, tier, manual: true, replace: true, planTier: tier).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetNodesHiddenAsync(WorkspaceId workspace, bool hidden, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_forgotten.Contains(workspace))
            {
                return;
            }

            // Applies at once for this run, even when the write then fails (as the other hints).
            _hidden[workspace] = hidden;
        }

        Changed?.Invoke(this, workspace);
        var key = NodesHiddenKeyPrefix + workspace;
        if (hidden)
        {
            await _preferences.SetAsync(key, "1", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _preferences.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }

        if (hidden)
        {
            bool forgotten;
            lock (_gate)
            {
                forgotten = _forgotten.Contains(workspace);
            }

            if (forgotten)
            {
                await _preferences.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            // Shown again: what was not prepared while hidden may be prepared now.
            Arm(workspace, RecommendationKind.Node, opened: false);
        }
    }

    private async Task<bool> IsHiddenAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_hidden.TryGetValue(workspace, out var known))
            {
                return known;
            }
        }

        var hidden = await _preferences.GetAsync(NodesHiddenKeyPrefix + workspace, cancellationToken).ConfigureAwait(false) == "1";
        lock (_gate)
        {
            if (!_forgotten.Contains(workspace))
            {
                _hidden.TryAdd(workspace, hidden);
                return _hidden[workspace];
            }
        }

        return hidden;
    }

    /// <summary>
    /// §10.3.2's rules, on opening the network or a change of its summaries. A drop below a tier removes the extra pending
    /// nodes (no request). With 自动准备推荐 on and the nodes shown, a request is made only when the network never had any
    /// (fill the tier), when it rose to a higher tier than it was filled for (fill up to it, the empty slots included), or
    /// when a node's basis summary was deleted (fill that slot). A slot emptied by 忽略 or by exploring stays empty.
    /// Never twice for the same state in one run.
    /// </summary>
    private async Task ConsiderNodesAsync(WorkspaceId workspace)
    {
        if (_stop.IsCancellationRequested || _generation.IsClosed || _health.IsBlocked
            || !await _settings.GetNodesAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        var key = (workspace, RecommendationKind.Node);
        lock (_gate)
        {
            if (_running.ContainsKey(key) || _forgotten.Contains(workspace))
            {
                return;
            }
        }

        var count = (await _store.ListBasesAsync(workspace, 0, 1, CancellationToken.None).ConfigureAwait(false)).Count;
        var target = NodeTarget(count);
        var rows = await _store.ListAsync(workspace, RecommendationKind.Node, CancellationToken.None).ConfigureAwait(false);
        var pending = rows.Where(r => r.State == RecommendationState.Pending).ToList();
        if (pending.Count > target)
        {
            // Fewer summaries than the tier the nodes were made for: the newest extra ones go, without a request.
            var extra = pending.Skip(target).Select(r => r.Id).ToList();
            await _store.RemovePendingAsync(workspace, RecommendationKind.Node, extra, CancellationToken.None).ConfigureAwait(false);
            pending = [.. pending.Take(target)];
            Changed?.Invoke(this, workspace);
        }

        var plan = await ReadPlanAsync(workspace).ConfigureAwait(false) ?? NodePlan.None;
        if (target < plan.Tier)
        {
            plan = plan with { Tier = target };
            await WritePlanAsync(workspace, plan).ConfigureAwait(false);
        }

        if (target == 0
            || await IsHiddenAsync(workspace, CancellationToken.None).ConfigureAwait(false)
            || !await _settings.GetAutoPrepareAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        int missing;
        int? planTier;
        if (target > plan.Tier)
        {
            // Never filled, or a higher tier: every slot up to it, the ones left empty by 忽略 included.
            missing = target - pending.Count;
            planTier = target;
            if (missing <= 0)
            {
                await WritePlanAsync(workspace, new NodePlan(target, _clock.UtcNow)).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            // Same tier: slots spent since it was filled stay spent; a node gone with its basis summary left one free.
            var spent = rows.Count(r => r.State != RecommendationState.Pending && r.UpdatedAt >= plan.Since);
            missing = plan.Tier - pending.Count - spent;
            planTier = null;
            if (missing <= 0)
            {
                return;
            }
        }

        var signature = string.Create(CultureInfo.InvariantCulture, $"{target}|{plan.Format()}|{string.Join(',', rows.Select(r => r.Id))}|{missing}");
        lock (_gate)
        {
            if (_disposed || _tried.GetValueOrDefault(key) == signature)
            {
                return;
            }

            _tried[key] = signature;
        }

        var outcome = await RunAsync(workspace, RecommendationKind.Node, missing, manual: false, replace: false, planTier).ConfigureAwait(false);
        if (outcome.NothingSent)
        {
            lock (_gate)
            {
                if (_tried.GetValueOrDefault(key) == signature)
                {
                    _tried.Remove(key);
                }
            }
        }
    }

    private async Task<NodePlan?> ReadPlanAsync(WorkspaceId workspace) =>
        NodePlan.Parse(await _preferences.GetAsync(NodePlanKeyPrefix + workspace, CancellationToken.None).ConfigureAwait(false));

    private async Task WritePlanAsync(WorkspaceId workspace, NodePlan plan)
    {
        lock (_gate)
        {
            if (_forgotten.Contains(workspace))
            {
                return;
            }
        }

        var key = NodePlanKeyPrefix + workspace;
        await _preferences.SetAsync(key, plan.Format(), CancellationToken.None).ConfigureAwait(false);

        // Deleted while this was written: the forgetting may have run first, so it is done again.
        bool forgotten;
        lock (_gate)
        {
            forgotten = _forgotten.Contains(workspace);
        }

        if (forgotten)
        {
            await _preferences.RemoveAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ForgetPreferencesAsync(WorkspaceId workspace)
    {
        try
        {
            await _preferences.RemoveAsync(NodePlanKeyPrefix + workspace, CancellationToken.None).ConfigureAwait(false);
            await _preferences.RemoveAsync(NodesHiddenKeyPrefix + workspace, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Deleting the workspace already succeeded; two leftover keys name nothing that exists.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCrashed(_logger, ex);
        }
    }
}
