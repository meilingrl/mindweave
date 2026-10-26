using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>An entry under an answer leading to a branch made from it; re-entering is navigation, not a new branch.</summary>
public sealed record BranchEntry(ConversationId Child, string Quote)
{
    public string Label => UiText.Quoted(Quote);
}

/// <summary>
/// One message in a conversation page. A finished message shows its stored text; an unfinished answer is
/// bound to the coordinator's live answer, so it never shows text older than what has already arrived.
/// </summary>
public sealed partial class MessageItemViewModel : ViewModelBase, IDisposable
{
    private readonly LiveAnswer? _live;
    private readonly UiThrottle? _throttle;

    public MessageItemViewModel(Message message, LiveAnswer? live, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(message);
        Id = message.Id;
        Role = message.Role;
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
    [NotifyPropertyChangedFor(nameof(HasBody), nameof(CanBranch))]
    public partial string Body { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(IsInProgress), nameof(IsProblem), nameof(CanBranch))]
    public partial MessageState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    public partial GenerationErrorKind? ErrorKind { get; private set; }

    [ObservableProperty]
    public partial string? ErrorDetail { get; private set; }

    /// <summary>Set by the page: whether this is the conversation's last answer and may be retried.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetryLabel))]
    public partial bool CanRetry { get; set; }

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
    public bool CanBranch => IsAnswer && _live is null && !IsInProgress && Body.Trim().Length > 0;

    public bool IsInProgress => State is MessageState.Pending or MessageState.Streaming;

    public bool IsProblem => State is MessageState.Failed or MessageState.Interrupted;

    public string? StateLabel => IsAnswer ? UiText.AnswerState(State, ErrorKind) : null;

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
        State = snapshot.State;
        ErrorKind = snapshot.ErrorKind;
        ErrorDetail = snapshot.ErrorDetail;
    }
}
