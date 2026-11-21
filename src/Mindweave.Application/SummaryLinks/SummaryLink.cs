using Mindweave.Application.Languages;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.SummaryLinks;

public readonly record struct SummaryLinkId(Guid Value)
{
    public static SummaryLinkId New() => new(Guid.CreateVersion7());

    public static SummaryLinkId Parse(string text) => new(Guid.ParseExact(text, "D"));

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// How a link came to be: made by the user (requirements §10.3.3), suggested by AI and confirmed (§10.3.4), or made when a
/// conversation started from a recommendation was first summarized (§10.3.5; shown like a user's link, its note says so).
/// </summary>
public enum SummaryLinkOrigin
{
    User,
    Confirmed,
    Recommended,
}

/// <summary>
/// A content link between two summaries of one workspace (architecture "SummaryLink"). It has no direction: the pair is
/// kept in one order (<see cref="NodeA"/> before <see cref="NodeB"/>, see <see cref="SummaryLinkPair"/>). <see cref="Note"/>
/// is the short note, null when there is none.
/// </summary>
public sealed record SummaryLink(
    SummaryLinkId Id,
    WorkspaceId Workspace,
    SummaryNodeId NodeA,
    SummaryNodeId NodeB,
    string? Note,
    SummaryLinkOrigin Origin,
    DateTimeOffset CreatedAt)
{
    public bool Touches(SummaryNodeId node) => NodeA == node || NodeB == node;

    /// <summary>The summary at the other end, seen from <paramref name="node"/> (one of the two).</summary>
    public SummaryNodeId Other(SummaryNodeId node) => node == NodeA ? NodeB : NodeA;
}

/// <summary>One link as a summary lists it: the summary at the other end with its shown name.</summary>
public sealed record SummaryLinkEntry(SummaryLink Link, SummaryNodeId Other, string OtherName);

/// <summary>
/// Another summary of the workspace that "关联到…" offers: its shown name, and whether it is already linked to the summary
/// the link starts from (then it is listed but cannot be picked).
/// </summary>
public sealed record SummaryLinkCandidate(SummaryNodeId Node, string DisplayName, bool Linked);

/// <summary>The stored order of a pair: the smaller identity (as text, the way SQLite compares it) first.</summary>
public static class SummaryLinkPair
{
    public static (SummaryNodeId A, SummaryNodeId B) Normalize(SummaryNodeId first, SummaryNodeId second) =>
        string.CompareOrdinal(first.ToString(), second.ToString()) <= 0 ? (first, second) : (second, first);
}

/// <summary>The note's rules (requirements §10.3.3): optional, at most <see cref="MaxLength"/> characters once trimmed.</summary>
public static class SummaryLinkNote
{
    public const int MaxLength = 16;

    /// <summary>Trimmed, with an empty note as none (null).</summary>
    public static string? Normalize(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    /// <summary>Characters as SQLite's length() counts them (Unicode code points).</summary>
    public static int Length(string? note) => Normalize(note) is { } text ? text.EnumerateRunes().Count() : 0;

    public static bool IsTooLong(string? note) => Length(note) > MaxLength;

    /// <summary>
    /// The note of a link made from a recommendation (requirements §10.3.5); editable like any other. It is written in
    /// the interface language of the moment the link is made, within <see cref="MaxLength"/>; a stored note is the
    /// user's text from then on and is never rewritten.
    /// </summary>
    public static string FromRecommendation => Bilingual.T("Recommendation", "来自推荐");
}

/// <summary>Why a link was not made or changed.</summary>
public enum SummaryLinkRefusal
{
    /// <summary>A summary cannot be linked to itself.</summary>
    Self,

    /// <summary>The two summaries are in different workspaces (cross-workspace links are a later vision).</summary>
    OtherWorkspace,

    /// <summary>The pair is linked already, in either order.</summary>
    Duplicate,

    /// <summary>The note is longer than <see cref="SummaryLinkNote.MaxLength"/> characters.</summary>
    NoteTooLong,

    /// <summary>A summary or the link no longer exists.</summary>
    Missing,
}

/// <summary>A link use case refused; nothing was written.</summary>
public sealed class SummaryLinkRefusedException : Exception
{
    public SummaryLinkRefusedException()
        : this(SummaryLinkRefusal.Missing)
    {
    }

    public SummaryLinkRefusedException(SummaryLinkRefusal reason)
        : base($"The summary link was refused: {reason}.") => Reason = reason;

    public SummaryLinkRefusedException(string message)
        : base(message) => Reason = SummaryLinkRefusal.Missing;

    public SummaryLinkRefusedException(string message, Exception innerException)
        : base(message, innerException) => Reason = SummaryLinkRefusal.Missing;

    public SummaryLinkRefusal Reason { get; }
}
