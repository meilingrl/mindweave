using Mindweave.Application.Models;
using Mindweave.Core.Context;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>The answer output limit set under 高级设置 › 输出上限 (2026-11-21).</summary>
public class OutputLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_saved_limit_goes_with_every_answer_and_internal_requests_keep_their_own()
    {
        await using var h = await ChatHarness.StartAsync(model: new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", MaxOutputTokens: 6_000));

        Assert.Equal(6_000, (await h.Models.GetAsync(Ct))!.MaxOutputTokens);
        var (root, _) = await h.RootWithAnswerAsync("火车是怎么发明的？");
        await h.AskAsync(root, "后来呢？");

        var answers = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        Assert.Equal(2, answers.Count);
        Assert.All(answers, a => Assert.Equal(6_000, a.Model.MaxOutputTokens));
        // The context budget reserves the same room for the answer.
        Assert.Equal(6_000, ContextBudget.For(answers[0].Model).OutputReserve);
        Assert.All(h.Client.Requests.Where(FakeChatClient.IsTitle), t => Assert.NotEqual(6_000, t.Model.MaxOutputTokens));
    }

    [Fact]
    public async Task An_empty_limit_sends_none_and_clears_a_saved_one()
    {
        await using var h = await ChatHarness.StartAsync(model: new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", MaxOutputTokens: 6_000));

        await h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", null), Ct);

        Assert.Null((await h.Models.GetAsync(Ct))!.MaxOutputTokens);
        await h.RootWithAnswerAsync("火车是怎么发明的？");
        Assert.Null(h.Client.Requests.Single(FakeChatClient.IsAnswer).Model.MaxOutputTokens);
    }

    [Theory]
    [InlineData(null, 255)]
    [InlineData(null, 16_001)]
    [InlineData(8_000, 4_001)]
    public async Task A_limit_below_the_minimum_or_above_half_the_capacity_is_refused(int? contextWindow, int limit)
    {
        await using var h = await ChatHarness.StartAsync();

        var error = await Assert.ThrowsAsync<ModelSettingsException>(() =>
            h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", null, contextWindow, MaxOutputTokens: limit), Ct));

        Assert.Equal(ModelSettingsProblem.InvalidOutputLimit, error.Problem);
    }

    [Theory]
    [InlineData(null, 256)]
    [InlineData(null, 16_000)]
    [InlineData(128_000, 64_000)]
    public async Task Limits_from_the_minimum_to_half_the_capacity_are_kept(int? contextWindow, int limit)
    {
        await using var h = await ChatHarness.StartAsync();

        await h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", null, contextWindow, MaxOutputTokens: limit), Ct);

        Assert.Equal(limit, (await h.Models.GetAsync(Ct))!.MaxOutputTokens);
    }
}
