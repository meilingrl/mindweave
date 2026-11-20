using System.Text;
using Mindweave.Core.Conversations;
using Mindweave.Core.Runs;

namespace Mindweave.Application.Conversations;

/// <summary>What a running answer's current request is doing before its text arrives.</summary>
public enum AnswerPhase
{
    /// <summary>Waiting for the service (queued, connecting, or its first text not yet arrived).</summary>
    Waiting,

    /// <summary>Summarizing older context so the request fits (S3).</summary>
    Compressing,

    /// <summary>A reasoning model sent reasoning before any text of this request; shown live, never stored (v0.21).</summary>
    Thinking,

    /// <summary>A request of a run failed by chance and is tried once more (L5), until the new one sends something.</summary>
    Retrying,
}

/// <summary>
/// The one stage a running answer is in, as its status line says it (docs/design/reasoning-and-status.md). When several
/// hold, the first in this order wins: approval, tool, compressing, writing, thinking, retrying, finishing, waiting.
/// </summary>
public enum AnswerStage
{
    /// <summary>Queued, connecting, or the current request has sent nothing yet.</summary>
    Waiting,

    /// <summary>Summarizing older context before the request.</summary>
    Compressing,

    /// <summary>The current request is sending reasoning and no text yet.</summary>
    Thinking,

    /// <summary>The current request's text is streaming in.</summary>
    Writing,

    /// <summary>A tool call is running (<see cref="LiveAnswerSnapshot.ActiveTool"/> says which).</summary>
    Tool,

    /// <summary>A call waits for the user's approval (<see cref="LiveAnswerSnapshot.PendingApproval"/>).</summary>
    Approval,

    /// <summary>A request failed by chance and is being tried again.</summary>
    Retrying,

    /// <summary>The last request of a run (a limit was reached or a round was cut off) has sent nothing yet.</summary>
    Finishing,

    /// <summary>The answer has ended.</summary>
    Done,
}

/// <summary>
/// The in-memory, always-newest view of one answer while the application owns it. View models read it
/// instead of the database, so a page that is closed and reopened mid-stream never shows older text than
/// what has arrived. <see cref="Changed"/> is raised on worker threads.
/// </summary>
public sealed class LiveAnswer
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _text = new();
    private MessageState _state;
    private AnswerPhase _phase;
    private long _revision;
    private GenerationErrorKind? _errorKind;
    private string? _errorDetail;
    private readonly List<ContentBlock> _toolBlocks = [];
    private string? _activeTool;
    private RunLimit? _limitReached;
    private bool _newTurn;
    private PendingApproval? _approval;
    private TaskCompletionSource<bool>? _decision;
    private IReadOnlyList<CitationBlock> _citations = [];
    private readonly StringBuilder _reasoning = new();
    private bool _reasoningTurn;
    private bool _turnText;
    private bool _final;

    public LiveAnswer(MessageId messageId, ConversationId conversationId, string initialText = "", long initialRevision = 0)
    {
        MessageId = messageId;
        ConversationId = conversationId;
        _text.Append(initialText);
        _turnText = initialText.Length > 0;
        _revision = initialRevision;
        _state = MessageState.Pending;
    }

    public event EventHandler? Changed;

    public MessageId MessageId { get; }

    public ConversationId ConversationId { get; }

    public LiveAnswerSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new LiveAnswerSnapshot(_text.ToString(), _revision, _state, _errorKind, _errorDetail, _phase)
            {
                Stage = StageNow(),
                Reasoning = _reasoning.ToString(),
                ToolBlocks = [.. _toolBlocks],
                ActiveTool = _activeTool,
                LimitReached = _limitReached,
                PendingApproval = _approval,
                Citations = _citations,
            };
        }
    }

    internal void MarkStreaming() => Update(() => _state = MessageState.Streaming);

    internal void SetPhase(AnswerPhase phase) => Update(() => _phase = phase);

    /// <summary>The most reasoning kept (characters); older reasoning is dropped and the rest starts with "…".</summary>
    public const int MaxReasoning = 20_000;

    /// <summary>
    /// The service sent reasoning (shown live, never stored; requirements §10.4.4 v0.21). It is kept while the answer runs,
    /// a run's later requests after a blank line. Before any text of the current request, waiting or retrying becomes
    /// <see cref="AnswerPhase.Thinking"/>; any other phase is kept.
    /// </summary>
    internal void AppendReasoning(string delta)
    {
        if (delta.Length == 0)
        {
            return;
        }

        Update(() =>
        {
            if (_state is not (MessageState.Pending or MessageState.Streaming))
            {
                return;
            }

            if (_reasoningTurn && _reasoning.Length > 0)
            {
                _reasoning.Append("\n\n");
            }

            _reasoningTurn = false;
            _reasoning.Append(delta);
            if (_reasoning.Length > MaxReasoning)
            {
                _reasoning.Remove(0, _reasoning.Length - MaxReasoning).Insert(0, '…');
            }

            if (_phase is AnswerPhase.Waiting or AnswerPhase.Retrying && !_turnText)
            {
                _phase = AnswerPhase.Thinking;
            }
        });
    }

    internal void Append(string delta)
    {
        if (delta.Length == 0)
        {
            return;
        }

        Update(() =>
        {
            if (_state is MessageState.Pending or MessageState.Streaming)
            {
                // A later model reply of a run continues the text after a blank line (decision R1: one text block).
                if (_newTurn && _text.Length > 0)
                {
                    _text.Append("\n\n");
                }

                _newTurn = false;
                _turnText = true;
                if (_phase == AnswerPhase.Retrying)
                {
                    _phase = AnswerPhase.Waiting;
                }

                _text.Append(delta);
                _revision++;
            }
        });
    }

    /// <summary>
    /// A run's next model request begins (<paramref name="final"/>: the last one, without tools): its text and reasoning are
    /// separated from what came before, and the stage starts again from waiting (or stays retrying).
    /// </summary>
    internal void BeginTurn(bool final = false) => Update(() =>
    {
        _newTurn = true;
        _reasoningTurn = true;
        _turnText = false;
        _final = final;
        if (_phase != AnswerPhase.Retrying)
        {
            _phase = AnswerPhase.Waiting;
        }
    });

    /// <summary>A tool call is being run (<paramref name="tool"/>: its name), shown as the activity line says.</summary>
    internal void StartTool(ToolCallBlock call, string tool) => Update(() =>
    {
        if (_state is MessageState.Pending or MessageState.Streaming)
        {
            _toolBlocks.Add(call);
            _activeTool = tool;
            _phase = AnswerPhase.Waiting;
            _revision++;
        }
    });

    /// <summary>A tool call was not run (a limit) or could not be (an error before running): its call and its result together.</summary>
    internal long AddToolCall(ToolCallBlock call) => Mutate(() => _toolBlocks.Add(call));

    /// <summary>The result of the tool call last started (or added). Returns the revision the blocks then have.</summary>
    internal long AddToolResult(ToolResultBlock result) => Mutate(() =>
    {
        _toolBlocks.Add(result);
        _activeTool = null;
    });

    /// <summary>No tool is running any more (stopped while it ran).</summary>
    internal void EndTool() => Update(() => _activeTool = null);

    internal void SetLimitReached(RunLimit limit) => Update(() => _limitReached ??= limit);

    /// <summary>
    /// Shows an approval card for the call (phase 3, Q10) and returns the user's decision: true to allow. Only one card at a
    /// time; the run waits for it.
    /// </summary>
    internal Task<bool> RequestApproval(PendingApproval approval)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Update(() =>
        {
            _approval = approval;
            _decision = decision;
            _revision++;
        });
        return decision.Task;
    }

    /// <summary>The user allowed or refused the waiting call. False when no card waits for <paramref name="callId"/> (already decided, or gone).</summary>
    public bool Decide(string callId, bool allow)
    {
        TaskCompletionSource<bool>? decision = null;
        lock (_gate)
        {
            if (_approval is { } waiting && waiting.CallId == callId && _state is MessageState.Pending or MessageState.Streaming)
            {
                decision = _decision;
                _approval = null;
                _decision = null;
                _revision++;
            }
        }

        if (decision is null)
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        decision.TrySetResult(allow);
        return true;
    }

    /// <summary>The card is gone without a decision (the run was stopped while it waited).</summary>
    internal void WithdrawApproval() => Update(() =>
    {
        _approval = null;
        _decision = null;
    });

    /// <summary>The answer's 出处, set when it ends (requirements §10.4.5); stored after its tool blocks.</summary>
    internal void SetCitations(IReadOnlyList<CitationBlock> citations) => Mutate(() => _citations = citations);

    private long Mutate(Action change)
    {
        long revision = 0;
        Update(() =>
        {
            if (_state is MessageState.Pending or MessageState.Streaming)
            {
                change();
                _revision++;
            }

            revision = _revision;
        });
        return revision;
    }

    /// <summary>Terminal: later deltas are ignored. Returns the final revision to persist.</summary>
    internal long Finish(MessageState state, GenerationErrorKind? errorKind, string? errorDetail)
    {
        long revision = 0;
        Update(() =>
        {
            _state = state;
            _errorKind = errorKind;
            _errorDetail = errorDetail;
            _reasoning.Clear();
            _activeTool = null;
            revision = ++_revision;
        });
        return revision;
    }

    private AnswerStage StageNow() =>
        _state is not (MessageState.Pending or MessageState.Streaming) ? AnswerStage.Done
        : _approval is not null ? AnswerStage.Approval
        : _activeTool is not null ? AnswerStage.Tool
        : _phase == AnswerPhase.Compressing ? AnswerStage.Compressing
        : _turnText ? AnswerStage.Writing
        : _phase == AnswerPhase.Thinking ? AnswerStage.Thinking
        : _phase == AnswerPhase.Retrying ? AnswerStage.Retrying
        : _final ? AnswerStage.Finishing
        : AnswerStage.Waiting;

    private void Update(Action change)
    {
        lock (_gate)
        {
            change();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record LiveAnswerSnapshot(string Text, long Revision, MessageState State, GenerationErrorKind? ErrorKind, string? ErrorDetail, AnswerPhase Phase = AnswerPhase.Waiting)
{
    /// <summary>The stage the status line says (docs/design/reasoning-and-status.md).</summary>
    public AnswerStage Stage { get; init; }

    /// <summary>The reasoning received so far while the answer runs; empty once it ends. Never part of <see cref="Blocks"/>.</summary>
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>A run's tool calls and results so far, in step order.</summary>
    public IReadOnlyList<ContentBlock> ToolBlocks { get; init; } = [];

    /// <summary>The tool running now, by name; null when none is.</summary>
    public string? ActiveTool { get; init; }

    /// <summary>The limit that ended the run's use of tools, if one did.</summary>
    public RunLimit? LimitReached { get; init; }

    /// <summary>A tool call waiting for the user's approval (phase 3: reading an address the model wrote), shown as a card.</summary>
    public PendingApproval? PendingApproval { get; init; }

    /// <summary>The answer's 出处 once it ended (requirements §10.4.5).</summary>
    public IReadOnlyList<CitationBlock> Citations { get; init; } = [];

    /// <summary>The content as stored: the text block first (decision R1), then the run's tool blocks, then its 出处.</summary>
    public IReadOnlyList<ContentBlock> Blocks => ToolBlocks.Count == 0 && Citations.Count == 0
        ? ContentBlocks.FromText(Text)
        : [new TextBlock(Text), .. ToolBlocks, .. Citations];
}

/// <summary>
/// A tool call waiting for approval: the call, the tool, and what is asked (for 取网页, the full address; for a third-party
/// tool, its arguments as formatted JSON, with <see cref="Server"/> and the tool's own name in <see cref="ToolTitle"/>).
/// </summary>
public sealed record PendingApproval(string CallId, string Tool, string Target)
{
    /// <summary>The third-party server the call goes to (phase 4); null for a built-in tool.</summary>
    public string? Server { get; init; }

    /// <summary>The tool's name at its server (phase 4).</summary>
    public string? ToolTitle { get; init; }

    public bool IsThirdParty => Server is not null;
}
