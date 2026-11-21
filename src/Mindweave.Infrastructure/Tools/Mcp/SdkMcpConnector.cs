using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Languages;
using Mindweave.Application.Tools.Mcp;
using Mindweave.Infrastructure.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Mindweave.Infrastructure.Tools.Mcp;

/// <summary>
/// Connections through the official MCP SDK (architecture §15.14, decision M1). A local command runs with only the basic
/// environment and the user's variables (decision M12) in its own working folder; an address is reached by Streamable HTTP
/// with SSE as fallback, without redirects, cookies or OAuth (decisions M14, Q15). The client declares no capabilities
/// (decision M10). The SDK gets no logger: what it would log may hold arguments and results.
/// </summary>
public sealed class SdkMcpConnector(AppPaths paths, string? version = null) : IMcpConnector
{
    /// <summary>Environment variables of the app that never reach a server (decision M12).</summary>
    private const string OwnPrefix = "MINDWEAVE_";

    private readonly string _version = version ?? typeof(SdkMcpConnector).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public string WorkingDirectoryOf(string serverId) => Path.Combine(paths.DataDirectory, "mcp", Folder(serverId));

    public void DeleteWorkingDirectory(string serverId)
    {
        try
        {
            var folder = WorkingDirectoryOf(serverId);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; it is under the data directory and holds nothing the app reads.
        }
    }

    /// <summary>The environment a local server gets: the SDK's basic set without the app's own variables, then the user's.</summary>
    public static Dictionary<string, string?> EnvironmentFor(IReadOnlyDictionary<string, string> user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var basic = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in basic)
        {
            if (!name.StartsWith(OwnPrefix, StringComparison.OrdinalIgnoreCase))
            {
                environment[name] = value;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            // An app started from the Dock or Finder gets PATH=/usr/bin:/bin:/usr/sbin:/sbin; npx, uvx and node live elsewhere.
            environment.TryGetValue("PATH", out var path);
            environment["PATH"] = WithUsualDirectories(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists);
        }

        foreach (var (name, value) in user)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>
    /// <paramref name="path"/> with the folders command-line tools are usually installed in appended when they exist and are
    /// missing (Homebrew, /usr/local, the user's ~/.local, ~/.cargo, ~/.volta, ~/.bun, ~/.deno). Unix only.
    /// </summary>
    internal static string WithUsualDirectories(string? path, string home, Func<string, bool> exists)
    {
        var parts = (path ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        string[] usual =
        [
            "/opt/homebrew/bin", "/opt/homebrew/sbin", "/usr/local/bin",
            home + "/.local/bin", home + "/.cargo/bin", home + "/.volta/bin", home + "/.bun/bin", home + "/.deno/bin",
        ];
        foreach (var folder in usual)
        {
            if (!parts.Contains(folder, StringComparer.Ordinal) && exists(folder))
            {
                parts.Add(folder);
            }
        }

        return string.Join(':', parts);
    }

    /// <summary>
    /// A bare command name (no folder) looked up along the server's own <paramref name="path"/>: the process is started with
    /// the app's PATH, which may not hold it (a Dock-started app). Anything else, or not found, is returned as given. Unix only.
    /// </summary>
    internal static string ResolveCommand(string command, string? path, Func<string, bool> isFile)
    {
        if (OperatingSystem.IsWindows() || command.Contains('/', StringComparison.Ordinal) || string.IsNullOrEmpty(path))
        {
            return command;
        }

        foreach (var folder in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, command);
            if (isFile(candidate))
            {
                return candidate;
            }
        }

        return command;
    }

    public async Task<IMcpConnection> ConnectAsync(McpLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var server = launch.Server;
        IClientTransport transport;
        if (server.Kind == McpServerKind.Command)
        {
            var folder = WorkingDirectoryOf(server.Id);
            Directory.CreateDirectory(folder);
            var environment = EnvironmentFor(launch.Environment);
            var command = server.Command ?? throw new McpServerException(McpFailureKind.StartFailed, new BilingualText("No command was entered.", "没有填写命令。"));
            transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = server.Name,
                    Command = ResolveCommand(command, environment.GetValueOrDefault("PATH"), File.Exists),
                    Arguments = [.. server.Arguments],
                    WorkingDirectory = folder,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = environment,
                    ShutdownTimeout = TimeSpan.FromSeconds(5),
                    StandardErrorLines = launch.StandardError,
                },
                NullLoggerFactory.Instance);
        }
        else
        {
            var url = server.Url ?? throw new McpServerException(McpFailureKind.StartFailed, new BilingualText("No address was entered.", "没有填写地址。"));
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (server.HeaderName is { } header && launch.HeaderValue is { } value)
            {
                headers[header] = value;
            }

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = !IsLoopback(url),
                ConnectTimeout = TimeSpan.FromSeconds(30),
            };
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = server.Name,
                    Endpoint = url,
                    TransportMode = HttpTransportMode.AutoDetect,
                    AdditionalHeaders = headers,
                    EnableStandaloneGetStream = false,
                    ConnectionTimeout = TimeSpan.FromSeconds(30),
                },
                client,
                NullLoggerFactory.Instance,
                ownsHttpClient: true);
        }

        try
        {
            var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "Mindweave", Version = _version },
                    Capabilities = new ClientCapabilities(),
                },
                NullLoggerFactory.Instance,
                cancellationToken).ConfigureAwait(false);
            return new Connection(client);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            throw new McpServerException(McpFailureKind.StartFailed, CannotRun(server.Command, ex.Message), ex);
        }
        catch (HttpRequestException ex)
        {
            throw new McpServerException(McpFailureKind.StartFailed, HttpProblem(ex), ex);
        }
        catch (Exception ex) when (ex is McpException or IOException or InvalidOperationException or JsonException)
        {
            if (Inner<Win32Exception>(ex) is { } cannot)
            {
                // The SDK wraps a process that could not start ("Failed to connect transport").
                throw new McpServerException(McpFailureKind.StartFailed, CannotRun(server.Command, cannot.Message), ex);
            }

            if (Inner<HttpRequestException>(ex) is { } http)
            {
                throw new McpServerException(McpFailureKind.StartFailed, HttpProblem(http), ex);
            }

            var cause = ex.InnerException is { } inner && inner.Message != ex.Message ? inner.Message : null;
            // On Windows a command is started through cmd.exe, so a missing one starts and exits instead of failing: name it.
            var command = server.Kind == McpServerKind.Command;
            throw new McpServerException(
                McpFailureKind.StartFailed,
                new BilingualText(
                    "The server didn’t finish starting and the handshake" + (command ? $" (command “{server.Command}”)" : string.Empty) + ": " + ex.Message + (cause is null ? string.Empty : " (" + cause + ")"),
                    "服务器没有完成启动与握手" + (command ? $"（命令“{server.Command}”）" : string.Empty) + "：" + ex.Message + (cause is null ? string.Empty : "（" + cause + "）")),
                ex);
        }
    }

    private static BilingualText CannotRun(string? command, string detail) =>
        new($"Can’t run the command “{command}”: {detail}", $"无法运行命令“{command}”：{detail}");

    private static BilingualText HttpProblem(HttpRequestException ex) => ex.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(
            string.Format(CultureInfo.InvariantCulture, "The server refused the connection ({0}). Check the header; servers that need you to sign in aren’t supported yet.", (int)ex.StatusCode),
            string.Format(CultureInfo.InvariantCulture, "服务器拒绝了连接（{0}）：请检查请求头；需要登录的服务器暂不支持。", (int)ex.StatusCode)),
        { } code => new(
            string.Format(CultureInfo.InvariantCulture, "The server returned {0}: {1}", (int)code, ex.Message),
            string.Format(CultureInfo.InvariantCulture, "服务器返回了 {0}：{1}", (int)code, ex.Message)),
        null => new("Can’t connect to the server: " + ex.Message, "无法连接服务器：" + ex.Message),
    };

    private static T? Inner<T>(Exception ex)
        where T : Exception
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is T found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool IsLoopback(Uri url) =>
        url.IsLoopback || (IPAddress.TryParse(url.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static string Folder(string serverId) =>
        new([.. serverId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);

    private sealed class Connection : IMcpConnection
    {
        private readonly McpClient _client;

        public Connection(McpClient client)
        {
            _client = client;
            Closed = client.Completion.ContinueWith(t => Reason(t.Result), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public Task<BilingualText?> Closed { get; }

        public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return [.. tools.Select(t => Info(t.ProtocolTool))];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Failure(ex);
            }
        }

        public async Task<McpCallResult> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (arguments.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in arguments.EnumerateObject())
                {
                    values[property.Name] = property.Value.Clone();
                }
            }

            CallToolResult result;
            try
            {
                result = await _client.CallToolAsync(new CallToolRequestParams { Name = name, Arguments = values }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Failure(ex);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The SDK cancels pending requests when the connection ends.
                throw new McpServerException(McpFailureKind.ConnectionLost, Lost);
            }

            return new McpCallResult(result.IsError == true, [.. result.Content.Select(Content)]);
        }

        public async ValueTask DisposeAsync() => await _client.DisposeAsync().ConfigureAwait(false);

        private static BilingualText Lost { get; } = new("The server disconnected or exited during the call.", "服务器在调用中断开或退出了。");

        private McpServerException Failure(Exception ex) => ex switch
        {
            McpProtocolException protocol => new McpServerException(McpFailureKind.Protocol, ServerError(protocol.Message), ex),
            _ when _client.Completion.IsCompleted || ex is IOException or ObjectDisposedException or ClientTransportClosedException =>
                new McpServerException(McpFailureKind.ConnectionLost, Lost, ex),
            McpException other => new McpServerException(McpFailureKind.Protocol, ServerError(other.Message), ex),
            _ => new McpServerException(McpFailureKind.ConnectionLost, new BilingualText("The connection to the server failed: " + ex.Message, "与服务器的连接出错：" + ex.Message), ex),
        };

        private static BilingualText ServerError(string detail) => new("The server returned an error: " + detail, "服务器返回了错误：" + detail);

        private static BilingualText? Reason(ClientCompletionDetails details) => details switch
        {
            StdioClientCompletionDetails { ExitCode: { } code } => new(
                string.Format(CultureInfo.InvariantCulture, "the process exited with code {0}", code),
                string.Format(CultureInfo.InvariantCulture, "进程退出，退出码 {0}", code)),
            HttpClientCompletionDetails { HttpStatusCode: { } status } => new(
                string.Format(CultureInfo.InvariantCulture, "the server closed the connection (HTTP {0})", (int)status),
                string.Format(CultureInfo.InvariantCulture, "服务器结束了连接（HTTP {0}）", (int)status)),
            { Exception: { } ex } => new(ex.Message, ex.Message),
            _ => null,
        };

        private static McpToolInfo Info(Tool tool) => new(
            tool.Name,
            tool.Title ?? tool.Annotations?.Title,
            tool.Description,
            tool.InputSchema.ValueKind == JsonValueKind.Undefined ? "{\"type\":\"object\"}" : tool.InputSchema.GetRawText(),
            tool.Annotations?.ReadOnlyHint,
            tool.Annotations?.DestructiveHint,
            tool.Annotations?.IdempotentHint,
            tool.Annotations?.OpenWorldHint);

        private static McpContent Content(ContentBlock block) => block switch
        {
            TextContentBlock text => new McpContent.Text(text.Text),
            ImageContentBlock image => new McpContent.Image(image.DecodedData, image.MimeType),
            ResourceLinkBlock link => new McpContent.ResourceLink(link.Uri, link.Title ?? link.Name, link.MimeType),
            EmbeddedResourceBlock { Resource: TextResourceContents text } => new McpContent.Embedded(text.Uri, text.Text, text.MimeType),
            EmbeddedResourceBlock embedded => new McpContent.Embedded(embedded.Resource.Uri, null, embedded.Resource.MimeType),
            _ => new McpContent.Other(block.Type),
        };
    }
}
