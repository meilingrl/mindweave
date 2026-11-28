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
    /// changes, so evaluation reports can be compared by it. Version 0 is the time before they existed.
    /// </summary>
    public const int Version = 1;

    /// <summary>The answer instructions for a conversation that is a branch from a selected passage, or not.</summary>
    public static string For(bool branch) => Join(Fixed, BranchPart(branch));

    /// <summary>
    /// The part that is the same for every conversation: it opens the request (architecture §15.5, order of a request), so a
    /// provider's cache serves it to every question.
    /// </summary>
    public static string Fixed => PromptLibrary.Get("answer");

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
