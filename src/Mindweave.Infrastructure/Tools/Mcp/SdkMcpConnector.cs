using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
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

        foreach (var (name, value) in user)
        {
            environment[name] = value;
        }

        return environment;
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
            transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = server.Name,
                    Command = server.Command ?? throw new McpServerException(McpFailureKind.StartFailed, "没有填写命令。"),
                    Arguments = [.. server.Arguments],
                    WorkingDirectory = folder,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = EnvironmentFor(launch.Environment),
                    ShutdownTimeout = TimeSpan.FromSeconds(5),
                    StandardErrorLines = launch.StandardError,
                },
                NullLoggerFactory.Instance);
        }
        else
        {
            var url = server.Url ?? throw new McpServerException(McpFailureKind.StartFailed, "没有填写地址。");
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
            throw new McpServerException(McpFailureKind.StartFailed, $"无法运行命令“{server.Command}”：{ex.Message}", ex);
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
                throw new McpServerException(McpFailureKind.StartFailed, $"无法运行命令“{server.Command}”：{cannot.Message}", ex);
            }

            if (Inner<HttpRequestException>(ex) is { } http)
            {
                throw new McpServerException(McpFailureKind.StartFailed, HttpProblem(http), ex);
            }

            var inner = ex.InnerException is { } cause && cause.Message != ex.Message ? "（" + cause.Message + "）" : string.Empty;
            throw new McpServerException(McpFailureKind.StartFailed, "服务器没有完成启动与握手：" + ex.Message + inner, ex);
        }
    }

    private static string HttpProblem(HttpRequestException ex) => ex.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            string.Format(CultureInfo.InvariantCulture, "服务器拒绝了连接（{0}）：请检查请求头；需要登录的服务器暂不支持。", (int)ex.StatusCode),
        { } code => string.Format(CultureInfo.InvariantCulture, "服务器返回了 {0}：{1}", (int)code, ex.Message),
        null => "无法连接服务器：" + ex.Message,
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

        public Task<string?> Closed { get; }

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
                throw new McpServerException(McpFailureKind.ConnectionLost, "服务器在调用中断开或退出了。");
            }

            return new McpCallResult(result.IsError == true, [.. result.Content.Select(Content)]);
        }

        public async ValueTask DisposeAsync() => await _client.DisposeAsync().ConfigureAwait(false);

        private McpServerException Failure(Exception ex) => ex switch
        {
            McpProtocolException protocol => new McpServerException(McpFailureKind.Protocol, "服务器返回了错误：" + protocol.Message, ex),
            _ when _client.Completion.IsCompleted || ex is IOException or ObjectDisposedException or ClientTransportClosedException =>
                new McpServerException(McpFailureKind.ConnectionLost, "服务器在调用中断开或退出了。", ex),
            McpException other => new McpServerException(McpFailureKind.Protocol, "服务器返回了错误：" + other.Message, ex),
            _ => new McpServerException(McpFailureKind.ConnectionLost, "与服务器的连接出错：" + ex.Message, ex),
        };

        private static string? Reason(ClientCompletionDetails details) => details switch
        {
            StdioClientCompletionDetails { ExitCode: { } code } => string.Format(CultureInfo.InvariantCulture, "进程退出，退出码 {0}", code),
            HttpClientCompletionDetails { HttpStatusCode: { } status } => string.Format(CultureInfo.InvariantCulture, "服务器结束了连接（HTTP {0}）", (int)status),
            { Exception: { } ex } => ex.Message,
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
