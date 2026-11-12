using System.Globalization;
using Mindweave.Application.Storage;

namespace Mindweave.Application.Statistics;

/// <summary>
/// The user's own unit prices for one model, per million tokens. A price has both parts (either may be 0); without one
/// the page shows no cost for that model. No price is ever built in or fetched.
/// </summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion)
{
    /// <summary>tokens / 1,000,000 × price, for input and output, unrounded (the page rounds to two decimals).</summary>
    public decimal CostOf(TokenCounts counts) =>
        (counts.Input / 1_000_000m * InputPerMillion) + (counts.Output / 1_000_000m * OutputPerMillion);
}

/// <summary>Why a typed unit price or currency symbol was not accepted.</summary>
public enum PriceProblem
{
    NotANumber,
    Negative,
    TooLarge,

    /// <summary>One of the two prices is filled and the other is empty.</summary>
    Incomplete,

    /// <summary>The currency symbol is longer than <see cref="UsagePricing.MaxCurrencyLength"/> characters.</summary>
    CurrencyTooLong,
}

/// <summary>Parsing and checking what the user types for prices; the rules the page and the store share.</summary>
public static class UsagePricing
{
    public const string DefaultCurrency = "¥";

    public const int MaxCurrencyLength = 3;

    /// <summary>A unit price above this is surely a typo (per million tokens).</summary>
    public const decimal MaxPrice = 1_000_000m;

    /// <summary>
    /// One typed unit price: empty (null, no problem), a non-negative decimal number with a '.' as the point (a '．' or
    /// '。' typed with a Chinese keyboard is taken as one too), or a problem.
    /// </summary>
    public static (decimal? Value, PriceProblem? Problem) ParsePrice(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim().Replace('。', '.').Replace('．', '.');
        if (trimmed.Length == 0)
        {
            return (null, null);
        }

        if (!decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return (null, PriceProblem.NotANumber);
        }

        if (value < 0)
        {
            return (null, PriceProblem.Negative);
        }

        return value > MaxPrice ? (null, PriceProblem.TooLarge) : (value, null);
    }

    /// <summary>
    /// The two typed prices of a model: both empty means no price (null), both valid a price; otherwise the first problem,
    /// with which field it is in (<c>true</c> for input).
    /// </summary>
    public static (ModelPrice? Price, PriceProblem? Problem, bool? InInput) ParsePrices(string? input, string? output)
    {
        var (inValue, inProblem) = ParsePrice(input);
        if (inProblem is { } p1)
        {
            return (null, p1, true);
        }

        var (outValue, outProblem) = ParsePrice(output);
        if (outProblem is { } p2)
        {
            return (null, p2, false);
        }

        return (inValue, outValue) switch
        {
            (null, null) => (null, null, null),
            ({ } i, { } o) => (new ModelPrice(i, o), null, null),
            (null, _) => (null, PriceProblem.Incomplete, true),
            _ => (null, PriceProblem.Incomplete, false),
        };
    }

    /// <summary>A currency symbol: free text up to three characters (trimmed); empty means the default.</summary>
    public static (string? Symbol, PriceProblem? Problem) ParseCurrency(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return (DefaultCurrency, null);
        }

        return new StringInfo(trimmed).LengthInTextElements > MaxCurrencyLength ? (null, PriceProblem.CurrencyTooLong) : (trimmed, null);
    }

    /// <summary>A price as it is typed back into its field: invariant, no trailing zeros ("0.5", "2").</summary>
    public static string Format(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);
}

/// <summary>The unit prices and the currency symbol the statistics page uses (2026-11-12).</summary>
public interface IUsagePrices
{
    /// <summary>The symbol written before costs; <see cref="UsagePricing.DefaultCurrency"/> until changed.</summary>
    Task<string> GetCurrencyAsync(CancellationToken cancellationToken);

    /// <summary>Stores a checked symbol (see <see cref="UsagePricing.ParseCurrency"/>); throws for one that is not.</summary>
    Task SetCurrencyAsync(string symbol, CancellationToken cancellationToken);

    Task<ModelPrice?> GetAsync(ModelKey key, CancellationToken cancellationToken);

    /// <summary>Stores the model's price, or removes it when <paramref name="price"/> is null.</summary>
    Task SetAsync(ModelKey key, ModelPrice? price, CancellationToken cancellationToken);
}

/// <summary>
/// Prices in the preference store, one key per model and service host (no migration); a value that cannot be read
/// back is no price.
/// </summary>
public sealed class UsagePricesService(IPreferenceStore store) : IUsagePrices
{
    public const string CurrencyKey = "usage.currency";

    public const string PricePrefix = "usage.price:";

    public static string KeyOf(ModelKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return PricePrefix + key.Model + "@" + key.Host;
    }

    public async Task<string> GetCurrencyAsync(CancellationToken cancellationToken)
    {
        var stored = await store.GetAsync(CurrencyKey, cancellationToken).ConfigureAwait(false);
        return UsagePricing.ParseCurrency(stored).Symbol ?? UsagePricing.DefaultCurrency;
    }

    public Task SetCurrencyAsync(string symbol, CancellationToken cancellationToken)
    {
        var (checkedSymbol, problem) = UsagePricing.ParseCurrency(symbol);
        if (problem is not null || checkedSymbol is null)
        {
            throw new ArgumentException("The currency symbol is longer than three characters.", nameof(symbol));
        }

        return checkedSymbol == UsagePricing.DefaultCurrency
            ? store.RemoveAsync(CurrencyKey, cancellationToken)
            : store.SetAsync(CurrencyKey, checkedSymbol, cancellationToken);
    }

    public async Task<ModelPrice?> GetAsync(ModelKey key, CancellationToken cancellationToken) =>
        Parse(await store.GetAsync(KeyOf(key), cancellationToken).ConfigureAwait(false));

    public Task SetAsync(ModelKey key, ModelPrice? price, CancellationToken cancellationToken)
    {
        if (price is null)
        {
            return store.RemoveAsync(KeyOf(key), cancellationToken);
        }

        if (price.InputPerMillion is < 0 or > UsagePricing.MaxPrice || price.OutputPerMillion is < 0 or > UsagePricing.MaxPrice)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Unit prices are between 0 and one million.");
        }

        return store.SetAsync(KeyOf(key), UsagePricing.Format(price.InputPerMillion) + "|" + UsagePricing.Format(price.OutputPerMillion), cancellationToken);
    }

    /// <summary>"input|output"; anything else is no price.</summary>
    public static ModelPrice? Parse(string? stored)
    {
        if (stored?.Split('|') is not [var input, var output])
        {
            return null;
        }

        return UsagePricing.ParsePrices(input, output) is ({ } price, null, _) ? price : null;
    }
}
