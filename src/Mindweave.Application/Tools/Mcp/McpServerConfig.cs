using System.Security.Cryptography;
using System.Text;
using Mindweave.Core.Runs;

namespace Mindweave.Application.Tools.Mcp;

/// <summary>
/// A third-party server as the user added it (requirements §10.4.6, docs/design/mcp.md §2). <see cref="Slug"/> starts the
/// names its tools are given (decision M9). The values of its environment and of its header are secrets (decision M3): only
/// their names are here, the values are in the credential store under <see cref="Credential"/>.
/// </summary>
public sealed record McpServerConfig(
    string Id,
    string Name,
    string Slug,
    McpServerKind Kind,
    string? Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> EnvironmentNames,
    Uri? Url,
    string? HeaderName,
    string? Credential,
    IReadOnlyList<McpToolSetting> Tools)
{
    public McpToolSetting? Tool(string name) => Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// A tool the user has turned on, or one turned off again because its definition changed (decision M6): its name at the
/// server, the name the model is given (decision M9), the definition as it was when enabled (decision M5), the risk that
/// gave, and the user's own choice of risk.
/// </summary>
public sealed record McpToolSetting(
    string Name,
    string ModelName,
    McpToolInfo Definition,
    string Hash,
    ToolRisk DefaultRisk,
    ToolRisk? ChosenRisk = null,
    bool Enabled = true,
    string? DisabledReason = null)
{
    /// <summary>The risk calls of this tool have: the user's choice, else the one its definition gave when enabled.</summary>
    public ToolRisk Risk => ChosenRisk ?? DefaultRisk;
}

/// <summary>
/// What 添加 collects (requirements §10.4.6): a name, a local command with its arguments and environment, or an address with
/// one optional header. <see cref="Id"/> is chosen up front so the confirmation can show the server's working folder.
/// </summary>
public sealed record McpServerDraft(
    string Name,
    McpServerKind Kind,
    string? Command = null,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? Url = null,
    string? HeaderName = null,
    string? HeaderValue = null)
{
    public string Id { get; init; } = Guid.CreateVersion7().ToString("N")[..16];
}

/// <summary>
/// What the confirmation shows before a server is saved (requirements §10.4.6): the full command line with each argument,
/// the working folder and the environment's names; or the address and the header's name. <see cref="Problem"/> is why the
/// draft cannot be saved, in words.
/// </summary>
public sealed record McpAddPreview(
    string? Problem,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyList<string> EnvironmentNames,
    Uri? Url,
    string? HeaderName)
{
    public bool CanAdd => Problem is null;
}

/// <summary>The names tools are given (decision M9): letters, digits, '_' and '-', at most 64, starting with the server's short name.</summary>
public static class McpNames
{
    public const int MaxName = 64;

    private const int MaxSlug = 16;

    private const string Separator = "__";

    /// <summary>A server's short name: the ASCII letters and digits of its name, lower case, at most 16; "mcp" when none; unique.</summary>
    public static string Slug(string name, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var used = new HashSet<string>(taken, StringComparer.Ordinal);
        var letters = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                letters.Append(c);
            }
            else if (c is '-' or '_' or ' ' && letters.Length > 0 && letters[^1] != '_')
            {
                letters.Append('_');
            }
        }

        var slug = letters.ToString().Trim('_');
        if (slug.Length > MaxSlug)
        {
            slug = slug[..MaxSlug].TrimEnd('_');
        }

        if (slug.Length == 0)
        {
            slug = "mcp";
        }

        var unique = slug;
        for (var n = 2; used.Contains(unique); n++)
        {
            unique = slug + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return unique;
    }

    /// <summary>
    /// The model's name for tool <paramref name="tool"/> of a server: "slug__tool" with other characters as '_'; when it is
    /// longer than 64 or already taken, shortened with '_' and four hex digits of a hash added.
    /// </summary>
    public static string ToolName(string slug, string tool, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var used = new HashSet<string>(taken, StringComparer.Ordinal);
        var clean = new StringBuilder(tool.Length);
        foreach (var c in tool)
        {
            clean.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '_');
        }

        var name = slug + Separator + (clean.Length > 0 ? clean.ToString() : "tool");
        if (name.Length <= MaxName && !used.Contains(name))
        {
            return name;
        }

        for (var attempt = 0; ; attempt++)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(slug + "\n" + tool + "\n" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture))))[..4].ToLowerInvariant();
            var head = name.Length > MaxName - 5 ? name[..(MaxName - 5)] : name;
            var candidate = head + "_" + hash;
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Whether <paramref name="name"/> is a valid environment variable name (letters, digits, '_', not starting with a digit).</summary>
    public static bool IsEnvironmentName(string name) =>
        name.Length is > 0 and <= 128 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Whether <paramref name="name"/> is a valid HTTP header name (RFC 9110 token).</summary>
    public static bool IsHeaderName(string name) =>
        name.Length is > 0 and <= 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal));
}
