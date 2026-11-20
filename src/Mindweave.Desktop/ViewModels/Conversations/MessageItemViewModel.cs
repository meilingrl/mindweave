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
/// <summary>
/// One row of the 出处 list under an answer (requirements §10.4.5): number, title, real address, when fetched; for the app's
/// own material (L11) 附件《…》、材料《…》 or 总结《…》 and where it is, without an address or a time.
/// </summary>
public sealed record CitationItem(CitationBlock Block, CommunityToolkit.Mvvm.Input.IRelayCommand Open)
{
    public string Number => $"[{Block.Number}]";

    public string Title => Block.Label;

    /// <summary>The real address; for local material, where it is (the row's tooltip either way).</summary>
    public string Address => Block.Url?.AbsoluteUri ?? UiText.CitationPlace(Block.Kind);

    public string Meta => Block.Url is { } url ? UiText.CitationMeta(url, Block.FetchedAt) : UiText.CitationPlace(Block.Kind);

    /// <summary>A web source: its address line and host with the time; local material shows only where it is.</summary>
    public bool HasAddress => Block.Url is not null;
}

public sealed partial class MessageItemViewModel : ViewModelBase, IDisposable
{
    private readonly LiveAnswer? _live;
    private readonly UiThrottle? _throttle;
    private readonly TimeProvider _time;
    private readonly Action<Core.Resources.ResourceId, string>? _openToolResult;
    private readonly Action<Core.Summaries.SummaryNodeId>? _openSummary;
    private readonly Action<CitationBlock>? _openCitation;
    private readonly Func<MessageId, string, bool, bool>? _decide;

    public MessageItemViewModel(
        Message message,
        LiveAnswer? live,
        IUiDispatcher dispatcher,
        TimeProvider? time = null,
        Action<AttachmentChip>? openAttachment = null,
        Action<Core.Resources.ResourceId, string>? openToolResult = null,
        Action<Core.Summaries.SummaryNodeId>? openSummary = null,
        Action<CitationBlock>? openCitation = null,
        Func<MessageId, string, bool, bool>? decide = null)
    {
        _openCitation = openCitation;
        _decide = decide;
        CitationBlocks = ContentBlocks.CitationsOf(message.Blocks);
        _openToolResult = openToolResult;
        _openSummary = openSummary;
        ToolBlocks = [.. ContentBlocks.ToolBlocksOf(message.Blocks)];
        ArgumentNullException.ThrowIfNull(message);
        _time = time ?? TimeProvider.System;
        Id = message.Id;
        Role = message.Role;
        SentAt = message.Role == MessageRole.Assistant ? message.CompletedAt ?? message.CreatedAt : message.CreatedAt;
        StartedAt = message.CreatedAt;
        CompletedAt = message.CompletedAt;
        IsRetryAttempt = message.Supersedes is not null;

        // A question shows its attachments as chips above the text it was typed with (requirements §10.4.3).
        Attachments = [.. ContentBlocks.AttachmentsOf(message.Blocks).Select(a => AttachmentChip.ForBlock(a, openAttachment))];
        Body = message.Role == MessageRole.User ? ContentBlocks.TypedTextOf(message.Blocks) : message.Text;
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

    /// <summary>A question's attachments, shown above its text; empty for an answer.</summary>
    public IReadOnlyList<AttachmentChip> Attachments { get; }

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>How this answer's request sent attachments it did not send whole (the reconciler sets it); null when all went whole.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachmentNote), nameof(AttachmentNote), nameof(AttachmentNoteDetails))]
    public partial IReadOnlyList<Application.Storage.ResourceInput>? AttachmentInputs { get; set; }

    public bool HasAttachmentNote => AttachmentInputs is { Count: > 0 };

    /// <summary>The light note under the answer: only parts, or only a summary, of an attachment went out.</summary>
    public string? AttachmentNote => AttachmentInputs is { Count: > 0 } inputs ? UiText.AttachmentNoteOf(inputs) : null;

    /// <summary>What went out of each attachment, one line each, shown when the note is expanded.</summary>
    public string? AttachmentNoteDetails => AttachmentInputs is { Count: > 0 } inputs ? string.Join("\n", inputs.Select(UiText.AttachmentInputLine)) : null;

    [ObservableProperty]
    public partial bool IsAttachmentNoteExpanded { get; set; }

    /// <summary>The run's tool calls and results (stored, or live while it runs), in step order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity), nameof(ActivityText), nameof(ActivitySteps), nameof(StatusText))]
    public partial IReadOnlyList<ContentBlock> ToolBlocks { get; private set; }

    /// <summary>The tool running now, while the run is under way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivitySteps), nameof(HasActivity), nameof(StatusText))]
    public partial string? ActiveTool { get; private set; }

    /// <summary>The stored run of this answer (the reconciler sets it): step times and states, the limit reached.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivitySteps), nameof(LimitNote), nameof(HasLimitNote))]
    public partial Application.Storage.RunRecord? Run { get; set; }

    /// <summary>The limit the live run reached (before the run is read back from storage).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LimitNote), nameof(HasLimitNote))]
    public partial Core.Runs.RunLimit? LiveLimit { get; private set; }

    /// <summary>The answer called tools (or is calling one): the quiet activity line shows above its text.</summary>
    public bool HasActivity => IsAnswer && (ToolBlocks.Count > 0 || ActiveTool is not null);

    /// <summary>
    /// The steps taken, "检索了 2 次附件 · 检索了 1 次总结网络", the running one included; what the run is doing now is said only
    /// in the status line (<see cref="StatusText"/>, requirements §10.4.4 v0.21).
    /// </summary>
    public string ActivityText => UiText.RunActivity(ToolBlocks.OfType<ToolCallBlock>().Select(c => (c.Tool, (string?)c.Summary)));

    /// <summary>A call waiting for the user's approval (requirements §10.4.5, Q10): the card in the answer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApproval), nameof(StatusText), nameof(IsThirdPartyApproval), nameof(ApprovalTitle))]
    [NotifyCanExecuteChangedFor(nameof(AllowCommand), nameof(DenyCommand))]
    public partial PendingApproval? PendingApproval { get; private set; }

    public bool HasApproval => PendingApproval is not null;

    /// <summary>The card asks about a third-party tool (phase 4): it shows the server, the tool and the arguments.</summary>
    public bool IsThirdPartyApproval => PendingApproval?.IsThirdParty == true;

    public string ApprovalTitle => IsThirdPartyApproval ? UiText.McpApprovalTitle : UiText.ApprovalTitle;

    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(HasApproval))]
    private void Allow() => Decide(true);

    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(HasApproval))]
    private void Deny() => Decide(false);

    private void Decide(bool allow)
    {
        if (PendingApproval is { } waiting)
        {
            _decide?.Invoke(Id, waiting.CallId, allow);
            PendingApproval = null;
        }
    }

    /// <summary>The answer's 出处 (stored, or live once it ended), in number order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCitations), nameof(Citations), nameof(CitationNumbers))]
    public partial IReadOnlyList<CitationBlock> CitationBlocks { get; private set; }

    public bool HasCitations => IsAnswer && CitationBlocks.Count > 0;

    /// <summary>The 出处 list under the answer.</summary>
    public IReadOnlyList<CitationItem> Citations => [.. CitationBlocks.Select(c => new CitationItem(c, new CommunityToolkit.Mvvm.Input.RelayCommand(() => _openCitation?.Invoke(c))))];

    /// <summary>The numbers the text may show as links: only those with a 出处 (a number naming nothing stays text).</summary>
    public IReadOnlySet<int> CitationNumbers => CitationBlocks.Select(c => c.Number).ToHashSet();

    /// <summary>Opens the viewer at 出处 <paramref name="number"/>; false when the answer has none of that number.</summary>
    public bool OpenCitation(int number)
    {
        var citation = CitationBlocks.FirstOrDefault(c => c.Number == number);
        if (citation is null || _openCitation is null)
        {
            return false;
        }

        _openCitation(citation);
        return true;
    }

    public IReadOnlyList<RunStepItem> ActivitySteps => RunStepItem.From(ToolBlocks, Run, ActiveTool, _openToolResult, _openSummary);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityToggleLabel))]
    public partial bool IsActivityExpanded { get; set; }

    public string ActivityToggleLabel => IsActivityExpanded ? UiText.RunStepsCollapse : UiText.RunStepsExpand;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleActivity() => IsActivityExpanded = !IsActivityExpanded;

    public bool HasLimitNote => LimitNote is not null;

    /// <summary>The note under an answer whose run reached a limit and answered from what it had.</summary>
    public string? LimitNote => (Run?.LimitReached ?? LiveLimit) is { } limit ? UiText.RunLimitNote(limit) : null;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleAttachmentNote() => IsAttachmentNoteExpanded = !IsAttachmentNoteExpanded;

    /// <summary>A question's bubble shows when it has typed text; a question of attachments only shows just its chips.</summary>
    public bool ShowsQuestionBubble => IsUser && !string.IsNullOrEmpty(Body);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBody), nameof(ShowsQuestionBubble), nameof(CanBranch), nameof(IsWaiting), nameof(IsStreaming), nameof(CanCopy))]
    public partial string Body { get; private set; }

    /// <summary>The stage the status line says (docs/design/reasoning-and-status.md); its motion follows from it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial AnswerStage Stage { get; private set; }

    /// <summary>The reasoning received so far while the answer runs (requirements §10.4.4 v0.21); empty otherwise. Never stored.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoning))]
    public partial string Reasoning { get; private set; } = string.Empty;

    /// <summary>The fold at the top of a running answer shows once reasoning has arrived; it goes when the answer ends.</summary>
    public bool HasReasoning => IsAnswer && IsInProgress && Reasoning.Length > 0;

    /// <summary>Closed at first; opened only for as long as this answer runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasoningToggleLabel))]
    public partial bool IsReasoningExpanded { get; set; }

    public string ReasoningToggleLabel => IsReasoningExpanded ? UiText.ReasoningCollapse : UiText.ReasoningExpand;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleReasoning() => IsReasoningExpanded = !IsReasoningExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(IsInProgress), nameof(IsProblem), nameof(CanBranch), nameof(IsWaiting), nameof(IsStreaming), nameof(HasReasoning))]
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

    /// <summary>One sentence for the current stage beside the weaving dots, from sending until the answer ends.</summary>
    public string StatusText => UiText.AnswerStatus(
        Stage, ActiveTool, ActiveTool is { } running ? ToolBlocks.OfType<ToolCallBlock>().LastOrDefault(c => c.Tool == running)?.Summary : null, PendingApproval);

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
        Stage = snapshot.Stage;
        Reasoning = snapshot.Reasoning;
        State = snapshot.State;
        ErrorKind = snapshot.ErrorKind;
        ErrorDetail = snapshot.ErrorDetail;
        if (!ToolBlocks.SequenceEqual(snapshot.ToolBlocks))
        {
            ToolBlocks = snapshot.ToolBlocks;
        }

        ActiveTool = snapshot.ActiveTool;
        LiveLimit = snapshot.LimitReached;
        PendingApproval = snapshot.PendingApproval;
        if (!CitationBlocks.SequenceEqual(snapshot.Citations))
        {
            CitationBlocks = snapshot.Citations;
        }
    }
}
