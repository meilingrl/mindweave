using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>Something a step found that can be opened: a summary, or (for a tool result) the stored result.</summary>
public sealed record RunStepLink(string Label, IRelayCommand Open);

/// <summary>
/// One tool step as the expanded activity line shows it (requirements §10.4.4): the tool and its input, the result (or the
/// error), its time and state, and links to what it found. Not a branch source (architecture §15.9).
/// </summary>
public sealed record RunStepItem(string Title, string Outcome, string Meta, bool IsProblem, IReadOnlyList<RunStepLink> Links)
{
    public bool HasLinks => Links.Count > 0;

    /// <summary>A failed step as the failure block (D2 §2.1): its error, then when, how long and where, to copy.</summary>
    public FailureViewModel? Failure { get; init; }

    public bool HasFailure => Failure is not null;

    /// <summary>The plain outcome line: a result, a refusal, or nothing when the failure block says it.</summary>
    public bool ShowsOutcome => Failure is null && !string.IsNullOrEmpty(Outcome);

    /// <summary>
    /// The steps of an answer from its blocks (calls paired with their results by call ID) and, when known, its stored
    /// steps for times and states. <paramref name="openResult"/> opens a stored result; <paramref name="openSummary"/> a summary.
    /// </summary>
    internal static IReadOnlyList<RunStepItem> From(
        IReadOnlyList<ContentBlock> toolBlocks, RunRecord? run, string? activeTool, Action<ResourceId, string>? openResult, Action<SummaryNodeId>? openSummary)
    {
        var results = toolBlocks.OfType<ToolResultBlock>().GroupBy(r => r.CallId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var steps = run?.Steps.Where(s => s.Kind == RunStepKind.Tool && s.CallId is not null).GroupBy(s => s.CallId!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var calls = toolBlocks.OfType<ToolCallBlock>().ToList();
        var items = new List<RunStepItem>();
        for (var i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            var result = results.GetValueOrDefault(call.CallId);
            var step = steps?.GetValueOrDefault(call.CallId);
            var running = result is null && activeTool is not null && i == calls.Count - 1;
            var state = result?.Status switch
            {
                ToolResultStatus.Succeeded => UiText.RunStepDone,
                ToolResultStatus.Skipped => UiText.RunStepSkipped,
                ToolResultStatus.Failed => step?.State == RunStepState.Unknown ? UiText.RunStepUnknown : UiText.RunStepFailed,
                ToolResultStatus.Denied => UiText.RunStepDenied,
                _ when running => UiText.RunStepRunning,
                _ => step?.State == RunStepState.Unknown ? UiText.RunStepUnknown
                    : step?.Approval == Approval.Pending ? UiText.ApprovalExpired
                    : UiText.RunStepStopped,
            };
            var outcome = result is null ? string.Empty
                : result.Status == ToolResultStatus.Succeeded ? result.Detail ?? string.Empty
                : result.Preview;
            if (step?.Approval == Approval.Approved)
            {
                // A call the user allowed says so (requirements §10.4.6).
                state += " · " + UiText.RunStepApproved;
            }

            var meta = step?.Duration is { } took ? $"{UiText.StepDuration(took)} · {state}" : state;
            var links = new List<RunStepLink>();
            if (result?.Resource is { } stored && openResult is not null)
            {
                links.Add(new RunStepLink(UiText.OpenToolResult, new RelayCommand(() => openResult(stored, call.Summary))));
            }

            foreach (var finding in result?.Findings ?? [])
            {
                if (finding.Kind == ToolFindingKind.Summary && openSummary is not null && Guid.TryParse(finding.Id, out var node))
                {
                    links.Add(new RunStepLink(finding.Title, new RelayCommand(() => openSummary(new SummaryNodeId(node)))));
                }
                else if (finding.Kind == ToolFindingKind.WebSource && result?.Resource is { } source && openResult is not null)
                {
                    // A search result or page: its number and title open the stored result.
                    links.Add(new RunStepLink($"[{finding.Part}] {finding.Title}", new RelayCommand(() => openResult(source, call.Summary))));
                }
            }

            items.Add(new RunStepItem(call.Summary, outcome, meta, result?.Status is ToolResultStatus.Failed or ToolResultStatus.Denied, links)
            {
                Failure = result?.Status == ToolResultStatus.Failed ? StepFailure(outcome, step) : null,
            });
        }

        return items;
    }

    private static FailureViewModel StepFailure(string outcome, RunStepRecord? step)
    {
        var detail = step?.ErrorDetail is { } text && text != outcome ? text : null;
        return new FailureViewModel(
            outcome.Length > 0 ? outcome : UiText.RunStepFailed,
            details: step is null ? null : new Application.Diagnostics.FailureDetails(step.FinishedAt ?? step.StartedAt, null, null, null, step.Duration, detail),
            extra: step?.Target is { } host ? [new FailureLine(UiText.FailureHostLabel, host)] : null);
    }
}
