using System.Globalization;
using System.Text;
using Mindweave.Application.Prompts;
using Mindweave.Application.Tools;
using Mindweave.Core.Runs;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Application.Conversations;

/// <summary>
/// The run instructions (docs/design/agent-loop.md L1, architecture §15.5), which follow the answer instructions
/// (<see cref="AnswerPrompt"/>) in the system turn that opens every request of a run: that only the user's question is an
/// instruction, what each offered tool is for, when to stop and how to search, which sources to prefer, how to cite and
/// what to say when something is missing; then the budget line (L2) or, for the last request, the instruction to answer
/// (R8). An answer without tools gets none of this. <see cref="Version"/> changes with the text, so evaluation reports can
/// be compared by it. The text is the prompt file "run" in the interface language (docs/design/bilingual-ui.md §5).
/// </summary>
public static class RunPrompt
{
    /// <summary>
    /// The version of the run instructions (the "run" prompt files and the tool lines below); raise it whenever the text
    /// changes. Writing the English text (docs/design/bilingual-ui.md §5) left the Chinese text, and so the version, as it was.
    /// Version 4 (reply quality, Q6): the stop, refine, search-term and source lines, and naming an attachment without a
    /// number; what the app is moved to the answer instructions.
    /// </summary>
    public const int Version = 4;

    /// <summary>What each built-in tool is for; a third-party tool is described by its own definition.</summary>
    private static string? UseOf(string tool) => tool switch
    {
        SearchAttachmentsTool.ToolName => T(
            "this conversation's attachments and material obtained earlier (including web pages read before): finds passages by keyword or reads a given part; use it when an attachment was not sent whole, or to find details not shown above.",
            "本会话的附件与较早取得的材料（含以前读过的网页），按关键词找片段或读取某一部分；附件没有整体发送、或要找上面没有的细节时使用。"),
        SearchSummariesTool.ToolName => T(
            "summaries the user saved before; use it first when the question is about something the user learned or organized before; what it finds takes precedence, with no need to check it again on the web.",
            "用户以前保存的总结；问到用户之前学过、整理过的内容时先用它，查到了就以它为准，不必再上网核实。"),
        WebSearchTool.ToolName => T(
            "use it for recent information, or information not in the local material; include the topic in the search terms.",
            "需要最新的、或本地材料里没有的信息时使用；检索词带上主题。"),
        FetchPageTool.ToolName => T(
            "when the snippets in the search results are not enough, reads one of their pages.",
            "搜索结果的摘要不够时，读取其中的网页。"),
        _ => null,
    };

    /// <summary>The instructions for a run that offers <paramref name="tools"/> (as frozen when the question was accepted).</summary>
    public static string For(IReadOnlyList<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var s = new StringBuilder();
        foreach (var tool in tools)
        {
            if (UseOf(tool.Name) is { } use)
            {
                s.Append(T($"\n   - {tool.Name} ({tool.DisplayName}): {use}", $"\n   - {tool.Name}（{tool.DisplayName}）：{use}"));
            }
        }

        var others = tools.Where(t => UseOf(t.Name) is null).Select(t => t.Name).ToList();
        if (others.Count > 0)
        {
            s.Append(T(
                $"\n   - {string.Join(", ", others)}: third-party tools the user added; use each as its own description says; a call with side effects needs the user's approval.",
                $"\n   - {string.Join("、", others)}：用户添加的第三方工具，按各自的说明使用；有副作用的调用要经用户批准。"));
        }

        return PromptLibrary.Get("run", ("tools", s.ToString()));
    }

    /// <summary>
    /// The budget line (L2) of a request that still offers tools: rounds with tools left including this one, and tool calls left.
    /// </summary>
    public static string BudgetLine(RunLimits limits, int requestsMade, int callsMade)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var rounds = Math.Max(1, limits.ModelRequests - 1 - requestsMade);
        var calls = Math.Max(0, limits.ToolCalls - callsMade);
        return T(
            $"[Allowance] Including this round, this answer can still call tools in {Plural(rounds, "round", "rounds")}, {Plural(calls, "call", "calls")} in all; once they are used up, you must answer directly.",
            string.Create(CultureInfo.InvariantCulture, $"【额度】包括这一轮，本次回答还可以调用工具 {rounds} 轮、共 {calls} 次；用完之后必须直接回答。"));
    }
}
