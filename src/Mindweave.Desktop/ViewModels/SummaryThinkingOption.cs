using Mindweave.Core.Chat;

namespace Mindweave.Desktop.ViewModels;

public sealed record SummaryThinkingOption(SummaryThinkingMode Mode, string Label)
{
    public override string ToString() => Label;
}
