using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Infrastructure.Tests.Chat;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.RunEval;

/// <summary>One question of a scenario, with the web switch's position; the first turn may carry the arranged attachments.</summary>
internal sealed record EvalTurn(string Question, WebSearchMode Mode = WebSearchMode.Auto, bool WithAttachments = false);

/// <summary>What a scenario put in place before its first question.</summary>
internal sealed record EvalSetup(IReadOnlyList<ResourceId> Attachments)
{
    public static EvalSetup None { get; } = new([]);
}

/// <summary>
/// One evaluation scenario (docs/design/agent-loop.md §4, L12): a fixed world (attachments, summaries, web pages), one or more
/// questions, and what a good run of the last one looks like. <see cref="Script"/> is a well-behaved model's run, used to check
/// the scenario itself with the fake model; a real model is measured against <see cref="Expect"/>.
/// </summary>
internal sealed record EvalScenario(string Id, string Category, string Purpose, IReadOnlyList<EvalTurn> Turns, EvalExpectation Expect)
{
    public Func<ChatHarness, Task<EvalSetup>>? Arrange { get; init; }

    public Action<FakeChatClient> Script { get; init; } = _ => { };

    /// <summary>Run limits other than the defaults (设置 › 工具).</summary>
    public RunLimits? Limits { get; init; }

    /// <summary>The model's declared context window, to put the context under pressure.</summary>
    public int? ContextWindow { get; init; }

    /// <summary>
    /// Why no model can pass this scenario yet (the improvement that would let it, docs/design/agent-loop.md §3); the scripted
    /// check then expects a failed check, so the note is removed once the improvement lands.
    /// </summary>
    public string? KnownGap { get; init; }
}

/// <summary>What a good run of a scenario's last question does. Every check is reported; none stops the evaluation.</summary>
internal sealed record EvalExpectation
{
    /// <summary>Each group: the answer contains at least one of its words.</summary>
    public IReadOnlyList<string[]> Contains { get; init; } = [];

    /// <summary>The answer contains none of these.</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>Each of these tools is called at least once.</summary>
    public IReadOnlyList<string> UsesTools { get; init; } = [];

    /// <summary>None of these tools is called.</summary>
    public IReadOnlyList<string> AvoidsTools { get; init; } = [];

    /// <summary>No tool is called at all.</summary>
    public bool NoTools { get; init; }

    /// <summary>The answer cites at least one source, and every [n] it writes names one.</summary>
    public bool Cites { get; init; }

    /// <summary>Hosts the model must not try to read (an attempt shows as an approval card, which the evaluation refuses).</summary>
    public IReadOnlyList<string> NeverAsksFor { get; init; } = [];

    public int? MaxToolCalls { get; init; }

    /// <summary>Checks of their own, on what was observed.</summary>
    public IReadOnlyList<Func<EvalObservation, EvalCheck>> Extra { get; init; } = [];
}

internal sealed record EvalCheck(string Name, bool Passed, string Detail);

/// <summary>One tool call of the measured run, as the model made it.</summary>
internal sealed record EvalCall(string Tool, string Arguments, string? Input, RunStepState State, string? Approval);

/// <summary>One model request of the measured answer task (the answer's own and any summaries made for it).</summary>
internal sealed record EvalRequest(string? Purpose, string Outcome, int? EstimatedTokens, int? PromptTokens, int? CompletionTokens, long Milliseconds);

/// <summary>What was observed of a scenario's last answer, and of the scenario as a whole.</summary>
internal sealed record EvalObservation(
    Message Answer,
    RunRecord? Run,
    IReadOnlyList<EvalCall> Calls,
    IReadOnlyList<EvalRequest> Requests,
    IReadOnlyList<string> ApprovalsAsked,
    IReadOnlyList<string> Searches,
    IReadOnlyList<string> Fetched,
    TimeSpan Elapsed,
    int ScenarioRequests,
    bool TimedOut);

/// <summary>A scenario's outcome: what was observed, the measures taken from it, and its checks. <see cref="Failure"/>: it could not run.</summary>
internal sealed record EvalResult(EvalScenario Scenario, EvalObservation? Observation, IReadOnlyList<EvalCheck> Checks, string? Failure)
{
    public EvalMetrics? Metrics => Observation is { } o ? EvalMetrics.Of(o) : null;

    public int Passed => Checks.Count(c => c.Passed);
}

/// <summary>The numbers of one answer (docs/design/agent-loop.md §4.2), all taken from the stored records.</summary>
internal sealed record EvalMetrics(
    string State,
    int ModelRequests,
    int ToolCalls,
    IReadOnlyDictionary<string, int> ByTool,
    int Repeated,
    int FailedCalls,
    int Approvals,
    string? LimitReached,
    int CitedNumbers,
    int ValidCitations,
    int EstimatedTokens,
    int? PromptTokens,
    int? CompletionTokens,
    double Seconds)
{
    public static EvalMetrics Of(EvalObservation o)
    {
        var numbers = CitationMarkers.Find(o.Answer.Text).Select(m => m.Number).Distinct().ToList();
        var known = ContentBlocks.CitationsOf(o.Answer.Blocks).Select(c => c.Number).ToHashSet();
        var answerRequests = o.Requests.Where(r => r.Purpose is null or "answer").ToList();
        int? Sum(Func<EvalRequest, int?> pick) => o.Requests.All(r => pick(r) is null) ? null : o.Requests.Sum(r => pick(r) ?? 0);
        return new EvalMetrics(
            o.TimedOut ? "timed out" : o.Answer.State.ToString(),
            o.Run?.Steps.Count(s => s.Kind == RunStepKind.Model) ?? answerRequests.Count,
            o.Calls.Count,
            o.Calls.GroupBy(c => c.Tool).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            o.Calls.Where(c => c.State != RunStepState.Denied).GroupBy(c => (c.Tool, EvalJson.Normalize(c.Arguments))).Sum(g => g.Count() - 1),
            o.Calls.Count(c => c.State == RunStepState.Failed),
            o.ApprovalsAsked.Count,
            o.Run?.LimitReached?.ToString(),
            numbers.Count,
            numbers.Count(known.Contains),
            o.Requests.Sum(r => r.EstimatedTokens ?? 0),
            Sum(r => r.PromptTokens),
            Sum(r => r.CompletionTokens),
            o.Elapsed.TotalSeconds);
    }
}
