using System.Reflection;
using System.Text.Json;
using Mindweave.Application.Conversations;
using Mindweave.Application.Languages;
using Mindweave.Application.Prompts;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Summaries;
using Mindweave.Application.SummaryLinks;
using Mindweave.Application.Tools;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Tests.Languages;

/// <summary>
/// The long prompts live in one file per language (docs/design/bilingual-ui.md §5, plan Task 14): both languages have every
/// prompt with the same placeholders, each keeps its rule on the output language, the Chinese prompts are what the app
/// sent before, and the model's output is read with either language's markers.
/// </summary>
[Collection(LanguageTests.Name)]
public class PromptLibraryTests
{
    /// <summary>
    /// Where each prompt says which language to write in, in each language. Null: that language's prompt has no such rule
    /// (the Chinese run instructions never had one, nor did the Chinese naming prompt; the branch background is not an
    /// instruction to write anything).
    /// </summary>
    private static readonly Dictionary<string, (string? English, string? Chinese)> LanguageRules = new(StringComparer.Ordinal)
    {
        ["summary"] = ("Use the language of the conversation.", "使用对话所用的语言。"),
        ["summary-branch-background"] = (null, null),
        ["summary-naming"] = ("in the language of the summaries", null),
        ["compression"] = ("In the language of the conversation", "请用对话所用的语言"),
        ["title"] = ("use the language of the question", "使用提问所用的语言"),
        ["attachment-digest"] = ("In the language of the material", "请用材料所用的语言"),
        ["attachment-digest-merge"] = ("In the language of the material", "用材料所用的语言"),
        ["run"] = ("Answer in the language of the user's question.", null),
        ["recommendations"] = ("Use the language most of these titles are written in.", "使用这些标题主要使用的语言。"),
        ["link-suggestions"] = ("Use the language most of these titles are written in.", "使用这些标题主要使用的语言。"),
        ["workspace-names"] = ("use the language most of these titles are written in.", "使用这些标题主要使用的语言。"),
    };

    private static readonly RecommendationSource[] Sources =
    [
        new("S1", SummaryNodeId.New(), "蒸汽机", "蒸汽机的原理", "瓦特改良了冷凝器"),
        new("S2", SummaryNodeId.New(), "火车", "火车", string.Empty),
    ];

    [Fact]
    public void Every_prompt_exists_in_both_languages()
    {
        Assert.Equal(PromptLibrary.Names.Order(StringComparer.Ordinal), PromptLibrary.Embedded(UiLanguage.English));
        Assert.Equal(PromptLibrary.Names.Order(StringComparer.Ordinal), PromptLibrary.Embedded(UiLanguage.Chinese));
        foreach (var name in PromptLibrary.Names)
        {
            Assert.False(string.IsNullOrWhiteSpace(PromptLibrary.Get(name, UiLanguage.English)), name);
            Assert.False(string.IsNullOrWhiteSpace(PromptLibrary.Get(name, UiLanguage.Chinese)), name);
            Assert.DoesNotContain('\r', PromptLibrary.Get(name, UiLanguage.English));
            Assert.False(PromptLibrary.Get(name, UiLanguage.English).EndsWith('\n'), name);
        }
    }

    [Fact]
    public void Placeholders_match()
    {
        foreach (var name in PromptLibrary.Names)
        {
            Assert.Equal(
                PromptLibrary.PlaceholdersOf(PromptLibrary.Get(name, UiLanguage.Chinese)).Order(StringComparer.Ordinal),
                PromptLibrary.PlaceholdersOf(PromptLibrary.Get(name, UiLanguage.English)).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Each_prompt_keeps_the_output_language_rule()
    {
        Assert.Equal(PromptLibrary.Names.Order(StringComparer.Ordinal), LanguageRules.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, (english, chinese)) in LanguageRules)
        {
            var en = PromptLibrary.Get(name, UiLanguage.English);
            var zh = PromptLibrary.Get(name, UiLanguage.Chinese);
            if (english is not null)
            {
                // The language is that of what the model reads (conversation, question, material, or the titles and
                // summaries it is given), never the interface's.
                Assert.Contains(english, en, StringComparison.Ordinal);
                Assert.Contains("language", english, StringComparison.Ordinal);
                Assert.True(
                    new[] { "conversation", "question", "material", "titles", "summaries" }.Any(s => english.Contains(s, StringComparison.Ordinal)),
                    $"{name}: the English rule names nothing the model reads.");
            }

            if (chinese is not null)
            {
                Assert.Contains(chinese, zh, StringComparison.Ordinal);
                Assert.True(zh.Contains("所用的语言", StringComparison.Ordinal) || zh.Contains("主要使用的语言", StringComparison.Ordinal), name);
            }
        }
    }

    [Fact]
    public void Chinese_prompts_are_unchanged()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
            var rendered = Render();
            Assert.Equal(PromptGoldens.Chinese.Keys.Order(StringComparer.Ordinal), rendered.Keys.Order(StringComparer.Ordinal));
            foreach (var (key, golden) in PromptGoldens.Chinese)
            {
                Assert.True(golden == rendered[key], $"{key} changed:\n{rendered[key]}");
            }
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public void Prompts_follow_the_language_when_the_request_is_built()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.English);
            var english = Render();
            Assert.StartsWith("You are the assistant in Mindweave", english["run.all"], StringComparison.Ordinal);
            Assert.Contains("search_attachments (", english["run.all"], StringComparison.Ordinal);
            Assert.Contains("notes_write, calendar_add: third-party tools", english["run.all"], StringComparison.Ordinal);
            Assert.Contains("Recommend 3 new questions worth exploring next", english["recommendations.question"], StringComparison.Ordinal);
            Assert.Contains("must extend one of the summaries", english["recommendations.node"], StringComparison.Ordinal);
            Assert.Contains("at most 5 pairs of summaries", english["link-suggestions"], StringComparison.Ordinal);
            Assert.Contains("at most 16 characters", english["link-suggestions"], StringComparison.Ordinal);
            Assert.StartsWith("Current name: 机械\n", english["workspace-names.user"], StringComparison.Ordinal);
            Assert.Contains("[S1] 蒸汽机: 蒸汽机的原理\nExcerpt: 瓦特改良了冷凝器", english["recommendations.user"], StringComparison.Ordinal);
            Assert.Contains("- S1 and S2", english["link-suggestions.user"], StringComparison.Ordinal);
            Assert.Contains("引用的一段话", english["summary-branch-background"], StringComparison.Ordinal);

            // Nothing Chinese is left in what is written for the model, apart from the sample content and the tools' own
            // display names (which are the tools' text, not the prompt's).
            var tools = new ITool[] { new SearchAttachmentsTool(null!), new SearchSummariesTool(null!), new WebSearchTool(null!), new FetchPageTool(null!) };
            foreach (var (key, text) in english)
            {
                var own = text.Replace("引用的一段话", string.Empty, StringComparison.Ordinal).Replace("来源回答的开头", string.Empty, StringComparison.Ordinal);
                foreach (var sample in tools.Select(t => t.DisplayName).Concat(["蒸汽机车", "蒸汽机的原理", "蒸汽机", "瓦特改良了冷凝器", "火车", "铁路", "为什么铁轨是钢的？", "机械"]))
                {
                    own = own.Replace(sample, string.Empty, StringComparison.Ordinal);
                }

                Assert.False(own.Any(c => c is >= '一' and <= '鿿' or >= '　' and <= '〿' or >= '＀' and <= '￯'), $"{key}:\n{text}");
            }
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public void Fill_refuses_a_missing_or_unknown_placeholder()
    {
        Assert.Equal("a 1 {\"b\":2} 1", PromptLibrary.Fill("a {n} {\"b\":2} {n}", ("n", "1")));
        Assert.Equal("{x}", PromptLibrary.Fill("{v}", ("v", "{x}")));
        Assert.Throws<InvalidOperationException>(() => PromptLibrary.Fill("a {n}"));
        Assert.Throws<InvalidOperationException>(() => PromptLibrary.Fill("a", ("n", "1")));
        Assert.Throws<ArgumentException>(() => PromptLibrary.Get("no-such-prompt", UiLanguage.English));
    }

    [Fact]
    public void Parser_accepts_English_markers()
    {
        var parsed = SummaryRules.ParseGenerated("Title: X\nShort name: Y\n\nbody");
        Assert.NotNull(parsed);
        Assert.Equal("X", parsed.Title);
        Assert.Equal("Y", parsed.ShortTitle);
        Assert.Equal("body", parsed.Body);

        var lower = SummaryRules.ParseGenerated("title: X\nshort name: Y\n\nbody");
        Assert.Equal(("X", "Y", "body"), (lower!.Title, lower.ShortTitle, lower.Body));

        var chinese = SummaryRules.ParseGenerated("标题：甲\n短名：乙\n\n正文");
        Assert.Equal(("甲", "乙", "正文"), (chinese!.Title, chinese.ShortTitle, chinese.Body));

        // A link suggestion written as a line, the English way.
        var input = new LinkSuggestionInput(Sources, 2, []);
        var pairs = LinkSuggestionPrompt.Parse("S1 and S2 | contrast | Both are about steam", input, []);
        Assert.Equal("contrast", Assert.Single(pairs).Note);
    }

    /// <summary>Every prompt the goldens hold, rendered by the code as it is now, in the current language.</summary>
    private static Dictionary<string, string> Render()
    {
        var app = typeof(SummaryService).Assembly;
        var all = new ITool[]
        {
            new SearchAttachmentsTool(null!), new SearchSummariesTool(null!), new WebSearchTool(null!), new FetchPageTool(null!), new NamedTool("notes_write"), new NamedTool("calendar_add"),
        };
        WorkspaceName.TryCreate("机械", out var current, out _);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["summary"] = PromptLibrary.Get("summary"),
            ["summary.request"] = Static(typeof(SummaryService), "Request"),
            ["summary-branch-background"] = PromptLibrary.Get("summary-branch-background", ("quote", "引用的一段话"), ("excerpt", "来源回答的开头")),
            ["summary-naming"] = PromptLibrary.Get("summary-naming"),
            ["compression"] = PromptLibrary.Get("compression"),
            ["title"] = PromptLibrary.Get("title"),
            ["attachment-digest"] = PromptLibrary.Get("attachment-digest"),
            ["attachment-digest-merge"] = PromptLibrary.Get("attachment-digest-merge"),
            ["run.all"] = RunPrompt.For(all),
            ["run.none"] = RunPrompt.For([]),
            ["run.one"] = RunPrompt.For([new SearchAttachmentsTool(null!)]),
            ["run.third"] = RunPrompt.For([new NamedTool("notes_write")]),
            ["run.budget"] = RunPrompt.BudgetLine(RunLimits.Default, 2, 5),
            ["run.final"] = Static(app.GetType("Mindweave.Application.Conversations.AnswerRunner")!, "FinalInstruction"),
            ["run.cutoff"] = Static(app.GetType("Mindweave.Application.Conversations.AnswerRunner")!, "CutOffInstruction"),
            ["recommendations.question"] = RecommendationPrompt.SystemPrompt(new RecommendationAsk(RecommendationKind.Question, 3)),
            ["recommendations.node"] = RecommendationPrompt.SystemPrompt(new RecommendationAsk(RecommendationKind.Node, 2)),
            ["recommendations.user"] = RecommendationPrompt.UserText(new RecommendationInput(["蒸汽机车", "铁路"], 5, Sources, 4, ["为什么铁轨是钢的？"])),
            ["recommendations.user-empty"] = RecommendationPrompt.UserText(new RecommendationInput([], 0, Sources, 2, [])),
            ["link-suggestions"] = LinkSuggestionPrompt.SystemPrompt(),
            ["link-suggestions.user"] = LinkSuggestionPrompt.UserText(new LinkSuggestionInput(Sources, 7, [("S1", "S2")])),
            ["link-suggestions.user-noavoid"] = LinkSuggestionPrompt.UserText(new LinkSuggestionInput(Sources, 2, [])),
            ["workspace-names"] = WorkspaceNamePrompt.SystemPrompt,
            ["workspace-names.user"] = WorkspaceNamePrompt.UserText(current!, new WorkspaceNameInput(["蒸汽机车", "铁路"], 4, ["蒸汽机"], 1)),
            ["workspace-names.user-nosummary"] = WorkspaceNamePrompt.UserText(current!, new WorkspaceNameInput(["蒸汽机车"], 1, [], 0)),
        };
    }

    /// <summary>A static string property of an internal or private member, read as the code reads it.</summary>
    private static string Static(Type type, string name) =>
        (string)type.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    /// <summary>A third-party-looking tool that is never run.</summary>
    private sealed class NamedTool(string name) : ITool
    {
        public string Name => name;

        public string DisplayName => name;

        public string Description => "test tool";

        public string ParametersSchema => "{\"type\":\"object\"}";

        public ToolRisk Risk => ToolRisk.SideEffect;

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public Task<bool> IsAvailableAsync(ToolScope scope, CancellationToken cancellationToken) => Task.FromResult(true);

        public string Describe(JsonElement arguments) => string.Empty;

        public Task<ToolOutput> ExecuteAsync(ToolScope scope, JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolOutput("done", "done", ResourceProvenance.Workspace, []));
    }
}
