using Mindweave.Application.Prompts;

namespace Mindweave.Application.Conversations;

/// <summary>
/// The answer instructions (requirements §10.9, architecture §15.5, docs/design/reply-quality.md Q1, Q2, Q6b): the system turn
/// that opens every answer request, with or without tools, in the interface language. The base says what the app is and how
/// to explain, as defaults the user's question overrides; a branch adds its own part. An answer with tools continues with the
/// run instructions (<see cref="RunPrompt"/>). The texts are the prompt files "answer" and "answer-branch".
/// </summary>
public static class AnswerPrompt
{
    /// <summary>
    /// The version of the answer instructions (the "answer" and "answer-branch" prompt files); raise it whenever the text
    /// changes, so evaluation reports can be compared by it. Version 0 is the time before they existed. Version 2 (reply
    /// quality increment 2): the personal note and the titles of matching summaries ("answer-note", "answer-summaries").
    /// </summary>
    public const int Version = 2;

    /// <summary>The answer instructions for a conversation that is a branch from a selected passage, or not.</summary>
    public static string For(bool branch) => Join(Fixed, BranchPart(branch));

    /// <summary>
    /// The part that is the same for every conversation: it opens the request (architecture §15.5, order of a request), so a
    /// provider's cache serves it to every question.
    /// </summary>
    public static string Fixed => PromptLibrary.Get("answer");

    /// <summary>
    /// What opens every answer request: <see cref="Fixed"/>, then the personal note when there is one (architecture §15.5,
    /// order of a request). The note is the same for every conversation, so it stands at the start with the base.
    /// </summary>
    public static string Opening(string? personalNote) => Join(Fixed, NotePart(personalNote));

    /// <summary>
    /// The personal note (requirements §10.9), marked as the user's own preferences; empty when there is none. The summary
    /// prompt carries the same part.
    /// </summary>
    public static string NotePart(string? personalNote) =>
        string.IsNullOrWhiteSpace(personalNote) ? string.Empty : Filled("answer-note", ("note", personalNote.Trim()));

    /// <summary>
    /// The titles of matching summaries of the workspace (requirements §10.9), with the request to build on them; empty when
    /// there are none. They differ from question to question, so they stand with the question (<see cref="RequestNote"/>).
    /// </summary>
    public static string SummariesPart(IReadOnlyList<string> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var lines = titles.Select(t => t.ReplaceLineEndings(" ").Trim()).Where(t => t.Length > 0).Select(t => "- " + t).ToList();
        return lines.Count == 0 ? string.Empty : Filled("answer-summaries", ("titles", string.Join('\n', lines)));
    }

    /// <summary>The prompt filled in; empty when an evaluation edition leaves it out (an edition from before it existed).</summary>
    private static string Filled(string name, params (string Name, string Value)[] values) =>
        PromptLibrary.Get(name) is { Length: > 0 } template ? PromptLibrary.Fill(template, values) : string.Empty;

    /// <summary>The parts that belong to one request: the branch part and the titles of matching summaries.</summary>
    public static string RequestPart(bool branch, IReadOnlyList<string> titles) => Join(BranchPart(branch), SummariesPart(titles));

    /// <summary>
    /// The branch part, for a branch from a selected passage; empty otherwise. It differs between a branch and its parent, so it
    /// stands with the question (<see cref="RequestNote"/>), not at the start.
    /// </summary>
    public static string BranchPart(bool branch) => branch ? PromptLibrary.Get("answer-branch") : string.Empty;

    /// <summary>The system text of a run: the answer instructions, then the run instructions.</summary>
    public static string WithRun(string answer, string run) => Join(answer, run);

    /// <summary>The parts that are not empty, a blank line between them.</summary>
    private static string Join(params string[] parts) => string.Join("\n\n", parts.Where(p => p.Length > 0));
}
