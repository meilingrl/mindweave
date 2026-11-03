using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>
/// An entry under an answer leading to a branch made from it; re-entering is navigation, not a new branch. An archived
/// branch keeps its entry, marked (D-021).
/// </summary>
public sealed record BranchEntry(ConversationId Child, string Quote, bool Archived = false)
{
    public string Label => UiText.Quoted(Quote) + (Archived ? " · " + UiText.ArchivedMarker : string.Empty);
}

/// <summary>
/// One message in a conversation page. A finished message shows its stored text; an unfinished answer is
/// bound to the coordinator's live answer, so it never shows text older than what has already arrived.
/// </summary>
public sealed partial class MessageItemViewModel : ViewModelBase, IDisposable
{
    private readonly LiveAnswer? _live;
    private readonly UiThrottle? _throttle;
    private readonly TimeProvider _time;

    public MessageItemViewModel(Message message, LiveAnswer? live, IUiDispatcher dispatcher, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        _time = time ?? TimeProvider.System;
        Id = message.Id;
        Role = message.Role;
        SentAt = message.Role == MessageRole.Assistant ? message.CompletedAt ?? message.CreatedAt : message.CreatedAt;
        StartedAt = message.CreatedAt;
        CompletedAt = message.CompletedAt;
        IsRetryAttempt = message.Supersedes is not null;
        Body = message.Text;
        State = message.State;
        ErrorKind = message.ErrorKind;
        ErrorDetail = message.ErrorDetail;

        if (live is not null && !message.IsFinished)
        {
            _live = live;
            _throttle = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(50), ApplyLive);
            _live.Changed += OnLiveChanged;
            ApplyLive();
        }
    }

    public MessageId Id { get; }

    public MessageRole Role { get; }

    public bool IsUser => Role == MessageRole.User;

    public bool IsAnswer => Role == MessageRole.Assistant;

    public bool IsRetryAttempt { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBody), nameof(CanBranch), nameof(IsWaiting), nameof(IsStreaming), nameof(CanCopy))]
    public partial string Body { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WaitingLabel))]
    public partial AnswerPhase Phase { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(IsInProgress), nameof(IsProblem), nameof(CanBranch), nameof(IsWaiting), nameof(IsStreaming))]
    [NotifyPropertyChangedFor(nameof(ShowsQuietState), nameof(CanRetryQuietly), nameof(CanRetryInNotice), nameof(HasActions), nameof(TimestampText))]
    [NotifyPropertyChangedFor(nameof(ElapsedText), nameof(DurationText))]
    public partial MessageState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(ShowsQuietState), nameof(HasActions))]
    public partial GenerationErrorKind? ErrorKind { get; private set; }

    [ObservableProperty]
    public partial string? ErrorDetail { get; private set; }

    /// <summary>Set by the page: whether this is the conversation's last answer and may be retried.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetryLabel), nameof(CanRetryQuietly), nameof(CanRetryInNotice), nameof(HasActions))]
    public partial bool CanRetry { get; set; }

    /// <summary>Set by the page while core storage is failing: a branch cannot be created now (§9.3).</summary>
    [ObservableProperty]
    public partial bool IsBranchPaused { get; set; }

    /// <summary>Set by the page: the conversation is hidden by an archive, so nothing new starts from this message.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBranch))]
    public partial bool IsArchived { get; set; }

    /// <summary>An older attempt that a later retry replaced; still readable, no longer sent as context.</summary>
    [ObservableProperty]
    public partial bool IsSuperseded { get; set; }

    public bool HasBody => Body.Length > 0;

    /// <summary>Bound to the coordinator's live answer rather than to stored text.</summary>
    internal bool IsLive => _live is not null;

    /// <summary>The request for this answer sent older context as a summary; the original messages are untouched.</summary>
    [ObservableProperty]
    public partial bool WasCompressed { get; set; }

    /// <summary>Branches made from this answer, oldest first.</summary>
    public ObservableCollection<BranchEntry> Branches { get; } = [];

    /// <summary>
    /// Only a finished answer whose text is saved can be branched from (§6.2). A live answer reaches a terminal
    /// state in memory slightly before storage; the page reloads from storage on finish, so this item is replaced.
    /// </summary>
    public bool CanBranch => IsAnswer && !IsArchived && _live is null && !IsInProgress && Body.Trim().Length > 0;

    public bool IsInProgress => State is MessageState.Pending or MessageState.Streaming;

    /// <summary>
    /// Running but no text yet. Only then is a waiting indicator shown: once text streams in, the growing text is the
    /// progress, and an indicator under every new paragraph only distracts.
    /// </summary>
    public bool IsWaiting => IsInProgress && !HasBody;

    public string WaitingLabel => Phase == AnswerPhase.Compressing ? UiText.CompressingContext : UiText.WaitingForAnswer;

    public bool IsProblem => State is MessageState.Failed or MessageState.Interrupted;

    public string? StateLabel => IsAnswer ? UiText.AnswerState(State, ErrorKind) : null;

    /// <summary>Text is arriving: a small in-answer mark says it is still being written (not a bar under each paragraph).</summary>
    public bool IsStreaming => IsInProgress && HasBody;

    /// <summary>A plain state (stopped, cancelled) shown quietly in the row under the answer; failures get a notice.</summary>
    public bool ShowsQuietState => StateLabel is not null && !IsProblem && !IsInProgress;

    /// <summary>Retry or regenerate in the quiet row under the answer.</summary>
    public bool CanRetryQuietly => CanRetry && !IsProblem;

    /// <summary>Retry next to the error, inside the failure notice.</summary>
    public bool CanRetryInNotice => CanRetry && IsProblem;

    /// <summary>
    /// The quiet row under a finished answer (copy, retry or regenerate, its state, its time). Hidden while it is
    /// still being written: the text is not final yet and the composer offers stop.
    /// </summary>
    public bool HasActions => IsAnswer && !IsInProgress;

    /// <summary>When the question was sent, or when the answer ended (its start while no end was recorded).</summary>
    public DateTimeOffset SentAt { get; }

    /// <summary>A quiet time beside the message: the clock today, the full date otherwise; none while an answer runs.</summary>
    public string? TimestampText => IsInProgress ? null : UiText.MessageTime(SentAt, _time.GetUtcNow());

    /// <summary>When the message was created: for an answer, the moment its question was sent.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>When the answer ended, as stored; null while it runs and for data written before this was recorded.</summary>
    public DateTimeOffset? CompletedAt { get; }

    /// <summary>
    /// A quiet live counter while an answer is pending or streaming ("12 秒"), counted from <see cref="StartedAt"/>.
    /// The page refreshes it once a second while it is shown (<see cref="RefreshElapsed"/>); null otherwise.
    /// </summary>
    public string? ElapsedText => IsAnswer && IsInProgress ? UiText.Elapsed(_time.GetUtcNow() - StartedAt) : null;

    /// <summary>
    /// A finished answer's total time ("用时 14 秒"), beside its time in the quiet row. Only for an answer that completed
    /// or was stopped and whose end is recorded; failed, interrupted and older answers show none.
    /// </summary>
    public string? DurationText => IsAnswer && State is MessageState.Complete or MessageState.Stopped && CompletedAt is { } end
        ? UiText.AnswerDuration(end - StartedAt)
        : null;

    /// <summary>Re-reads the clock for <see cref="ElapsedText"/>; the page calls it on its one-second tick.</summary>
    public void RefreshElapsed()
    {
        if (IsInProgress)
        {
            OnPropertyChanged(nameof(ElapsedText));
        }
    }

    /// <summary>The message's Markdown source can be copied once there is any; a failed or stopped partial answer included.</summary>
    public bool CanCopy => HasBody;

    public string RetryLabel => State == MessageState.Complete ? UiText.Regenerate : UiText.Retry;

    public void Dispose()
    {
        if (_live is not null)
        {
            _live.Changed -= OnLiveChanged;
        }
    }

    private void OnLiveChanged(object? sender, EventArgs e) => _throttle!.Request();

    private void ApplyLive()
    {
        var snapshot = _live!.Snapshot();
        Body = snapshot.Text;
        Phase = snapshot.Phase;
        State = snapshot.State;
        ErrorKind = snapshot.ErrorKind;
        ErrorDetail = snapshot.ErrorDetail;
    }
}
