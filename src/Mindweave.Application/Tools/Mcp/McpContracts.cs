using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mindweave.Application.Tools.Mcp;

/// <summary>How a third-party server is reached (requirements §10.4.6): a local command, or an address.</summary>
public enum McpServerKind
{
    Command,
    Http,
}

/// <summary>
/// A tool as a server describes it (untrusted, architecture §15.7): its name, an optional title and description, the JSON
/// Schema of its input (JSON text) and the four hints. <see cref="Hash"/> is what enabling it records (decision M5): any
/// change of these is a changed definition (decision M6).
/// </summary>
public sealed record McpToolInfo(
    string Name,
    string? Title,
    string? Description,
    string InputSchema,
    bool? ReadOnlyHint = null,
    bool? DestructiveHint = null,
    bool? IdempotentHint = null,
    bool? OpenWorldHint = null)
{
    /// <summary>A stable hash of the definition: name, title, description, schema (as given) and the hints.</summary>
    public string Hash()
    {
        var text = string.Join(
            "\n",
            Name,
            Title ?? string.Empty,
            Description ?? string.Empty,
            InputSchema,
            Hint(ReadOnlyHint),
            Hint(DestructiveHint),
            Hint(IdempotentHint),
            Hint(OpenWorldHint));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// The risk a tool gets when it is enabled (architecture §15.7): declared read-only and not declared destructive is a
    /// network read; anything else is a side effect.
    /// </summary>
    public Core.Runs.ToolRisk DefaultRisk =>
        ReadOnlyHint == true && DestructiveHint != true ? Core.Runs.ToolRisk.NetworkRead : Core.Runs.ToolRisk.SideEffect;

    /// <summary>Whether the server declares the tool read-only (and not destructive), for the settings list.</summary>
    public bool DeclaresReadOnly => ReadOnlyHint == true && DestructiveHint != true;

    private static string Hint(bool? value) => value switch
    {
        true => "1",
        false => "0",
        null => "-",
    };
}

/// <summary>One item of a tool's result (MCP content blocks), already decoded.</summary>
public abstract record McpContent
{
    private McpContent()
    {
    }

    public sealed record Text(string Value) : McpContent;

    public sealed record Image(ReadOnlyMemory<byte> Data, string MediaType) : McpContent;

    public sealed record ResourceLink(string Uri, string? Name, string? MediaType) : McpContent;

    /// <summary>A resource embedded in the result: its text when it is text, otherwise null with its media type.</summary>
    public sealed record Embedded(string Uri, string? Body, string? MediaType) : McpContent;

    /// <summary>Anything else (audio, unknown types): only its type is kept.</summary>
    public sealed record Other(string Type) : McpContent;
}

/// <summary>What a tool call returned: its content and whether the server marked it as an error.</summary>
public sealed record McpCallResult(bool IsError, IReadOnlyList<McpContent> Content);

/// <summary>
/// What a connection needs (architecture §15.14): the server, the secret values of its environment and header (decision M3),
/// and where its standard error lines go (already in order; the connector does not scrub them, the manager does).
/// </summary>
public sealed record McpLaunch(
    McpServerConfig Server,
    IReadOnlyDictionary<string, string> Environment,
    string? HeaderValue,
    Action<string> StandardError);

/// <summary>Opens connections to third-party servers. The only place the MCP library is used is behind this (decision M1).</summary>
public interface IMcpConnector
{
    /// <summary>
    /// Starts the server (a local command) or connects to it (an address), completes the handshake and returns the
    /// connection. Throws <see cref="McpServerException"/> when it cannot (no such command, no handshake, refused).
    /// </summary>
    Task<IMcpConnection> ConnectAsync(McpLaunch launch, CancellationToken cancellationToken);

    /// <summary>The server's own working folder under the data directory (decision M12); created when it starts.</summary>
    string WorkingDirectoryOf(string serverId);

    /// <summary>Removes the server's working folder (when the server is removed, decision M4). Never throws.</summary>
    void DeleteWorkingDirectory(string serverId);
}

/// <summary>One open connection to a server.</summary>
public interface IMcpConnection : IAsyncDisposable
{
    /// <summary>All tools the server offers now.</summary>
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Calls a tool. Throws <see cref="McpServerException"/> with <see cref="McpFailureKind.ConnectionLost"/> when the server
    /// went away during the call, <see cref="McpFailureKind.Protocol"/> when it answered with a protocol error.
    /// </summary>
    Task<McpCallResult> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken);

    /// <summary>Completes when the connection ends (the process exited, the address closed it), with a reason in words.</summary>
    Task<string?> Closed { get; }
}

/// <summary>Why talking to a server failed.</summary>
public enum McpFailureKind
{
    /// <summary>The server could not be started or connected to (no such command, refused, an HTTP error).</summary>
    StartFailed,

    /// <summary>Starting, the handshake or listing tools took longer than allowed.</summary>
    Timeout,

    /// <summary>The server went away (exited, closed the connection) during a request.</summary>
    ConnectionLost,

    /// <summary>A tool call did not return in time (the server may still be working on it).</summary>
    CallTimeout,

    /// <summary>The server answered a request with an error.</summary>
    Protocol,
}

/// <summary>Talking to a server failed; the message says why in words.</summary>
public sealed class McpServerException : Exception
{
    public McpServerException()
    {
    }

    public McpServerException(string message)
        : base(message)
    {
    }

    public McpServerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public McpServerException(McpFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public McpFailureKind Kind { get; } = McpFailureKind.StartFailed;
}
