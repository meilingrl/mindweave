using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Preferences;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Recommendations;

/// <summary>Why a request made no recommendations, or why none was sent.</summary>
public enum RecommendationProblem
{
    /// <summary>Recommendations of this kind are turned off in 设置 › 提示; nothing was sent.</summary>
    Disabled,

    /// <summary>The workspace has no conversation title and no summary to draw on; nothing was sent.</summary>
    NoContent,

    /// <summary>No model is configured; nothing was sent.</summary>
    NotConfigured,

    /// <summary>The key is missing, unreadable or refused by the service.</summary>
    Credentials,

    /// <summary>Storage is paused, and with it every new AI request; nothing was sent.</summary>
    StoragePaused,

    /// <summary>New requests are refused for the moment (an exit is under way); nothing was sent.</summary>
    Unavailable,

    /// <summary>The service failed, or its output was cut off or empty (<see cref="RecommendationOutcome.Error"/> says how).</summary>
    Failed,

    /// <summary>The service answered, but nothing in it was usable (every item a repeat, or none readable).</summary>
    Empty,

    /// <summary>Stopped: the workspace was deleted, or the application is closing. Nothing is shown.</summary>
    Cancelled,
}

/// <summary>What one request did: how many recommendations it stored, or why none.</summary>
public sealed record RecommendationOutcome(int Added, RecommendationProblem? Problem = null, GenerationErrorKind? Error = null)
{
    public static RecommendationOutcome Refused(RecommendationProblem problem, GenerationErrorKind? error = null) => new(0, problem, error);

    /// <summary>Nothing was sent: a later look may send one (unlike a request that was sent and failed).</summary>
    public bool NothingSent => Problem is RecommendationProblem.Disabled or RecommendationProblem.NoContent or RecommendationProblem.NotConfigured
        or RecommendationProblem.StoragePaused or RecommendationProblem.Unavailable or RecommendationProblem.Cancelled;

    /// <summary>A problem the page shows quietly with 重试 even when the request was made in the background (§10.3 共同规则).</summary>
    public bool ShownQuietly => Problem is RecommendationProblem.Failed or RecommendationProblem.Empty;
}

/// <summary>
/// What 新探索 shows (requirements §10.3.1): whether questions are on, whether the workspace has anything to draw on, the
/// pending questions (at most <see cref="RecommendationService.ShownQuestions"/>), whether a request runs for them, and the
/// last background failure to mention quietly. <see cref="AutoPrepares"/>: 自动准备推荐 is on (off, only 换一批 makes them).
/// </summary>
public sealed record QuestionOffer(bool Enabled, bool HasContent, IReadOnlyList<Recommendation> Items, bool IsPreparing, RecommendationOutcome? Failure, bool AutoPrepares = true);

/// <summary>Recommended questions (and, later, nodes) for the pages (requirements §10.3).</summary>
public interface IRecommendations
{
    /// <summary>A workspace's recommendations changed, or a request for them started or ended. Raised on worker threads.</summary>
    event EventHandler<WorkspaceId>? Changed;

    Task<QuestionOffer> GetQuestionsAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>
    /// 新探索 was opened for the workspace: when 自动准备推荐 is on and §10.3.1's rules call for it, new questions are
    /// prepared in the background shortly after (the old ones stay until they arrive).
    /// </summary>
    void NewExplorationOpened(WorkspaceId workspace);

    /// <summary>换一批 (or 重试): asks once for new questions now. Never throws for a refused or failed request.</summary>
    Task<RecommendationOutcome> RefreshQuestionsAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>不感兴趣: removed and remembered (only its normalized question is kept).</summary>
    Task IgnoreAsync(Recommendation recommendation, CancellationToken cancellationToken);

    /// <summary>A question filled in from it was sent: removed, remembered as used.</summary>
    Task MarkUsedAsync(Recommendation recommendation, CancellationToken cancellationToken);

    /// <summary>
    /// The same by identity, for a recommended node explored from 新探索 (the page holds only its identity and question).
    /// Nothing happens when it is no longer pending (ignored meanwhile, or gone with its basis summary).
    /// </summary>
    Task MarkUsedAsync(WorkspaceId workspace, RecommendationId id, CancellationToken cancellationToken);

    /// <summary>What the summary network shows (requirements §10.3.2).</summary>
    Task<NodeOffer> GetNodesAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>
    /// The network was opened or its conversations changed: when §10.3.2's rules call for it, new nodes are prepared in the
    /// background shortly after; a drop below a tier removes the extra ones (no request). Nothing else is asked.
    /// </summary>
    void SummaryNetworkOpened(WorkspaceId workspace);

    /// <summary>换一批推荐 (or 重试): replaces the shown nodes with as many as the tier allows. Never throws for a refused or failed request.</summary>
    Task<RecommendationOutcome> RefreshNodesAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>隐藏推荐 / 显示推荐: display only, remembered per workspace; while hidden nothing is prepared by itself.</summary>
    Task SetNodesHiddenAsync(WorkspaceId workspace, bool hidden, CancellationToken cancellationToken);
}

/// <summary>How long after 新探索 opens the background looks, and the clock it waits on (tests stand it still).</summary>
public sealed class RecommendationOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Makes and keeps recommendations (requirements §10.3; architecture "首版后的推荐"). Each request is workspace-level
/// background work through the generation coordinator: idle priority, so it starts only when no answer, title or summary
/// waits, and it is not interrupted once it runs; recorded for its workspace (<c>recommend-questions</c>,
/// <c>recommend-nodes</c>). At most one request per workspace at a time (a second one waits for the first; the same kind
/// joins it). Input is titles and short excerpts only (<see cref="RecommendationPrompt"/>). A result is written only if
/// the workspace (and each item's basis summary) still exists; deleting the workspace stops its request. Nothing is
/// re-sent after a restart; a failure is kept only in memory, to be mentioned quietly on the page.
/// </summary>
public sealed partial class RecommendationService : IRecommendations, IDisposable
{
    /// <summary>新探索 lists at most this many questions; a batch asks for as many.</summary>
    public const int ShownQuestions = 3;

    private readonly IRecommendationStore _store;
    private readonly IConversationStore _conversations;
    private readonly ModelSettingsService _models;
    private readonly StorageHealth _health;
    private readonly GenerationCoordinator _generation;
    private readonly IRecommendationSettings _settings;
    private readonly IPreferenceStore _preferences;
    private readonly RecommendationOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<RecommendationService> _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<(WorkspaceId, RecommendationKind), Run> _running = [];
    private readonly Dictionary<WorkspaceId, Task> _tails = [];
    private readonly Dictionary<(WorkspaceId, RecommendationKind), RecommendationOutcome> _failures = [];
    private readonly Dictionary<(WorkspaceId, RecommendationKind), string> _tried = [];
    private readonly Dictionary<(WorkspaceId, RecommendationKind), ITimer> _timers = [];
    private readonly Dictionary<WorkspaceId, bool> _hidden = [];
    private readonly HashSet<WorkspaceId> _forgotten = [];
    private readonly HashSet<WorkspaceId> _openedPending = [];
    private readonly HashSet<Task> _inFlight = [];
    private readonly HashSet<WorkspaceId> _shown = [];
    private bool _disposed;
    private bool _credentialsRefused;
    private int _sent;

    public RecommendationService(
        IRecommendationStore store,
        IConversationStore conversations,
        ModelSettingsService models,
        StorageHealth health,
        GenerationCoordinator generation,
        IRecommendationSettings settings,
        IPreferenceStore preferences,
        RecommendationOptions options,
        IClock clock,
        ILogger<RecommendationService> logger)
    {
        _store = store;
        _conversations = conversations;
        _models = models;
        _health = health;
        _generation = generation;
        _settings = settings;
        _preferences = preferences;
        _options = options;
        _clock = clock;
        _logger = logger;
        _models.Changed += OnModelsChanged;
    }

    public event EventHandler<WorkspaceId>? Changed;

    /// <summary>Requests sent so far (tests).</summary>
    public int Sent => Volatile.Read(ref _sent);

    /// <summary>A background look is waiting to run for this workspace (tests).</summary>
    public bool IsWaiting(WorkspaceId workspace)
    {
        lock (_gate)
        {
            return _timers.Keys.Any(k => k.Item1 == workspace);
        }
    }

    /// <summary>A background look at one kind is waiting to run for this workspace (tests).</summary>
    public bool IsWaiting(WorkspaceId workspace, RecommendationKind kind)
    {
        lock (_gate)
        {
            return _timers.ContainsKey((workspace, kind));
        }
    }

    /// <summary>Completes once nothing this does is in flight: no look, no request (tests).</summary>
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

    public async Task<QuestionOffer> GetQuestionsAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        var enabled = await _settings.GetQuestionsAsync(cancellationToken).ConfigureAwait(false);
        if (!enabled)
        {
            return new QuestionOffer(false, false, [], false, null);
        }

        var rows = await _store.ListAsync(workspace, RecommendationKind.Question, cancellationToken).ConfigureAwait(false);
        var pending = rows.Where(r => r.State == RecommendationState.Pending).Take(ShownQuestions).ToList();
        var hasContent = pending.Count > 0 || await HasContentAsync(workspace, cancellationToken).ConfigureAwait(false);

        // No model, or its key refused: none is shown (§10.3 共同规则); 换一批 is still there to say why. Stored ones come
        // back with a model (or a key) that works.
        if (!await UsableModelAsync(workspace, cancellationToken).ConfigureAwait(false))
        {
            pending = [];
        }

        var auto = await _settings.GetAutoPrepareAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var preparing = _running.ContainsKey((workspace, RecommendationKind.Question));
            return new QuestionOffer(true, hasContent, pending, preparing, preparing ? null : _failures.GetValueOrDefault((workspace, RecommendationKind.Question)), auto);
        }
    }

    public void NewExplorationOpened(WorkspaceId workspace) => Arm(workspace, RecommendationKind.Question, opened: true);

    public async Task<RecommendationOutcome> RefreshQuestionsAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        if (!await _settings.GetQuestionsAsync(cancellationToken).ConfigureAwait(false))
        {
            return RecommendationOutcome.Refused(RecommendationProblem.Disabled);
        }

        return await RunAsync(workspace, RecommendationKind.Question, ShownQuestions, manual: true, replace: true, planTier: null).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task IgnoreAsync(Recommendation recommendation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        await _store.SetStateAsync(recommendation.Id, RecommendationState.Ignored, RecommendationText.Normalize(recommendation.Question), _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, recommendation.Workspace);

        // An ignored node's slot stays empty (§10.3.2); an ignored question may leave none to show (§10.3.1).
        if (recommendation.Kind == RecommendationKind.Question)
        {
            Arm(recommendation.Workspace, RecommendationKind.Question, opened: false);
        }
        else
        {
            await SpendNodeSlotAsync(recommendation.Workspace).ConfigureAwait(false);
        }
    }

    public async Task MarkUsedAsync(Recommendation recommendation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        await _store.SetStateAsync(recommendation.Id, RecommendationState.Used, RecommendationText.Normalize(recommendation.Question), _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, recommendation.Workspace);
        if (recommendation.Kind == RecommendationKind.Question)
        {
            Arm(recommendation.Workspace, RecommendationKind.Question, opened: false);
        }
        else
        {
            await SpendNodeSlotAsync(recommendation.Workspace).ConfigureAwait(false);
        }
    }

    public async Task MarkUsedAsync(WorkspaceId workspace, RecommendationId id, CancellationToken cancellationToken)
    {
        // Used keeps the question as it is; only an ignored row is reduced to its normalized form.
        if (await _store.SetStateAsync(id, RecommendationState.Used, "-", _clock.UtcNow, cancellationToken).ConfigureAwait(false))
        {
            await SpendNodeSlotAsync(workspace).ConfigureAwait(false);
            Changed?.Invoke(this, workspace);
        }
    }

    /// <summary>
    /// The workspace was deleted (architecture §6: "删除…确认时先取消"): its waiting look and running request stop, and what
    /// was remembered about it goes. A result that still arrives finds no workspace and is dropped.
    /// </summary>
    public async Task ForgetAsync(WorkspaceId workspace)
    {
        List<Run> runs;
        lock (_gate)
        {
            _forgotten.Add(workspace);
            _hidden.Remove(workspace);
            foreach (var key in _timers.Keys.Where(k => k.Item1 == workspace).ToList())
            {
                _timers.Remove(key, out var timer);
                timer!.Dispose();
            }

            runs = [.. _running.Where(r => r.Key.Item1 == workspace).Select(r => r.Value)];
            foreach (var key in _failures.Keys.Where(k => k.Item1 == workspace).ToList())
            {
                _failures.Remove(key);
            }

            foreach (var key in _tried.Keys.Where(k => k.Item1 == workspace).ToList())
            {
                _tried.Remove(key);
            }
        }

        foreach (var run in runs)
        {
            await run.Stop.CancelAsync().ConfigureAwait(false);
        }

        foreach (var run in runs)
        {
            try
            {
                await run.Task.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Already logged by the run; forgetting must finish.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        // The network's remembered tier and hidden state are kept as preferences: they go with the workspace too.
        await ForgetPreferencesAsync(workspace).ConfigureAwait(false);
    }

    /// <summary>A model is configured (whether its key works is known only by asking).</summary>
    private async Task<bool> HasModelAsync(CancellationToken cancellationToken) =>
        await _models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Recommendations may be shown: a model is configured and its key was not refused by the last recommendation request
    /// (§10.3 共同规则 "未配置模型或凭据失效时…不显示推荐"; review 2026-11-13). The workspace is remembered, so a change of
    /// model reloads its pages.
    /// </summary>
    private async Task<bool> UsableModelAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _shown.Add(workspace);
            if (_credentialsRefused)
            {
                return false;
            }
        }

        return await HasModelAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The model or its key was saved: a refused key may work now, and pages showing none look again.</summary>
    private void OnModelsChanged(object? sender, EventArgs e)
    {
        WorkspaceId[] shown;
        lock (_gate)
        {
            _credentialsRefused = false;
            shown = [.. _shown];
        }

        foreach (var workspace in shown)
        {
            Changed?.Invoke(this, workspace);
        }
    }

    private async Task<bool> HasContentAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        var listed = await _conversations.ListAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (listed.Any(c => c.Conversation.Title is not null))
        {
            return true;
        }

        return (await _store.ListBasesAsync(workspace, 0, 1, cancellationToken).ConfigureAwait(false)).Count > 0;
    }

    /// <summary>A background look at the workspace's recommendations of a kind, after <see cref="RecommendationOptions.Delay"/> (each call restarts the wait).</summary>
    private void Arm(WorkspaceId workspace, RecommendationKind kind, bool opened)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // A wait already armed by opening the page keeps its stronger reason.
            if (_timers.Remove((workspace, kind), out var old))
            {
                old.Dispose();
                opened |= kind == RecommendationKind.Question && _openedPending.Contains(workspace);
            }

            if (kind == RecommendationKind.Question)
            {
                if (opened)
                {
                    _openedPending.Add(workspace);
                }
                else
                {
                    _openedPending.Remove(workspace);
                }
            }

            var slot = new StrongBox<ITimer?>();
            slot.Value = _options.Time.CreateTimer(_ => Fire(workspace, kind, slot), null, _options.Delay, Timeout.InfiniteTimeSpan);
            _timers[(workspace, kind)] = slot.Value;
        }
    }

    private void Fire(WorkspaceId workspace, RecommendationKind kind, StrongBox<ITimer?> slot)
    {
        bool opened;
        lock (_gate)
        {
            if (!_timers.TryGetValue((workspace, kind), out var current) || !ReferenceEquals(current, slot.Value))
            {
                return;
            }

            _timers.Remove((workspace, kind));
            current.Dispose();
            opened = kind == RecommendationKind.Question && _openedPending.Remove(workspace);
            if (_disposed)
            {
                return;
            }
        }

        Track(kind == RecommendationKind.Question ? ConsiderAsync(workspace, opened) : ConsiderNodesAsync(workspace));
    }

    /// <summary>
    /// §10.3.1's background rules: new questions when none is pending (none stored, or all used or ignored), or when a
    /// summary was made after the last batch and 新探索 was opened again. Never twice for the same state in one run.
    /// </summary>
    private async Task ConsiderAsync(WorkspaceId workspace, bool opened)
    {
        if (_stop.IsCancellationRequested || _generation.IsClosed || _health.IsBlocked
            || !await _settings.GetQuestionsAsync(CancellationToken.None).ConfigureAwait(false)
            || !await _settings.GetAutoPrepareAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        var key = (workspace, RecommendationKind.Question);
        lock (_gate)
        {
            if (_running.ContainsKey(key))
            {
                return;
            }
        }

        var rows = await _store.ListAsync(workspace, RecommendationKind.Question, CancellationToken.None).ConfigureAwait(false);
        var pending = rows.Count(r => r.State == RecommendationState.Pending);
        var latest = rows.Count == 0 ? null : rows.MaxBy(r => r.GeneratedAt);
        var bases = await _store.ListBasesAsync(workspace, 0, 1, CancellationToken.None).ConfigureAwait(false);
        var newer = opened && latest is not null && bases.NewestCreatedAt is { } newest && newest > latest.GeneratedAt;
        if (pending > 0 && !newer)
        {
            return;
        }

        // Without a model nothing would be sent; not starting at all keeps the page from flashing "正在准备…".
        if (!await HasModelAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        var signature = $"{latest?.Generation}|{bases.Count}|{pending}";
        lock (_gate)
        {
            if (_disposed || _tried.GetValueOrDefault(key) == signature)
            {
                return;
            }

            _tried[key] = signature;
        }

        var outcome = await RunAsync(workspace, RecommendationKind.Question, ShownQuestions, manual: false, replace: true, planTier: null).ConfigureAwait(false);
        if (outcome.NothingSent)
        {
            // Nothing was asked (no model yet, nothing to draw on, storage paused…): a later look may ask.
            lock (_gate)
            {
                if (_tried.GetValueOrDefault(key) == signature)
                {
                    _tried.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// One request for the workspace's recommendations of <paramref name="kind"/>, after any other of the workspace. With
    /// <paramref name="replace"/> the pending ones go when the new ones are stored; with <paramref name="planTier"/> a
    /// network that got nodes remembers that it was filled for that tier from the request's start (§10.3.2).
    /// </summary>
    private Task<RecommendationOutcome> RunAsync(WorkspaceId workspace, RecommendationKind kind, int count, bool manual, bool replace, int? planTier)
    {
        Run run;
        Task previous;
        TaskCompletionSource<RecommendationOutcome> started;
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(RecommendationOutcome.Refused(RecommendationProblem.Cancelled));
            }

            if (_running.TryGetValue((workspace, kind), out var existing))
            {
                return existing.Task;
            }

            previous = _tails.GetValueOrDefault(workspace) ?? Task.CompletedTask;
            var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            started = new TaskCompletionSource<RecommendationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            run = new Run(started.Task, stop);
            _running[(workspace, kind)] = run;
            _failures.Remove((workspace, kind));
            _tails[workspace] = run.Task;
        }

        Track(Task.Run(() => ExecuteAfterAsync(previous, workspace, kind, count, manual, replace, planTier, run, started)));
        Changed?.Invoke(this, workspace);
        return run.Task;
    }

    private async Task ExecuteAfterAsync(Task previous, WorkspaceId workspace, RecommendationKind kind, int count, bool manual, bool replace, int? planTier, Run run, TaskCompletionSource<RecommendationOutcome> result)
    {
        RecommendationOutcome outcome;
        try
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The other request's outcome is its own; this one runs after it either way.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            outcome = await ExecuteAsync(workspace, kind, count, replace, planTier, run.Stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = RecommendationOutcome.Refused(RecommendationProblem.Cancelled);
        }
#pragma warning disable CA1031 // A failed write or read is a failed request: logged, shown quietly, retried by the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCrashed(_logger, ex);
            outcome = RecommendationOutcome.Refused(RecommendationProblem.Failed);
        }

        lock (_gate)
        {
            if (_running.TryGetValue((workspace, kind), out var current) && ReferenceEquals(current, run))
            {
                _running.Remove((workspace, kind));
            }

            if (_tails.TryGetValue(workspace, out var tail) && ReferenceEquals(tail, run.Task))
            {
                _tails.Remove(workspace);
            }

            if (!manual && outcome.ShownQuietly && !_disposed)
            {
                _failures[(workspace, kind)] = outcome;
            }

            // A refused key hides what is stored until a request gets through again or the model is saved.
            if (outcome.Problem == RecommendationProblem.Credentials)
            {
                _credentialsRefused = true;
            }
            else if (!outcome.NothingSent)
            {
                _credentialsRefused = false;
            }
        }

        if (outcome.Problem is RecommendationProblem.Failed or RecommendationProblem.Credentials)
        {
            LogFailed(_logger, kind, outcome.Problem.Value, outcome.Error);
        }

        result.TrySetResult(outcome);
        Changed?.Invoke(this, workspace);
    }

    private async Task<RecommendationOutcome> ExecuteAsync(WorkspaceId workspace, RecommendationKind kind, int count, bool replace, int? planTier, CancellationToken stop)
    {
        var start = _clock.UtcNow;
        var model = await _models.GetActiveSnapshotAsync(stop).ConfigureAwait(false);
        if (model is null)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.NotConfigured);
        }

        var listed = await _conversations.ListAsync(workspace, stop).ConfigureAwait(false);
        var bases = await _store.ListBasesAsync(workspace, RecommendationPrompt.MaxSummaries, RecommendationPrompt.BodyChars, stop).ConfigureAwait(false);
        var rows = await _store.ListAsync(workspace, kind, stop).ConfigureAwait(false);

        // Shown ones first (换一批 means different ones), then the used and ignored ones, newest first.
        var input = RecommendationPrompt.SelectInput(listed, bases.Listed, bases.Count, rows.Select(r => r.Question));
        if (input.IsEmpty)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.NoContent);
        }

        if (_health.IsBlocked)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.StoragePaused);
        }

        var ask = new RecommendationAsk(kind, count);
        string output;
        try
        {
            Interlocked.Increment(ref _sent);
            output = await _generation.RequestTransientAsync(
                RecommendationPrompt.Request(model, input, ask),
                stop,
                GenerationPriority.Idle,
                new RequestAttribution(workspace, kind == RecommendationKind.Question ? RequestAttribution.RecommendQuestions : RequestAttribution.RecommendNodes),
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

        // Not repeated: an existing conversation's title or first question, or anything shown, used or ignored before.
        var avoid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conversation in listed)
        {
            foreach (var text in new[] { conversation.Conversation.Title, conversation.FirstQuestion })
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    avoid.Add(RecommendationText.Normalize(text));
                }
            }
        }

        foreach (var row in rows)
        {
            avoid.Add(RecommendationText.Normalize(row.Question));
        }

        var items = RecommendationPrompt.Parse(output, input, ask, avoid);
        if (items.Count == 0)
        {
            return RecommendationOutcome.Refused(RecommendationProblem.Empty);
        }

        stop.ThrowIfCancellationRequested();
        var stored = await _store.AddBatchAsync(
            workspace,
            kind,
            new RecommendationBatch(Guid.CreateVersion7(), _clock.UtcNow, items),
            replacePending: replace,
            CancellationToken.None).ConfigureAwait(false);
        if (stored > 0 && planTier is { } tier)
        {
            await WritePlanAsync(workspace, new NodePlan(tier, start)).ConfigureAwait(false);
        }

        return stored switch
        {
            null => RecommendationOutcome.Refused(RecommendationProblem.Cancelled),
            0 => RecommendationOutcome.Refused(RecommendationProblem.Empty),
            { } added => new RecommendationOutcome(added),
        };
    }

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

    /// <summary>Stops waiting and any request running (the application is closing).</summary>
    public void Dispose()
    {
        List<ITimer> timers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timers = [.. _timers.Values];
            _timers.Clear();
        }

        foreach (var timer in timers)
        {
            timer.Dispose();
        }

        _models.Changed -= OnModelsChanged;

        // Cancelled, not disposed: a request ending meanwhile may still read the token.
        _stop.Cancel();
    }

    private sealed record Run(Task<RecommendationOutcome> Task, CancellationTokenSource Stop);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recommendations ({Kind}) not made: {Problem} {Error}")]
    private static partial void LogFailed(ILogger logger, RecommendationKind kind, RecommendationProblem problem, GenerationErrorKind? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recommendations stopped unexpectedly")]
    private static partial void LogCrashed(ILogger logger, Exception exception);
}
