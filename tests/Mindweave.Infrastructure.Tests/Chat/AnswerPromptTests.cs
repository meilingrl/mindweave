using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Languages;
using Mindweave.Application.Prompts;
using Mindweave.Application.Summaries;
using Mindweave.Application.Tools;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// The answer instructions (requirements §10.9, architecture §15.5; docs/design/reply-quality.md Q1, Q2, Q6b, Q10): every
/// answer request opens with them, a branch adds its part to every answer in it, and each piece has a size limit checked
/// here, so growth is caught without a paid run.
/// </summary>
[Collection(Languages.LanguageTests.Name)]
public class AnswerPromptTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The most each piece may take, in the app's own estimate (CJK one token a character, so a Chinese text counts high).
    /// Measured when written (2026-11-27), English / Chinese: answer 228 / 212, branch 134 / 130, summary 402 / 313.
    /// </summary>
    public static TheoryData<string, int> Limits => new()
    {
        { "answer", 260 },
        { "answer-branch", 150 },
        { "summary", 440 },
    };

    [Theory]
    [MemberData(nameof(Limits))]
    public void Each_piece_stays_within_its_limit_in_both_languages(string name, int limit)
    {
        foreach (var language in new[] { UiLanguage.English, UiLanguage.Chinese })
        {
            var size = TokenEstimator.Estimate(PromptLibrary.Get(name, language));
            Assert.True(size <= limit, $"{name} ({language}): {size} estimated tokens, limit {limit}");
        }
    }

    [Fact]
    public void The_run_instructions_for_one_tool_stay_within_their_limit_in_both_languages()
    {
        // Version 4 added the stop, refine, search-term, source and attachment lines (Q6), and what the app is moved to the
        // answer instructions.
        const int limit = 560;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Chinese })
            {
                CurrentLanguage.Set(language);
                var size = TokenEstimator.Estimate(RunPrompt.For([new SearchAttachmentsTool(null!)]));
                Assert.True(size <= limit, $"run ({language}): {size} estimated tokens, limit {limit}");
            }
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public void The_pieces_say_what_the_design_asks_and_nothing_about_how_an_answer_ends()
    {
        var en = PromptLibrary.Get("answer", UiLanguage.English);
        foreach (var line in new[] { "core idea first", "Define a term the first time", "example or an analogy", "Scale the length", "two clearly different readings", "do what it asks" })
        {
            Assert.Contains(line, en, StringComparison.Ordinal);
        }

        var zh = PromptLibrary.Get("answer", UiLanguage.Chinese);
        foreach (var line in new[] { "先讲核心意思", "术语第一次出现时给出定义", "举一个例子或打个比方", "篇幅与问题相称", "两种明显不同的理解", "照提问做" })
        {
            Assert.Contains(line, zh, StringComparison.Ordinal);
        }

        // Q3, Q5: nothing about quizzing, guiding or suggested next questions.
        foreach (var text in new[] { en, zh, PromptLibrary.Get("answer-branch", UiLanguage.English), PromptLibrary.Get("answer-branch", UiLanguage.Chinese) })
        {
            foreach (var word in new[] { "quiz", "follow-up", "next question", "测验", "追问建议", "下一个问题" })
            {
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The fake provider tells internal requests apart by their system turn; the instructions must read as an answer's.
        var model = new Core.Models.ModelSnapshot("fake", "https://fake.example/v1", Core.Models.ChatProtocol.ChatCompletions, "fake-model", "ref", null, Core.Models.ModelCapabilities.DefaultsFor(Core.Models.ChatProtocol.ChatCompletions));
        var request = new ChatRequest(model, [new ChatTurn(ChatRole.System, AnswerPrompt.For(true)), new ChatTurn(ChatRole.User, "问")]);
        Assert.True(FakeChatClient.IsAnswer(request));
    }

    [Fact]
    public async Task Every_answer_in_a_branch_carries_the_branch_part_and_a_root_answer_does_not()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, answer) = await h.RootWithAnswerAsync("火车是怎么发明的？");
        var rootRequest = h.Client.Requests.Single(FakeChatClient.IsAnswer);
        Assert.Equal(AnswerPrompt.For(false), rootRequest.Turns[0].Content);
        Assert.DoesNotContain(PromptLibrary.Get("answer-branch"), rootRequest.Turns[0].Content, StringComparison.Ordinal);

        var child = await h.BranchAsync(root, answer, "火车");
        await h.AskAsync(child, "谁造了第一台？");
        await h.AskAsync(child, "后来呢？");

        // Acceptance 3 and 4: the first question of the branch and a later one both open with the branch part.
        var branchRequests = h.Client.Requests.Where(FakeChatClient.IsAnswer).Skip(1).ToList();
        Assert.Equal(2, branchRequests.Count);
        Assert.All(branchRequests, r => Assert.Equal(new ChatTurn(ChatRole.System, AnswerPrompt.For(true)), r.Turns[0]));

        // A deeper branch gets the same part once: it names only the nearest selection (the last passage quoted).
        var childAnswer = (await h.Store.GetMessagesAsync(child, Ct))[^1];
        var grandchild = await h.BranchAsync(child, childAnswer, "答");
        await h.AskAsync(grandchild, "再细说");
        var deeper = h.Client.Requests.Last(FakeChatClient.IsAnswer);
        Assert.Equal(AnswerPrompt.For(true), deeper.Turns[0].Content);
        Assert.Single(deeper.Turns, t => t.Role == ChatRole.System);
    }

    [Fact]
    public async Task An_edition_replaces_the_prompts_for_the_flow_it_is_used_in_only()
    {
        // The evaluation runs an earlier version beside the current one (Q10): an empty answer text leaves the instructions out.
        var earlier = new PromptEdition(
            "earlier",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["answer"] = string.Empty, ["answer-branch"] = string.Empty, ["summary"] = "旧的总结说明" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["answer"] = string.Empty, ["answer-branch"] = string.Empty, ["summary"] = "旧的总结说明" });
        ChatHarness h;
        using (PromptLibrary.Use(earlier))
        {
            h = await ChatHarness.StartAsync();
            await h.RootWithAnswerAsync("问");
            Assert.Equal(string.Empty, AnswerPrompt.For(true));
            Assert.Equal("旧的总结说明", PromptLibrary.Get("summary", UiLanguage.Chinese));
        }

        await using (h)
        {
            Assert.DoesNotContain(h.Client.Requests.Single(FakeChatClient.IsAnswer).Turns, t => t.Role == ChatRole.System);
            Assert.NotEqual(string.Empty, AnswerPrompt.For(false));
            Assert.NotEqual("旧的总结说明", PromptLibrary.Get("summary", UiLanguage.Chinese));
        }
    }

    [Fact]
    public void The_versions_moved_with_the_texts()
    {
        Assert.Equal(1, AnswerPrompt.Version);
        Assert.Equal(4, RunPrompt.Version);
        Assert.Equal(2, SummaryService.PromptVersion);
        Assert.StartsWith(PromptLibrary.Get("answer", UiLanguage.Chinese), AnswerPrompt.WithRun(PromptLibrary.Get("answer", UiLanguage.Chinese), "x"), StringComparison.Ordinal);
        Assert.Equal("x", AnswerPrompt.WithRun(string.Empty, "x"));
    }
}
