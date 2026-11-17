namespace Mindweave.Core.Runs;

/// <summary>
/// How much a tool can affect (architecture §15.7). Local reads are never asked about; network reads follow their switch;
/// side effects are approved one call at a time (phase 4).
/// </summary>
public enum ToolRisk
{
    /// <summary>Only reads this computer's Mindweave data.</summary>
    LocalRead,

    /// <summary>Asks an outside service without changing anything there (phase 3).</summary>
    NetworkRead,

    /// <summary>May change state outside Mindweave; every call is approved first (phase 4).</summary>
    SideEffect,
}

/// <summary>A step of a run (architecture §15.3): one model request, or one tool execution.</summary>
public enum RunStepKind
{
    Model,
    Tool,
}

/// <summary>Where a run step is. <see cref="Unknown"/>: a side effect interrupted while it ran, its outcome not known.</summary>
public enum RunStepState
{
    Running,
    Completed,
    Failed,
    Stopped,
    Denied,
    AwaitingApproval,
    Unknown,
}

/// <summary>How a tool call ended, as its result block records it.</summary>
public enum ToolResultStatus
{
    /// <summary>The tool ran and returned a result.</summary>
    Succeeded,

    /// <summary>The call could not be run or the tool failed (bad arguments, unknown tool, a timeout); the error went back to the model.</summary>
    Failed,

    /// <summary>The user refused the call (phase 4).</summary>
    Denied,

    /// <summary>Not run: the run had reached its tool-call limit, or the run was stopped first.</summary>
    Skipped,
}

/// <summary>Which limit ended a run's use of tools (requirements §10.4.4).</summary>
public enum RunLimit
{
    ModelRequests,
    ToolCalls,
    Duration,
    Tokens,
}

/// <summary>
/// A run's limits (requirements §10.4.4, Q9 approved 2026-11-17): model requests (the last of which offers no tools), tool
/// calls, wall-clock duration and the run's accumulated estimated tokens (input plus output of every request). Frozen into
/// the task when it is accepted.
/// </summary>
public sealed record RunLimits(int ModelRequests, int ToolCalls, TimeSpan Duration, int Tokens)
{
    public static RunLimits Default { get; } = new(8, 16, TimeSpan.FromMinutes(5), 200_000);

    public const int MinModelRequests = 2;
    public const int MaxModelRequests = 32;
    public const int MinToolCalls = 1;
    public const int MaxToolCalls = 64;
    public const int MinMinutes = 1;
    public const int MaxMinutes = 30;
    public const int MinTokens = 20_000;
    public const int MaxTokens = 2_000_000;

    /// <summary>The time the last request (without tools) is given beyond <see cref="Duration"/> (decision R6).</summary>
    public static TimeSpan FinalRequestGrace { get; } = TimeSpan.FromMinutes(1);

    /// <summary>Each value brought into its allowed range (decision R9).</summary>
    public RunLimits Clamped() => new(
        Math.Clamp(ModelRequests, MinModelRequests, MaxModelRequests),
        Math.Clamp(ToolCalls, MinToolCalls, MaxToolCalls),
        TimeSpan.FromMinutes(Math.Clamp(Math.Round(Duration.TotalMinutes), MinMinutes, MaxMinutes)),
        Math.Clamp(Tokens, MinTokens, MaxTokens));
}

/// <summary>What a tool result points at, for step details: a summary node, or a part of a resource.</summary>
public enum ToolFindingKind
{
    Summary,
    ResourcePart,
}

/// <summary>
/// One thing a tool result found: its kind, the identity of what it names (a summary node ID, or a resource ID), the part
/// (1-based, for a resource part), and a title to show.
/// </summary>
public sealed record ToolFinding(ToolFindingKind Kind, string Id, string Title, int? Part = null);

/// <summary>
/// The web search switch beside the composer (requirements §10.4.5): the model decides (<see cref="Auto"/>), at least one
/// search for this question (<see cref="On"/>), or no web tools (<see cref="Off"/>).
/// </summary>
public enum WebSearchMode
{
    Auto,
    On,
    Off,
}
