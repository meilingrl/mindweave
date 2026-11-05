using Mindweave.Core.Context;
using Mindweave.Core.Models;

namespace Mindweave.Core.Tests;

public class TokenCalibrationTests
{
    /// <summary>Claude through a relay, 2026-11-05: (estimate, billed prompt tokens) of the long-conversation run.</summary>
    private static readonly (int Estimate, int Reported)[] ClaudeViaRelay =
    [
        (69, 702), (109, 746), (156, 795), (188, 827), (726, 1489), (1260, 2116), (1771, 2735),
        (2336, 3362), (3148, 4327), (549, 1316), (3667, 4938), (592, 1361),
    ];

    [Fact]
    public void No_reports_keep_the_plain_estimate()
    {
        Assert.Same(TokenCalibration.None, TokenCalibration.Fit([]));
        Assert.Same(TokenCalibration.None, TokenCalibration.Fit([(0, 50), (40, 0)]));
    }

    [Fact]
    public void A_model_that_bills_more_is_covered_for_every_request_seen()
    {
        var fitted = TokenCalibration.Fit(ClaudeViaRelay);

        Assert.InRange(fitted.Scale, 1.1, 1.3);
        Assert.InRange(fitted.Overhead, 550, 750);
        Assert.All(ClaudeViaRelay, s => Assert.True(fitted.Predict(s.Estimate) >= s.Reported, $"{s.Estimate} → {fitted.Predict(s.Estimate)} < {s.Reported}"));
    }

    [Fact]
    public void A_model_that_bills_less_than_the_estimate_is_not_given_more_room()
    {
        // DeepSeek, 2026-10-27: the estimate was about 1.4× what was billed.
        Assert.Same(TokenCalibration.None, TokenCalibration.Fit([(100, 70), (1_000, 700), (4_000, 2_800)]));
    }

    [Fact]
    public void One_report_only_adds_its_overhead()
    {
        var fitted = TokenCalibration.Fit([(100, 160)]);

        Assert.Equal(1, fitted.Scale);
        Assert.Equal(60, fitted.Overhead);
    }

    [Fact]
    public void A_wild_report_among_normal_ones_is_set_aside()
    {
        List<(int, int)> samples = [.. ClaudeViaRelay, (900, 60_000)];

        var fitted = TokenCalibration.Fit(samples);

        Assert.InRange(fitted.Scale, 1.1, 1.3);
        Assert.InRange(fitted.Overhead, 550, 750);
    }

    [Fact]
    public void Requests_of_similar_size_add_overhead_but_no_slope()
    {
        // Small first requests whose counts differ by text, not size: a slope from them would be noise (review 2026-11-05).
        var fitted = TokenCalibration.Fit([(150, 790), (160, 820), (120, 700)]);

        Assert.Equal(1, fitted.Scale);
        Assert.Equal(660, fitted.Overhead);
    }

    [Fact]
    public void No_report_can_leave_a_model_without_room()
    {
        var model = new ModelSnapshot("m", "https://x.example/v1", ChatProtocol.ChatCompletions, "x", "cred", null, ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ContextWindowTokens = 32_000 });
        var wild = TokenCalibration.Fit([(100, 100), (1_000, 50_000)]);

        var budget = ContextBudget.For(model, wild);

        Assert.Equal(TokenCalibration.MaxScale, wild.Scale);
        Assert.True(budget.Available >= budget.AvailableTokens / 4, $"{budget.Available} of {budget.AvailableTokens}");
    }

    [Theory]
    [InlineData(1.0, 0, 10_000)]
    [InlineData(1.17, 630, 10_000)]
    [InlineData(2.5, 1_000, 3_333)]
    public void The_largest_estimate_within_a_space_is_predicted_to_fit_and_one_more_is_not(double scale, int overhead, int tokens)
    {
        var calibration = new TokenCalibration(scale, overhead);

        var within = calibration.EstimateWithin(tokens);

        Assert.True(calibration.Predict(within) <= tokens);
        Assert.True(calibration.Predict(within + 1) > tokens);
    }

    [Fact]
    public void The_budget_gives_planners_the_space_in_estimate_units()
    {
        var model = new ModelSnapshot("m", "https://x.example/v1", ChatProtocol.ChatCompletions, "x", "cred", null, ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ContextWindowTokens = 12_000 });
        var plain = ContextBudget.For(model);
        var learned = ContextBudget.For(model, new TokenCalibration(1.5, 500));

        Assert.Equal(6_704, plain.Available);
        Assert.Equal(6_704, learned.AvailableTokens);
        Assert.Equal(4_136, learned.Available);
        Assert.True(learned.Fits(learned.Available));
        Assert.False(learned.Fits(plain.Available));
    }
}
