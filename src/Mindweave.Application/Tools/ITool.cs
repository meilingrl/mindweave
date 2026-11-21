using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Core.Workspaces;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Application.Tools;

/// <summary>
/// What a run's tools may reach (requirements §10.4.4): the conversation's workspace, the conversation, and the resources
/// its frozen chain refers to (attachments and earlier tool results, in its own messages and its ancestors' frozen ranges).
/// Tool results made during the run join <see cref="Resources"/> as they are stored.
/// </summary>
public sealed class ToolScope(WorkspaceId workspace, ConversationId conversation, IEnumerable<ResourceId> resources)
{
    private readonly HashSet<ResourceId> _resources = [.. resources];
    private readonly Lock _gate = new();

    public WorkspaceId Workspace { get; } = workspace;

    public ConversationId Conversation { get; } = conversation;

    /// <summary>The run's web state when web search is on for it (phase 3); null otherwise, and the web tools are not offered.</summary>
    public WebRun? Web { get; init; }

    private readonly RunSources _sources = new();

    /// <summary>The numbering of the run's sources, web and local (L11): the web state's when there is one.</summary>
    public RunSources Sources => Web?.Numbering ?? _sources;

    /// <summary>
    /// Whether the web search switch is not 关 for this question (phase 4, decision M8): third-party tools that read the
    /// network are offered only then. True unless a caller says otherwise.
    /// </summary>
    public bool NetworkAllowed { get; init; } = true;

    public IReadOnlyCollection<ResourceId> Resources
    {
        get
        {
            lock (_gate)
            {
                return [.. _resources];
            }
        }
    }

    public bool Contains(ResourceId resource)
    {
        lock (_gate)
        {
            return _resources.Contains(resource);
        }
    }

    internal void Add(ResourceId resource)
    {
        lock (_gate)
        {
            _resources.Add(resource);
        }
    }
}

/// <summary>
/// What a tool returned: <paramref name="Text"/> the whole result (stored as a resource, and sent to the model when it is
/// short enough), <paramref name="Detail"/> a short account for step details ("找到 3 段"), the provenance of the material it
/// carries, and what it found.
/// </summary>
public sealed record ToolOutput(string Text, string Detail, ResourceProvenance Provenance, IReadOnlyList<ToolFinding> Findings)
{
    /// <summary>What the stored result is (phase 3: <see cref="ResourceOrigin.Web"/> for search results and pages).</summary>
    public ResourceOrigin Origin { get; init; } = ResourceOrigin.Tool;

    /// <summary>The stored result's name; null names it after the tool and its input.</summary>
    public string? Name { get; init; }

    /// <summary>Where the material came from (a page's final address).</summary>
    public Uri? SourceUrl { get; init; }

    /// <summary>Called with the stored result's ID (a web tool numbers its sources by it).</summary>
    public Action<ResourceId>? Stored { get; init; }

    /// <summary>
    /// Images that come with the result (phase 4, decision M15): each is stored as its own resource, and its placeholder
    /// <see cref="ImagePlaceholder"/> (1-based) in <see cref="Text"/> is replaced by <see cref="ImageMarker"/>. They are not
    /// sent to the model.
    /// </summary>
    public IReadOnlyList<ToolImage> Images { get; init; } = [];

    /// <summary>Where a tool's <paramref name="number"/>th image stands in its text until it is stored: "[Image 1]", "[图片 1]".</summary>
    public static string ImagePlaceholder(int number) => T(EnglishPlaceholder(number), ChinesePlaceholder(number));

    /// <summary>
    /// Both languages' placeholders for one image: the language may switch between the call and storing its result, so
    /// whichever the tool wrote is replaced.
    /// </summary>
    public static IReadOnlyList<string> ImagePlaceholders(int number) => [EnglishPlaceholder(number), ChinesePlaceholder(number)];

    /// <summary>What the stored text keeps in an image's place, in the language of the moment it is stored.</summary>
    public static string ImageMarker(ResourceId image) => T($"[Image: resource {image}]", $"[图片：资源 {image}]");

    private static string EnglishPlaceholder(int number) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[Image {number}]");

    private static string ChinesePlaceholder(int number) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[图片 {number}]");

    /// <summary>How the result is marked as material when sent to the model; null uses the web marking for external results.</summary>
    public Func<string, string>? Wrap { get; init; }

    /// <summary>The run's [n] numbers of the sources in the result (W4, L11), so a result the answer cites is kept longest (L7).</summary>
    public IReadOnlyList<int> Numbers { get; init; } = [];
}

/// <summary>An image a tool returned (phase 4), with its media type.</summary>
public sealed record ToolImage(ReadOnlyMemory<byte> Data, string MediaType);

/// <summary>
/// A call that may have changed something outside failed in a way that leaves its outcome unknown (a side effect that timed
/// out, or whose server went away while it ran; docs/design/mcp.md M17). The step is recorded as <c>unknown</c>; the message
/// goes back to the model.
/// </summary>
public sealed class ToolOutcomeUnknownException : Exception
{
    public ToolOutcomeUnknownException()
    {
    }

    public ToolOutcomeUnknownException(string message)
        : base(message)
    {
    }

    public ToolOutcomeUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Tools that come and go with the user's settings (phase 4: third-party tools); asked each time a question is accepted.</summary>
public interface IToolSource
{
    Task<IReadOnlyList<ITool>> ToolsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The tool ran but could not do what was asked (a page refused, the search service failed); the message says why in words
/// and goes back to the model as the call's result. Not logged as an unexpected failure.
/// </summary>
public sealed class ToolFailureException : Exception
{
    public ToolFailureException()
    {
    }

    public ToolFailureException(string message)
        : base(message)
    {
    }

    public ToolFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The arguments do not fit the tool's input; the message goes back to the model as the call's result.</summary>
public sealed class ToolInputException : Exception
{
    public ToolInputException()
    {
    }

    public ToolInputException(string message)
        : base(message)
    {
    }

    public ToolInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A tool a run can offer (architecture §15.6): its name (letters, digits, '_' and '-', at most 64), what it does, the JSON
/// Schema of its input, its risk, a time limit and an output limit. Tools read their own stores; they never see the
/// conversation, secrets or other tools' results beyond their scope.
/// </summary>
public interface ITool
{
    string Name { get; }

    /// <summary>The name a person sees (需求术语), for example 检索附件.</summary>
    string DisplayName { get; }

    string Description { get; }

    /// <summary>The JSON Schema of the arguments, as JSON text.</summary>
    string ParametersSchema { get; }

    ToolRisk Risk { get; }

    TimeSpan Timeout { get; }

    /// <summary>Whether the tool has anything to search in this scope (decision R5: a tool with nothing to find is not offered).</summary>
    Task<bool> IsAvailableAsync(ToolScope scope, CancellationToken cancellationToken);

    /// <summary>One readable line for the arguments, for step details (the search terms, the part read).</summary>
    string Describe(JsonElement arguments);

    /// <summary>Runs the tool. Throws <see cref="ToolInputException"/> when the arguments do not fit its input.</summary>
    Task<ToolOutput> ExecuteAsync(ToolScope scope, JsonElement arguments, CancellationToken cancellationToken);

    /// <summary>
    /// What the user must approve before this call runs (shown on the card: for 取网页, the full address), or null when it runs
    /// without asking (phase 3, Q10: an address that appeared nowhere the run may read freely).
    /// </summary>
    string? ApprovalFor(ToolScope scope, JsonElement arguments) => null;

    /// <summary>The host an outside request of this call goes to, for step details and statistics; null for a local tool.</summary>
    string? TargetOf(JsonElement arguments) => null;

    /// <summary>
    /// Checks, before the user is asked to approve a call, that it can run at all (phase 4: the server starts and still
    /// offers the tool as it was enabled, decision M6). Throws <see cref="ToolFailureException"/> with the reason; the call
    /// then fails without a card.
    /// </summary>
    Task PrepareAsync(ToolScope scope, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The card for a call that needs approval; <paramref name="asks"/> is what <see cref="ApprovalFor"/> gave.</summary>
    Conversations.PendingApproval DescribeApproval(string callId, string asks) => new(callId, Name, asks);

    /// <summary>What the model is told when the user refused a call.</summary>
    string DeniedResult => WebMaterial.DeniedResult;

    /// <summary>The step's account of a refused call.</summary>
    string DeniedDetail => T("You denied reading this", "你拒绝了读取");
}

/// <summary>Helpers for tools' arguments and definitions.</summary>
public static class ToolArguments
{
    /// <summary>A string property, trimmed; null when absent or blank. A value of another type is a <see cref="ToolInputException"/>.</summary>
    public static string? Text(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim() is { Length: > 0 } text ? text : null
            : throw new ToolInputException(T($"The argument {name} should be text.", $"参数 {name} 应是文字。"));
    }

    /// <summary>An integer property (a whole number given as a number or as digits); null when absent.</summary>
    public static int? WholeNumber(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
        {
            return n;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new ToolInputException(T($"The argument {name} should be a whole number.", $"参数 {name} 应是整数。"));
    }

    /// <summary>
    /// The arguments as compared for repeated calls (docs/design/agent-loop.md L4): object keys in order, strings without
    /// surrounding white space, numbers and other values as written.
    /// </summary>
    public static string Canonical(JsonElement arguments)
    {
        var s = new StringBuilder();
        Write(arguments);
        return s.ToString();

        void Write(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    s.Append('{');
                    var first = true;
                    foreach (var property in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        s.Append(first ? string.Empty : ",").Append(JsonSerializer.Serialize(property.Name)).Append(':');
                        first = false;
                        Write(property.Value);
                    }

                    s.Append('}');
                    break;
                case JsonValueKind.Array:
                    s.Append('[');
                    var i = 0;
                    foreach (var item in e.EnumerateArray())
                    {
                        s.Append(i++ == 0 ? string.Empty : ",");
                        Write(item);
                    }

                    s.Append(']');
                    break;
                case JsonValueKind.String:
                    s.Append(JsonSerializer.Serialize(e.GetString()!.Trim()));
                    break;
                default:
                    s.Append(e.GetRawText());
                    break;
            }
        }
    }

    /// <summary>The hash a run freezes for a tool's definition (architecture §15.3: <c>generation_task.tools</c>).</summary>
    public static string DefinitionHash(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var bytes = Encoding.UTF8.GetBytes(tool.Name + "\n" + tool.Description + "\n" + tool.ParametersSchema);
        return Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
    }

    /// <summary>At most <paramref name="max"/> characters of <paramref name="text"/> around its first match of <paramref name="terms"/>.</summary>
    public static string Snippet(string text, IReadOnlyList<string> terms, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(terms);
        var flat = text.ReplaceLineEndings(" ");
        if (flat.Length <= max)
        {
            return flat.Trim();
        }

        var hit = terms.Select(t => flat.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Clamp(hit - (max / 3), 0, flat.Length - max);
        if (start > 0 && char.IsLowSurrogate(flat[start]))
        {
            start--;
        }

        var end = Math.Min(flat.Length, start + max);
        if (end < flat.Length && char.IsHighSurrogate(flat[end - 1]))
        {
            end--;
        }

        return (start > 0 ? "…" : string.Empty) + flat[start..end].Trim() + (end < flat.Length ? "…" : string.Empty);
    }

    /// <summary>The distinct plain terms of a query as a person reads them: words and CJK pairs (see <see cref="SearchTerms"/>).</summary>
    public static IReadOnlyList<string> Terms(string query) =>
        [.. SearchTerms.Of(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)];
}
