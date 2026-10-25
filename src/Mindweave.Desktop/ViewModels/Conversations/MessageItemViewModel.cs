using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Conversations;

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
        Body = message.Body;
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
    [NotifyPropertyChangedFor(nameof(HasBody))]
    public partial string Body { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(IsInProgress), nameof(IsProblem))]
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
