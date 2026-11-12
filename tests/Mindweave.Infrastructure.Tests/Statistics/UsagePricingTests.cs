using Mindweave.Application.Statistics;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.Statistics;

/// <summary>The unit prices the user types (2026-11-12): what is accepted, how they are kept, and the cost they give.</summary>
public class UsagePricingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("", null, null)]
    [InlineData("   ", null, null)]
    [InlineData("2", "2", null)]
    [InlineData(" 0.5 ", "0.5", null)]
    [InlineData("0", "0", null)]
    [InlineData("1。25", "1.25", null)]
    [InlineData("8．0", "8", null)]
    [InlineData("1000000", "1000000", null)]
    [InlineData("-1", null, PriceProblem.Negative)]
    [InlineData("abc", null, PriceProblem.NotANumber)]
    [InlineData("1,5", null, PriceProblem.NotANumber)]
    [InlineData("1e3", null, PriceProblem.NotANumber)]
    [InlineData("1000000.01", null, PriceProblem.TooLarge)]
    public void A_typed_price_is_a_non_negative_number_or_empty(string typed, string? expected, PriceProblem? problem)
    {
        var (value, found) = UsagePricing.ParsePrice(typed);
        Assert.Equal(problem, found);
        Assert.Equal(expected, value is { } v ? UsagePricing.Format(v) : null);
    }

    [Fact]
    public void Both_prices_or_neither()
    {
        Assert.Equal((new ModelPrice(1, 2), (PriceProblem?)null, (bool?)null), UsagePricing.ParsePrices("1", "2"));
        Assert.Equal(((ModelPrice?)null, (PriceProblem?)null, (bool?)null), UsagePricing.ParsePrices("", " "));
        Assert.Equal(((ModelPrice?)null, (PriceProblem?)PriceProblem.Incomplete, (bool?)false), UsagePricing.ParsePrices("1", ""));
        Assert.Equal(((ModelPrice?)null, (PriceProblem?)PriceProblem.Incomplete, (bool?)true), UsagePricing.ParsePrices("", "2"));
        Assert.Equal(((ModelPrice?)null, (PriceProblem?)PriceProblem.Negative, (bool?)false), UsagePricing.ParsePrices("1", "-2"));
        Assert.Equal(((ModelPrice?)null, (PriceProblem?)PriceProblem.NotANumber, (bool?)true), UsagePricing.ParsePrices("x", "-2"));
    }

    [Theory]
    [InlineData("", "¥", null)]
    [InlineData("$", "$", null)]
    [InlineData(" US$ ", "US$", null)]
    [InlineData("元", "元", null)]
    [InlineData("EURO", null, PriceProblem.CurrencyTooLong)]
    public void A_currency_symbol_is_up_to_three_characters(string typed, string? expected, PriceProblem? problem) =>
        Assert.Equal((expected, problem), UsagePricing.ParseCurrency(typed));

    [Fact]
    public void Cost_is_tokens_per_million_times_the_price()
    {
        var price = new ModelPrice(2m, 8m);
        Assert.Equal((2m * 1.5m) + (8m * 0.25m), price.CostOf(new TokenCounts(1_500_000, 250_000, 3, 1)));
        Assert.Equal(0.000002m, price.CostOf(new TokenCounts(1, 0, 1, 0)));
        Assert.Equal(0m, new ModelPrice(0, 0).CostOf(new TokenCounts(9_000_000, 9_000_000, 1, 0)));
    }

    [Fact]
    public async Task Prices_are_kept_per_model_and_host_and_the_currency_once()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var prices = new UsagePricesService(new SqlitePreferenceStore(h.Database));
        var flash = new ModelKey("deepseek-flash", "api.deepseek.com");
        var relayed = new ModelKey("deepseek-flash", "relay.example.org");

        Assert.Equal("¥", await prices.GetCurrencyAsync(Ct));
        Assert.Null(await prices.GetAsync(flash, Ct));

        await prices.SetAsync(flash, new ModelPrice(0.5m, 2m), Ct);
        await prices.SetCurrencyAsync("$", Ct);
        Assert.Equal(new ModelPrice(0.5m, 2m), await prices.GetAsync(flash, Ct));
        Assert.Null(await prices.GetAsync(relayed, Ct));
        Assert.Equal("$", await prices.GetCurrencyAsync(Ct));

        // A second instance reads what the first stored.
        var again = new UsagePricesService(new SqlitePreferenceStore(h.Database));
        Assert.Equal(new ModelPrice(0.5m, 2m), await again.GetAsync(flash, Ct));

        await prices.SetAsync(flash, null, Ct);
        await prices.SetCurrencyAsync("", Ct);
        Assert.Null(await prices.GetAsync(flash, Ct));
        Assert.Equal("¥", await prices.GetCurrencyAsync(Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => prices.SetCurrencyAsync("EURO", Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => prices.SetAsync(flash, new ModelPrice(-1, 0), Ct));
    }

    [Theory]
    [InlineData("1|2", true)]
    [InlineData("1|", false)]
    [InlineData("x|2", false)]
    [InlineData("1", false)]
    [InlineData("1|2|3", false)]
    public void A_stored_price_that_cannot_be_read_back_is_no_price(string stored, bool readable) =>
        Assert.Equal(readable, UsagePricesService.Parse(stored) is not null);
}
