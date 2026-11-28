using Mindweave.Application.Ai;

namespace Mindweave.Application.Conversations;

/// <summary>
/// What belongs to one request only (requirements §10.11.4, architecture §15.5 "Order of a request"): the branch part of the
/// answer instructions, the index of earlier material, the remaining allowance or the instruction to answer. A request stands in
/// a fixed order so a provider's cache can serve what was sent before: the instructions that are the same for every conversation
/// open it, then the ancestors and the conversation so far, and these parts come last, with the current question, marked as the
/// app's own lines. Some services accept a system turn only at the start, so they always travel in a user turn: before the
/// question, or as a turn of their own after tool results. Their wording is not changed by where they stand.
/// </summary>
public static class RequestNote
{
    /// <summary>Opens the app's own lines in a user turn.</summary>
    public const string Open = "<app-notes>";

    /// <summary>Closes them; what follows in the same turn is the user's.</summary>
    public const string Close = "</app-notes>";

    /// <summary>The parts that are not empty, marked as the app's; empty when there are none.</summary>
    public static string Of(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
        return kept.Count == 0 ? string.Empty : Open + "\n" + string.Join("\n\n", kept) + "\n" + Close;
    }

    /// <summary>
    /// <paramref name="turns"/> with <paramref name="note"/> last: before the text of a last user turn (the question), or as a
    /// user turn of its own after anything else. Unchanged when the note is empty.
    /// </summary>
    public static IReadOnlyList<ChatTurn> Append(IReadOnlyList<ChatTurn> turns, string note)
    {
        ArgumentNullException.ThrowIfNull(turns);
        if (note.Length == 0)
        {
            return turns;
        }

        var noted = new List<ChatTurn>(turns);
        AppendTo(noted, note);
        return noted;
    }

    /// <summary>As <see cref="Append"/>, in place: a run keeps the note it sent once the model has replied to it.</summary>
    internal static void AppendTo(List<ChatTurn> turns, string note)
    {
        if (note.Length == 0)
        {
            return;
        }

        if (turns.Count > 0 && turns[^1].Role == ChatRole.User)
        {
            turns[^1] = turns[^1] with { Content = note + "\n\n" + turns[^1].Content };
        }
        else
        {
            turns.Add(new ChatTurn(ChatRole.User, note));
        }
    }

    /// <summary>The text of a turn without a note it opens with: what the user, or the material, says.</summary>
    public static string Without(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.StartsWith(Open, StringComparison.Ordinal) || content.IndexOf(Close, StringComparison.Ordinal) is not (var end and >= 0))
        {
            return content;
        }

        return content[(end + Close.Length)..].TrimStart('\n');
    }
}
