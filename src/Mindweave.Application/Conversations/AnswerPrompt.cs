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
    public static string For(bool branch) => Join(PromptLibrary.Get("answer"), branch ? PromptLibrary.Get("answer-branch") : string.Empty);

    /// <summary>The system text of a run: the answer instructions, then the run instructions.</summary>
    public static string WithRun(string answer, string run) => Join(answer, run);

    /// <summary>The parts that are not empty, a blank line between them.</summary>
    private static string Join(params string[] parts) => string.Join("\n\n", parts.Where(p => p.Length > 0));
}
