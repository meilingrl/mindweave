using System.Text;
using Mindweave.Application.Ai;
using Mindweave.Core.Models;

namespace Mindweave.Infrastructure.Tests.RunEval;

/// <summary>
/// The optional judge (docs/design/reply-quality.md Q10): a model that gives two hints and never a pass or a fail. Which of
/// two answers explains more clearly, asked twice with the answers' places swapped; and whether each sentence that cites a
/// source is supported by the passage it cites. Its own requests and tokens are counted apart from the answers under test.
/// </summary>
internal sealed class EvalJudge(IChatClient client, ModelSnapshot model, string key)
{
    /// <summary>Longest answer the judge is shown.</summary>
    private const int AnswerLimit = 6_000;

    private const string ClarityInstructions = """
        You compare two answers to the same question, written for a person who is exploring and learning. Judge only which one
        explains more clearly for that person: the core idea first, terms explained when they first appear, a length that fits
        the question, and, in a branch, an explanation of the selected passage rather than a repeat of the earlier answer.
        Do not prefer an answer for being longer. Reply with one word only: A, B, or same.
        """;

    private const string SupportInstructions = """
        You check one sentence of an answer against the passage it cites. Reply with one word only: yes if the passage supports
        everything the sentence says, partly if it supports only some of it, no if it does not support it.
        """;

    public int Requests { get; private set; }

    public int PromptTokens { get; private set; }

    public int CompletionTokens { get; private set; }

    /// <summary>
    /// Which edition's answer the judge finds clearer: the same edition both times, "same" both times, or "inconsistent"
    /// when the two orders disagree (a judge is noisy; the hint says so rather than picking one).
    /// </summary>
    public async Task<EvalClarity> ClarityAsync(string question, string? quote, string first, string second, CancellationToken ct)
    {
        var straight = Pick(await AskAsync(ClarityInstructions, ClarityQuestion(question, quote, first, second), ct));
        var swapped = Pick(await AskAsync(ClarityInstructions, ClarityQuestion(question, quote, second, first), ct));

        // Straight: A is the first answer. Swapped: A is the second.
        var a = straight switch { "a" => EvalClarity.First, "b" => EvalClarity.Second, "same" => EvalClarity.Same, _ => EvalClarity.Unreadable };
        var b = swapped switch { "a" => EvalClarity.Second, "b" => EvalClarity.First, "same" => EvalClarity.Same, _ => EvalClarity.Unreadable };
        return a == EvalClarity.Unreadable || b == EvalClarity.Unreadable ? EvalClarity.Unreadable
            : a == b ? a
            : EvalClarity.Inconsistent;
    }

    /// <summary>How many of the cited sentences their passages support, in part, or not; a sentence naming no source counts as unsupported.</summary>
    public async Task<EvalSupport> SupportAsync(IReadOnlyList<EvalCited> cited, CancellationToken ct)
    {
        int yes = 0, partly = 0, no = 0, unreadable = 0;
        foreach (var c in cited)
        {
            if (c.Passage.Length == 0)
            {
                no++;
                continue;
            }

            switch (Pick(await AskAsync(SupportInstructions, $"Sentence:\n{c.Sentence}\n\nPassage:\n{c.Passage}", ct)))
            {
                case "yes":
                    yes++;
                    break;
                case "partly":
                    partly++;
                    break;
                case "no":
                    no++;
                    break;
                default:
                    unreadable++;
                    break;
            }
        }

        return new EvalSupport(yes, partly, no, unreadable);
    }

    private static string ClarityQuestion(string question, string? quote, string a, string b)
    {
        var s = new StringBuilder("Question:\n").Append(question).Append("\n\n");
        if (quote is { Length: > 0 })
        {
            s.Append("The question was asked in a branch about this passage of an earlier answer:\n").Append(quote).Append("\n\n");
        }

        return s.Append("Answer A:\n").Append(Cut(a)).Append("\n\nAnswer B:\n").Append(Cut(b)).ToString();
    }

    private static string Cut(string text) => text.Length > AnswerLimit ? text[..AnswerLimit] + "…" : text;

    /// <summary>The verdict's first word, lower case, without punctuation.</summary>
    internal static string Pick(string reply)
    {
        var word = reply.Trim().Split((char[])[' ', '\n', '\t', '.', ',', '，', '。', ':', '：'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return word.Trim('*', '"', '\'', '“', '”').ToLowerInvariant();
    }

    private async Task<string> AskAsync(string instructions, string question, CancellationToken ct)
    {
        var request = new ChatRequest(model with { MaxOutputTokens = 2_048 }, [new ChatTurn(ChatRole.System, instructions), new ChatTurn(ChatRole.User, question)]);
        var text = new StringBuilder();
        Requests++;
        await foreach (var item in client.StreamAsync(request, key, ct))
        {
            switch (item)
            {
                case ChatStreamEvent.TextDelta delta:
                    text.Append(delta.Text);
                    break;
                case ChatStreamEvent.Usage usage:
                    PromptTokens += usage.PromptTokens ?? 0;
                    CompletionTokens += usage.CompletionTokens ?? 0;
                    break;
            }
        }

        return text.ToString();
    }
}

/// <summary>Which of two answers the judge found clearer.</summary>
internal enum EvalClarity
{
    First,
    Second,
    Same,
    Inconsistent,
    Unreadable,
}

/// <summary>The judge's reading of the cited sentences of one answer.</summary>
internal sealed record EvalSupport(int Yes, int Partly, int No, int Unreadable)
{
    public int Total => Yes + Partly + No + Unreadable;
}

/// <summary>The judge's hints on one scenario: clarity (previous against current) and the support of each answer's citations.</summary>
internal sealed record EvalJudgement(EvalClarity? Clarity, EvalSupport? Previous, EvalSupport? Current);
