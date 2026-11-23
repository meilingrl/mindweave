using Mindweave.Application.Conversations;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>One line of the context usage card: what the part is and about how much it takes.</summary>
public sealed record ContextUsageRow(ContextUsageKind Kind, int Tokens)
{
    public string Label => UiText.ContextPart(Kind);

    public string Amount => "~" + UiText.CompactTokens(Tokens);

    public string Name => Label + " " + Amount;
}

/// <summary>The third level of the context ring (the owner, 2026-11-23): the total, and what it is made of.</summary>
public sealed class ContextUsageDetails(ContextUsage usage)
{
    public ContextUsage Usage { get; } = usage ?? throw new ArgumentNullException(nameof(usage));

    public string Percent => UiText.ContextPercent(Usage);

    public string Total => UiText.ContextTotal(Usage);

    /// <summary>What every request carries first, then what the reader brought.</summary>
    public IReadOnlyList<ContextUsageRow> Rows { get; } =
        [.. usage.Parts.OrderBy(p => Order(p.Kind)).Select(p => new ContextUsageRow(p.Kind, p.Tokens))];

    public string? Note => UiText.ContextUsageNote(Usage);

    public bool HasNote => Note is not null;

    private static int Order(ContextUsageKind kind) => kind switch
    {
        ContextUsageKind.Instructions => 0,
        ContextUsageKind.Tools => 1,
        ContextUsageKind.Ancestors => 2,
        ContextUsageKind.ContextSummaries => 3,
        ContextUsageKind.Conversation => 4,
        _ => 5,
    };
}
