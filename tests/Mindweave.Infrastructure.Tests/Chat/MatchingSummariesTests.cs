using Mindweave.Application.Conversations;
using Mindweave.Application.Languages;
using Mindweave.Application.Models;
using Mindweave.Application.Prompts;
using Mindweave.Application.Summaries;
using Mindweave.Application.Tools;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// The titles of matching summaries (requirements §10.9, docs/design/reply-quality.md Q4, Q7): one local keyword search of
/// the workspace's summaries before each answer; at most three titles go with the question, by the follow-up rule (L9), and
/// they are the first thing left out when room is short.
/// </summary>
[Collection(Languages.LanguageTests.Name)]
public class MatchingSummariesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MatchingSummaries.Document Doc(string name, string body) =>
        new(name, MatchingSummaries.TermsOf(name), MatchingSummaries.TermsOf(body), DateTimeOffset.UnixEpoch);

    private static IReadOnlyList<string> Rank(string query, params MatchingSummaries.Document[] documents) =>
        MatchingSummaries.Rank(MatchingSummaries.TermsOf(query), documents, documents.Length);

    private static readonly MatchingSummaries.Document[] Workspace =
    [
        Doc("火车的发明", "斯蒂芬森的火箭号在雨山试验中胜出，证明蒸汽机车可以牵引列车。"),
        Doc("无砟轨道", "无砟轨道把钢轨固定在混凝土轨道板上，平顺性好，维护少。"),
        Doc("受电弓", "受电弓把碳滑板压在接触线上，从接触网取电。"),
        Doc("Entropy", "Entropy measures how many microstates fit a macrostate; the second law says it does not decrease."),
        Doc("Photosynthesis basics", "Plants turn light, water and carbon dioxide into sugar and oxygen in the chloroplast."),
    ];

    [Theory]
    [InlineData("火车是怎么发明的？", "火车的发明")] // two words of the title
    [InlineData("受电弓为什么不会打火？", "受电弓")] // the whole title
    [InlineData("斯蒂芬森的火箭号为什么能赢？", "火车的发明")] // three words of the body
    [InlineData("Can you explain entropy again?", "Entropy")]
    [InlineData("How does photosynthesis make sugar?", "Photosynthesis basics")]
    public void A_question_on_a_topic_finds_its_summary(string question, string expected) =>
        Assert.Equal([expected], Rank(question, Workspace));

    [Theory]
    [InlineData("量子纠缠是什么？")]
    [InlineData("What is the capital of France?")]
    [InlineData("这是什么？为什么是这样的？")] // only words that name no topic
    [InlineData("轨道的作用是什么")] // one word of a title is not enough, and "道的" is no word
    [InlineData("Why does the light change colour at dusk?")] // one body word
    public void A_question_on_something_else_finds_none(string question) =>
        Assert.Empty(Rank(question, Workspace));

    [Fact]
    public void At_most_three_titles_best_first()
    {
        var many = Enumerable.Range(1, 5).Select(i => Doc($"蒸汽机车 {i}", string.Concat(Enumerable.Repeat("锅炉 ", i)))).ToArray();
        var found = Rank("蒸汽机车的锅炉", [.. many, Doc("其他", "无关")]);
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public void A_word_in_every_summary_says_nothing()
    {
        var all = new[] { Doc("火车 一", "历史"), Doc("火车 二", "速度 极限 纪录"), Doc("火车 三", "轨道") };
        Assert.Empty(Rank("火车", all));
        Assert.Equal(["火车 二"], Rank("火车的速度极限纪录", all));
    }

    [Theory]
    [InlineData("What about the second one?", "火车史", "蒸汽机车", null, "火车史 蒸汽机车 What about the second one?")]
    [InlineData("这一种为什么更快，它的原理在高速列车上是否同样适用呢", "火车史", null, null, "火车史 这一种为什么更快，它的原理在高速列车上是否同样适用呢")]
    [InlineData("A question long enough to stand on its own without the title", "火车史", "蒸汽机车", null, "蒸汽机车 A question long enough to stand on its own without the title")]
    [InlineData("A question long enough to stand on its own without the title", "火车史", null, null, "A question long enough to stand on its own without the title")]
    public void The_query_follows_the_pre_search_rule_and_a_branch_adds_its_selection(string question, string? title, string? quote, string? previous, string expected)
    {
        Assert.Equal(expected, WebRun.SummaryQuery(question, title, quote, previous));

        // One rule: what the pre-search uses for a short or referring question is what the summary search uses.
        if (WebRun.PreSearchQuery(question, title, quote, previous) != question)
        {
            Assert.Equal(WebRun.PreSearchQuery(question, title, quote, previous), WebRun.SummaryQuery(question, title, quote, previous));
        }
    }

    [Fact]
    public async Task A_question_on_a_summarized_topic_carries_its_title_and_one_on_another_topic_none()
    {
        await using var h = await ChatHarness.StartAsync();
        var (summarized, _) = await h.RootWithAnswerAsync("蒸汽机车是怎么工作的？");
        await h.SettleAsync(summarized);
        await h.SummarizeAsync(summarized); // titled FakeChatClient.DefaultSummaryTitle, 蒸汽机车

        // Acceptance 8: its title is in the request, with the question.
        await h.RootWithAnswerAsync("蒸汽机车的锅炉为什么要多根管子？");
        var request = h.Client.Requests.Last(FakeChatClient.IsAnswer);
        Assert.Equal(AnswerPrompt.SummariesPart([FakeChatClient.DefaultSummaryTitle]), FakeChatClient.NoteOf(request));
        Assert.Contains("- " + FakeChatClient.DefaultSummaryTitle, FakeChatClient.NoteOf(request), StringComparison.Ordinal);

        // No match: no titles line.
        await h.RootWithAnswerAsync("量子纠缠是怎么回事？");
        Assert.Empty(FakeChatClient.NoteOf(h.Client.Requests.Last(FakeChatClient.IsAnswer)));

        // The conversation's own summary is in the request already: it is not named again.
        await h.AskAsync(summarized, "蒸汽机车后来被什么取代了？");
        Assert.Empty(FakeChatClient.NoteOf(h.Client.Requests.Last(FakeChatClient.IsAnswer)));
    }

    [Fact]
    public async Task A_short_follow_up_searches_with_the_title_and_a_branch_with_its_selection()
    {
        await using var h = await ChatHarness.StartAsync();
        var (summarized, _) = await h.RootWithAnswerAsync("蒸汽机车是怎么工作的？");
        await h.SettleAsync(summarized);
        await h.SummarizeAsync(summarized);

        // Acceptance 9: "the second one?" alone matches nothing; with the conversation's title it does.
        h.Client.Titles.Replies.Enqueue("蒸汽机车的改进");
        var (root, answer) = await h.RootWithAnswerAsync("早期有哪几项改进？");
        Assert.Empty(FakeChatClient.NoteOf(h.Client.Requests.Last(FakeChatClient.IsAnswer)));
        await h.SettleAsync(root);
        await h.AskAsync(root, "第二个呢？");
        Assert.Equal(AnswerPrompt.SummariesPart([FakeChatClient.DefaultSummaryTitle]), FakeChatClient.NoteOf(h.Client.Requests.Last(FakeChatClient.IsAnswer)));

        // In a branch the selected passage goes with the question, and the branch part stays first.
        var child = await h.BranchAsync(root, answer, "蒸汽机车");
        await h.AskAsync(child, "A question long enough to stand on its own, about something else entirely");
        Assert.Equal(AnswerPrompt.RequestPart(true, [FakeChatClient.DefaultSummaryTitle]), FakeChatClient.NoteOf(h.Client.Requests.Last(FakeChatClient.IsAnswer)));
    }

    [Fact]
    public async Task Short_of_room_the_titles_are_left_out_first()
    {
        await using var h = await ChatHarness.StartAsync();
        var (summarized, _) = await h.RootWithAnswerAsync("蒸汽机车是怎么工作的？");
        await h.SettleAsync(summarized);
        await h.SummarizeAsync(summarized);
        await h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", ContextWindowTokens: 12_000), Ct);

        // A question that fits only without the titles line.
        var budget = ContextBudget.For((await h.Models.GetActiveSnapshotAsync(Ct))!).Available;
        var room = budget - TokenEstimator.PerRequest - TokenEstimator.Message(AnswerPrompt.Fixed);
        var question = "蒸汽机车" + new string('啊', room);
        while (TokenEstimator.Message(question) > room)
        {
            question = question[..^1];
        }

        Assert.True(TokenEstimator.Message(question) + TokenEstimator.Message(RequestNote.Of(AnswerPrompt.SummariesPart([FakeChatClient.DefaultSummaryTitle]))) > room);
        var sent = await h.Conversations.StartAsync(h.Workspace, question, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Empty(FakeChatClient.NoteOf(h.Client.Requests.Last(FakeChatClient.IsAnswer)));
    }

    [Fact]
    public void The_titles_part_stays_small_and_says_to_build_on_them()
    {
        foreach (var language in new[] { UiLanguage.English, UiLanguage.Chinese })
        {
            try
            {
                CurrentLanguage.Set(language);
                var part = AnswerPrompt.SummariesPart(["蒸汽机车的工作原理", "无砟轨道的结构", "受电弓与接触网"]);
                var size = TokenEstimator.Estimate(part);
                Assert.True(size <= 90, $"{language}: {size}");
                Assert.Contains("- 无砟轨道的结构\n", part, StringComparison.Ordinal);
            }
            finally
            {
                CurrentLanguage.Set(UiLanguage.Chinese);
            }
        }

        Assert.Contains("do not explain it again from the start", PromptLibrary.Get("answer-summaries", UiLanguage.English), StringComparison.Ordinal);
        Assert.Equal(string.Empty, AnswerPrompt.SummariesPart([]));
        Assert.Equal(string.Empty, AnswerPrompt.RequestPart(false, []));
    }
}
