using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Export;

/// <summary>Exporting a saved summary version, as the summary page and the exit use it (course requirement C3).</summary>
public interface ISummaryExport
{
    Task WhenIdleAsync();

    Task<string> SuggestFileNameAsync(SummaryNodeId node, SummaryVersionId version, CancellationToken cancellationToken);

    Task ExportAsync(SummaryNodeId node, SummaryVersionId version, Func<ConversationSummary, string>? sourceName, ExportDestination destination, CancellationToken cancellationToken);
}
