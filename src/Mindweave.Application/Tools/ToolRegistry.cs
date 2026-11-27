using Mindweave.Application.Ai;
using Mindweave.Application.Storage;

namespace Mindweave.Application.Tools;

/// <summary>
/// The tools a run may be given (architecture §15.6). Phase 2 has two built-in local tools; later phases add network and
/// third-party ones. A run is offered the tools that have something to find in its scope (decision R5).
/// </summary>
public sealed class ToolRegistry(IEnumerable<ITool> tools, IEnumerable<IToolSource> sources)
{
    private readonly IReadOnlyList<ITool> _tools = [.. tools];
    private readonly IReadOnlyList<IToolSource> _sources = [.. sources];

    /// <summary>The built-in tools (and any a test adds); third-party tools come from the sources when a question is accepted.</summary>
    public IReadOnlyList<ITool> All => _tools;

    public ITool? Find(string name) => _tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The tools worth offering in <paramref name="scope"/>: the built-in ones first, then those of the sources (phase 4).
    /// A tool whose check fails is not offered; a source's tool whose name is already taken is not offered either (names
    /// are made unique when a tool is enabled, decision M9, so this only guards against a stale setting).
    /// </summary>
    public async Task<IReadOnlyList<ITool>> OfferAsync(ToolScope scope, CancellationToken cancellationToken)
    {
        var offered = new List<ITool>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<ITool>(_tools);
        foreach (var source in _sources)
        {
            candidates.AddRange(await source.ToolsAsync(cancellationToken).ConfigureAwait(false));
        }

        foreach (var tool in candidates)
        {
            if (names.Contains(tool.Name))
            {
                continue;
            }

            if (await tool.IsAvailableAsync(scope, cancellationToken).ConfigureAwait(false))
            {
                offered.Add(tool);
                names.Add(tool.Name);
            }
        }

        return offered;
    }

    public static ChatTool Definition(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new ChatTool(tool.Name, tool.Description, tool.ParametersSchema);
    }

    public static OfferedTool Frozen(ITool tool) => new(tool.Name, ToolArguments.DefinitionHash(tool));
}
