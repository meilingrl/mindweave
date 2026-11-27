using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

using Mindweave.Application.SummaryNetwork;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.SummaryLinks;

/// <summary>AI-suggested links the user confirms (requirements §10.3.4), as the summary network uses them.</summary>
public interface ILinkSuggestions
{
    /// <summary>A workspace's suggestions changed, or a 建议关联 request for it started or ended. Raised on worker threads.</summary>
    event EventHandler<WorkspaceId>? Changed;

    Task<LinkSuggestionOffer> GetAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>
    /// 建议关联 (or 重试): one request now, only because the user asked (never automatic). While one runs for the workspace
    /// the same request is joined, not sent again. Never throws for a refused or failed request; the outcome says why
    /// nothing was stored (<see cref="RecommendationProblem.NoContent"/>: fewer than three summaries;
    /// <see cref="RecommendationProblem.Empty"/>: no new valid pair).
    /// </summary>
    Task<RecommendationOutcome> SuggestAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>
    /// 接受: the suggestion becomes a link with origin <see cref="SummaryLinkOrigin.Confirmed"/> and the (possibly edited)
    /// note. Refused (<see cref="SummaryLinkRefusedException"/>) for a note over 16 characters (nothing written), a suggestion
    /// no longer pending, or a pair linked meanwhile (the suggestion is then dropped); <see cref="StorageWriteException"/>
    /// while storage is paused.
    /// </summary>
    Task<SummaryLink> AcceptAsync(LinkSuggestion suggestion, string? note, CancellationToken cancellationToken);

    /// <summary>忽略: kept as ignored, so the pair is never suggested again. False when it was no longer pending.</summary>
    Task<bool> IgnoreAsync(LinkSuggestion suggestion, CancellationToken cancellationToken);

    /// <summary>
    /// 全部忽略: the suggestions the user was asked about (<paramref name="suggestions"/>), those still pending, are ignored;
    /// one stored after the question (a request that ended meanwhile) stays pending. Returns how many.
    /// </summary>
    Task<int> IgnoreAllAsync(WorkspaceId workspace, IReadOnlyCollection<LinkSuggestionId> suggestions, CancellationToken cancellationToken);
}

/// <summary>
/// Makes and keeps link suggestions (requirements §10.3.4; architecture §6 "首版后的推荐与关联建议"). A request is
/// workspace-level work through the generation coordinator at the recommendations' priority: it starts only when no answer,
/// title or summary waits, and is not interrupted once it runs; it is recorded for its workspace as
/// <see cref="RequestAttribution.SuggestLinks"/>. At most one per workspace. Input is summary titles and short excerpts
/// only (<see cref="LinkSuggestionPrompt"/>). A result is written only if the workspace and both summaries of a pair still
/// exist; deleting the workspace stops its request. Nothing is re-sent after a restart, and nothing is ever automatic.
/// </summary>
public sealed partial class LinkSuggestionService : ILinkSuggestions, IDisposable
{
    private readonly ILinkSuggestionStore _store;
    private readonly IRecommendationStore _summaries;
    private readonly SummaryLinkService _links;
    private readonly ModelSettingsService _models;
    private readonly StorageHealth _health;
    private readonly GenerationCoordinator _generation;
    private readonly IClock _clock;
    private readonly ILogger<LinkSuggestionService> _logger;
    private readonly ISummaryNetworkLayout _network;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<WorkspaceId, Run> _running = [];
    private readonly Dictionary<WorkspaceId, RecommendationOutcome> _outcomes = [];
    private readonly HashSet<Task> _inFlight = [];
    private bool _disposed;
    private int _sent;

    public LinkSuggestionService(
        ILinkSuggestionStore store,
        IRecommendationStore summaries,
        SummaryLinkService links,
        ModelSettingsService models,
        StorageHealth health,
        GenerationCoordinator generation,
        IClock clock,
        ILogger<LinkSuggestionService> logger,
        ISummaryNetworkLayout network)
    {
        ArgumentNullException.ThrowIfNull(network);
        _network = network;
        _store = store;
        _summaries = summaries;
        _links = links;
        _models = models;
        _health = health;
        _generation = generation;
        _clock = clock;
        _logger = logger;

        // A link made, changed or deleted anywhere may take a pending suggestion along (made by hand: its pair is linked now;
        // deleted: its pair is ignored now), so the suggestions are read again too.
        _links.Changed += OnLinksChanged;
    }

    public event EventHandler<WorkspaceId>? Changed;

    /// <summary>Requests sent so far (tests).</summary>
    public int Sent => Volatile.Read(ref _sent);

    /// <summary>Completes once no request is in flight (tests).</summary>
    public async Task SettledAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                pending = [.. _inFlight];
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    public async Task<LinkSuggestionOffer> GetAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        var items = await _store.ListPendingAsync(workspace, cancellationToken).ConfigureAwait(false);

        // A suggestion stored before the rule (design session-network §8), or whose summaries were moved onto one line of
        // descent since, is not offered: a branch line already joins them.
        if (items.Count > 0)
        {
            var input = new LinkSuggestionInput([], 0, []) { Lineage = await LineageAsync(workspace, cancellationToken).ConfigureAwait(false) };
            items = [.. items.Where(i => !input.OnOneLine(i.NodeA, i.NodeB))];
        }
        lock (_gate)
        {
            var running = _running.ContainsKey(workspace);
            return new LinkSuggestionOffer(items, running, running ? null : _outcomes.GetValueOrDefault(workspace));
        }
    }

    /// <summary>Where each summary of the workspace sits: its conversation, its exploration's root and the conversations above it.</summary>
    private async Task<IReadOnlyDictionary<SummaryNodeId, SummaryLineage>> LineageAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        (await _network.ProjectAsync(workspace, cancellationToken).ConfigureAwait(false)).Nodes
            .Where(n => n.HasSummary)
            .ToDictionary(n => n.Id, n => new SummaryLineage(n.Conversation, n.Root, n.Ancestors));

    public Task<RecommendationOutcome> SuggestAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        Run run;
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(RecommendationOutcome.Refused(RecommendationProblem.Cancelled));
            }

            if (_running.TryGetValue(workspace, out var existing))
            {
                return existing.Task.WaitAsync(cancellationToken);
            }

            var result = new TaskCompletionSource<RecommendationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            run = new Run(result, CancellationTokenSource.CreateLinkedTokenSource(_stop.Token));
            _running[workspace] = run;
            _outcomes.Remove(workspace);
        }

        Track(Task.Run(() => ExecuteRunAsync(workspace, run), CancellationToken.None));
        Changed?.Invoke(this, workspace);
        return run.Task.WaitAsync(cancellationToken);
    }

    public async Task<SummaryLink> AcceptAsync(LinkSuggestion suggestion, string? note, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (SummaryLinkNote.IsTooLong(note))
        {
            throw new SummaryLinkRefusedException(SummaryLinkRefusal.NoteTooLong);
        }

        var accepted = await CommitAsync(() => _store.AcceptAsync(suggestion.Id, SummaryLinkNote.Normalize(note), _clock.UtcNow, cancellationToken)).ConfigureAwait(false);
        if (accepted.Workspace is { } workspace)
        {
            // A link was made (or the stale suggestion dropped): the links' change reads the suggestions again too (OnLinksChanged).
            _links.RaiseChanged(workspace);
        }

        return accepted.Link ?? throw new SummaryLinkRefusedException(accepted.Refused ?? SummaryLinkRefusal.Missing);
    }

    public async Task<bool> IgnoreAsync(LinkSuggestion suggestion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        var ignored = await CommitAsync(() => _store.IgnoreAsync(suggestion.Id, _clock.UtcNow, cancellationToken)).ConfigureAwait(false);
        Changed?.Invoke(this, suggestion.Workspace);
        return ignored;
    }

    public async Task<int> IgnoreAllAsync(WorkspaceId workspace, IReadOnlyCollection<LinkSuggestionId> suggestions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        var count = await CommitAsync(() => _store.IgnoreAllAsync(workspace, suggestions, _clock.UtcNow, cancellationToken)).ConfigureAwait(false);
        Changed?.Invoke(this, workspace);
        return count;
    }

    /// <summary>
    /// The workspace was deleted (architecture §6: "删除…确认时先取消"): its running request stops and what was kept about it in
    /// memory goes. Its suggestions, ignored ones included, went with the commit (v13 cascade); a result that still arrives
    /// finds no workspace and is dropped.
    /// </summary>
    public async Task ForgetAsync(WorkspaceId workspace)
    {
        Run? run;
        lock (_gate)
        {
            _running.TryGetValue(workspace, out run);
            _outcomes.Remove(workspace);
        }

        if (run is null)
        {
            return;
        }

        await run.Stop.CancelAsync().ConfigureAwait(false);
        await run.Task.ConfigureAwait(false);
    }

    private async Task ExecuteRunAsync(WorkspaceId workspace, Run run)
    {
        RecommendationOutcome outcome;
        try
        {
            outcome = await ExecuteAsync(workspace, run.Stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = RecommendationOutcome.Refused(RecommendationProblem.Cancelled);
        }
#pragma warning disable CA1031 // A failed write or read is a failed request: logged, said quietly, retried by the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCrashed(_logger, ex);
            outcome = RecommendationOutcome.Refused(RecommendationProblem.Failed);
        }

        lock (_gate)
        {
            if (_running.TryGetValue(workspace, out var current) && ReferenceEquals(current, run))
            {
                _running.Remove(workspace);
            }

            if (outcome.Problem is { } problem and not RecommendationProblem.Cancelled && !_disposed)
            {
                _outcomes[workspace] = outcome;
            }
        }

        if (outcome.Problem is RecommendationProblem.Failed or RecommendationProblem.Credentials)
        {
            LogFailed(_logger, outcome.Problem.Value, outcome.Error);
        }

        run.Result.TrySetResult(outcome);
        Changed?.Invoke(this, workspace);
    }

    private async Task<RecommendationOutcome> ExecuteAsync(WorkspaceId workspace, CancellationToken stop)
    {
        var model = await _models.GetActiveSnapshotAsync(stop).ConfigureAwait(false);
        if (model is null)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.NotConfigured);
        }

        // Titles and short excerpts of the current versions only (archived conversations' summaries included), the most
        // recently updated first; and the pairs not to suggest again.
        var bases = await _summaries.ListBasesAsync(workspace, LinkSuggestionPrompt.MaxSummaries, RecommendationPrompt.BodyChars, stop).ConfigureAwait(false);
        if (bases.Count < LinkSuggestionPrompt.MinSummaries)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.NoContent);
        }

        if (_health.IsBlocked)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.StoragePaused);
        }

        var avoid = await _store.ListAvoidedAsync(workspace, stop).ConfigureAwait(false);

        // Where each summary sits in its exploration, so a summary is never paired with one on its own line of descent.
        var lineage = await LineageAsync(workspace, stop).ConfigureAwait(false);
        var input = LinkSuggestionPrompt.SelectInput(bases.Listed, bases.Count, avoid, lineage);
        string output;
        try
        {
            Interlocked.Increment(ref _sent);
            output = await _generation.RequestTransientAsync(
                LinkSuggestionPrompt.Request(model, input),
                stop,
                GenerationPriority.Idle,
                new RequestAttribution(workspace, RequestAttribution.SuggestLinks),
                yields: false).ConfigureAwait(false);
        }
        catch (GenerationPreemptedException)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.Unavailable);
        }
        catch (InvalidOperationException)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.Unavailable);
        }
        catch (StorageAdmissionRefusedException)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.StoragePaused);
        }
        catch (ChatClientException ex) when (ex.Kind is GenerationErrorKind.Unauthorized or GenerationErrorKind.CredentialUnavailable or GenerationErrorKind.NotConfigured)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.Credentials, ex.Kind);
        }
        catch (ChatClientException ex)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.Failed, ex.Kind);
        }

        var items = LinkSuggestionPrompt.Parse(output, input, avoid);
        if (items.Count == 0)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.Empty);
        }

        stop.ThrowIfCancellationRequested();
        var stored = await _store.AddBatchAsync(workspace, new LinkSuggestionBatch(Guid.CreateVersion7(), _clock.UtcNow, items), CancellationToken.None).ConfigureAwait(false);
        return stored switch
        {
            null => RecommendationOutcome.Refused(RecommendationProblem.Cancelled),
            0 => RecommendationOutcome.Refused(RecommendationProblem.Empty),
            { } added => new RecommendationOutcome(added),
        };
    }

    /// <summary>Like the other non-AI commits: refused while storage is paused, and a storage failure pauses it.</summary>
    private async Task<T> CommitAsync<T>(Func<Task<T>> commit)
    {
        _health.ThrowIfBlocked();
        try
        {
            return await commit().ConfigureAwait(false);
        }
        catch (StorageWriteException ex)
        {
            _health.Report(ex);
            throw;
        }
    }

    private void OnLinksChanged(object? sender, WorkspaceId workspace) => Changed?.Invoke(this, workspace);

    private void Track(Task task)
    {
        lock (_gate)
        {
            _inFlight.Add(task);
        }

        _ = task.ContinueWith(
            t =>
            {
                lock (_gate)
                {
                    _inFlight.Remove(t);
                }

                if (t.Exception is { } ex)
                {
                    LogCrashed(_logger, ex.InnerException ?? ex);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Stops any request running (the application is closing).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _links.Changed -= OnLinksChanged;

        // Cancelled, not disposed: a request ending meanwhile may still read the token.
        _stop.Cancel();
    }

    private sealed record Run(TaskCompletionSource<RecommendationOutcome> Result, CancellationTokenSource Stop)
    {
        public Task<RecommendationOutcome> Task => Result.Task;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Link suggestions not made: {Problem} {Error}")]
    private static partial void LogFailed(ILogger logger, RecommendationProblem problem, GenerationErrorKind? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Link suggestions stopped unexpectedly")]
    private static partial void LogCrashed(ILogger logger, Exception exception);
}
