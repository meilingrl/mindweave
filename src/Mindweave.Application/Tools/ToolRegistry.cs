using Mindweave.Application.Ai;
using Mindweave.Application.Storage;

namespace Mindweave.Application.Tools;

/// <summary>
/// The tools a run may be given (architecture §15.6). Phase 2 has two built-in local tools; later phases add network and
/// third-party ones. A run is offered the tools that have something to find in its scope (decision R5).
/// </summary>
public sealed class ToolRegistry(IEnumerable<ITool> tools)
{
    private readonly IReadOnlyList<ITool> _tools = [.. tools];

    public IReadOnlyList<ITool> All => _tools;

    public ITool? Find(string name) => _tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>The tools worth offering in <paramref name="scope"/>, in registry order. A tool whose check fails is not offered.</summary>
    public async Task<IReadOnlyList<ITool>> OfferAsync(ToolScope scope, CancellationToken cancellationToken)
    {
        var offered = new List<ITool>();
        foreach (var tool in _tools)
        {
            if (await tool.IsAvailableAsync(scope, cancellationToken).ConfigureAwait(false))
            {
                offered.Add(tool);
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
