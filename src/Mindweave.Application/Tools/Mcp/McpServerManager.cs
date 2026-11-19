using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Credentials;
using Mindweave.Application.Diagnostics;

namespace Mindweave.Application.Tools.Mcp;

/// <summary>Timings of third-party servers (decision M11). Tests shorten them.</summary>
public sealed class McpOptions
{
    /// <summary>Starting (the process, the handshake, listing tools) must finish within this.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>One tool call must return within this.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The wait before connecting again after the n-th exit in a row (the last one repeats).</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } = DefaultBackoff;

    private static readonly TimeSpan[] DefaultBackoff = [.. new[] { 1, 2, 4, 8, 16, 32, 60 }.Select(s => TimeSpan.FromSeconds(s))];

    /// <summary>After <see cref="MaxExits"/> exits within this window, the server is not restarted automatically.</summary>
    public TimeSpan ExitWindow { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxExits { get; init; } = 5;

    /// <summary>How many of the latest standard error lines are kept for settings.</summary>
    public int StandardErrorLines { get; init; } = 20;

    /// <summary>Longest standard error line kept or logged.</summary>
    public int MaxLineLength { get; init; } = 500;
}

/// <summary>Where a server is (requirements §10.4.6): 未启动, 正在启动, 已连接, 出错, 反复退出已停止.</summary>
public enum McpServerState
{
    NotStarted,
    Starting,
    Connected,
    Failed,
    Halted,
}

/// <summary>A server's state, its last error in words, its latest standard error lines (scrubbed), and its tools when connected.</summary>
public sealed record McpServerStatus(McpServerState State, string? Error, IReadOnlyList<string> StandardError, IReadOnlyList<McpToolInfo>? Tools)
{
    public static McpServerStatus NotStarted { get; } = new(McpServerState.NotStarted, null, [], null);
}

/// <summary>
/// Keeps the connections to third-party servers (architecture §15.14, decision M11): a server is started (or connected to) on
/// demand, once at a time; when it exits, the next connection waits 1, 2, 4… up to 60 seconds; after five exits within ten
/// minutes it is not restarted until the user starts it. Every connection is closed when the app quits (the host disposes
/// this). Its standard error goes to the log without secrets (decision M13).
/// </summary>
public sealed partial class McpServerManager : IAsyncDisposable
{
    private readonly IMcpConnector _connector;
    private readonly IMcpServers _servers;
    private readonly McpOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<McpServerManager> _logger;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public McpServerManager(IMcpConnector connector, IMcpServers servers, McpOptions options, IClock clock, ILogger<McpServerManager> logger)
    {
        _connector = connector;
        _servers = servers;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>A server's status changed (raised on any thread), with its ID.</summary>
    public event EventHandler<string>? StatusChanged;

    public McpServerStatus StatusOf(string serverId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(serverId, out var entry) ? entry.Status() : McpServerStatus.NotStarted;
        }
    }

    /// <summary>
    /// 启动／连接 in settings: connects (a halted server is allowed again), lists the tools, and turns off enabled tools whose
    /// definition changed (decision M6). Throws <see cref="McpServerException"/> with the reason.
    /// </summary>
    public async Task<IReadOnlyList<McpToolInfo>> StartAsync(string serverId, CancellationToken cancellationToken)
    {
        var server = await _servers.GetAsync(serverId, cancellationToken).ConfigureAwait(false)
            ?? throw new McpServerException(McpFailureKind.StartFailed, "这个服务器已被移除。");
        var (_, tools) = await ConnectAsync(server, manual: true, cancellationToken).ConfigureAwait(false);
        return tools;
    }

    /// <summary>停止: closes the connection (ends the process); the server starts again on demand.</summary>
    public async Task StopAsync(string serverId)
    {
        IMcpConnection? connection;
        lock (_gate)
        {
            if (!_entries.TryGetValue(serverId, out var entry))
            {
                return;
            }

            connection = entry.Connection;
            entry.Connection = null;
            entry.Starting = null;
            entry.Attempt = null;
            entry.Exits.Clear();
            entry.State = McpServerState.NotStarted;
            entry.Error = null;
            entry.Tools = null;
        }

        Raise(serverId);
        if (connection is not null)
        {
            await CloseAsync(connection).ConfigureAwait(false);
        }
    }

    /// <summary>移除: stops the server, then removes it with its secrets and working folder (decision M4).</summary>
    public async Task RemoveAsync(string serverId, CancellationToken cancellationToken)
    {
        await StopAsync(serverId).ConfigureAwait(false);
        lock (_gate)
        {
            _entries.Remove(serverId);
        }

        await _servers.RemoveAsync(serverId, cancellationToken).ConfigureAwait(false);
        Raise(serverId);
    }

    /// <summary>
    /// Calls an enabled tool (starting the server on demand). Before the call the server's list is compared with the
    /// definition the user enabled (decision M6); a changed or vanished tool is turned off and the call refused. Throws
    /// <see cref="McpServerException"/> (<see cref="McpFailureKind.CallTimeout"/> or <see cref="McpFailureKind.ConnectionLost"/>
    /// once the call went out) or <see cref="ToolFailureException"/> for a changed definition.
    /// </summary>
    public async Task<McpCallResult> CallAsync(McpServerConfig server, McpToolSetting tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        var connection = await VerifyAsync(server, tool, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CallTimeout);
        try
        {
            return await connection.CallToolAsync(tool.Name, arguments, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpServerException(McpFailureKind.CallTimeout, Seconds("超时（{0} 秒内没有返回）。", _options.CallTimeout));
        }
    }

    /// <summary>
    /// Connects (starting the server on demand), lists its tools and checks that <paramref name="tool"/> is still enabled and
    /// offered as it was enabled (decision M6). Used before an approval card and again right before the call.
    /// </summary>
    public async Task<IMcpConnection> VerifyAsync(McpServerConfig server, McpToolSetting tool, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(tool);
        var (connection, _) = await ConnectAsync(server, manual: false, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<McpToolInfo> listed;
        using (var listing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            listing.CancelAfter(_options.StartTimeout);
            try
            {
                listed = await connection.ListToolsAsync(listing.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new McpServerException(McpFailureKind.Timeout, Seconds("服务器在 {0} 秒内没有列出它的工具。", _options.StartTimeout));
            }
            catch (McpServerException ex) when (ex.Kind == McpFailureKind.ConnectionLost)
            {
                // Lost before the call went out: nothing was done.
                throw new McpServerException(McpFailureKind.StartFailed, ex.Message, ex);
            }
        }

        SetTools(server.Id, listed);
        await _servers.ReconcileAsync(server.Id, listed, CancellationToken.None).ConfigureAwait(false);
        var current = (await _servers.GetAsync(server.Id, CancellationToken.None).ConfigureAwait(false))?.Tool(tool.Name);
        if (current is not { Enabled: true } || current.Hash != tool.Hash)
        {
            // Changed or gone at the server (decision M6), or turned off in settings since the question was accepted.
            throw new ToolFailureException((current?.DisabledReason ?? "这个工具已在设置中停用。") + "这次调用没有执行。");
        }

        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        List<IMcpConnection> open;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            open = [.. _entries.Values.Select(e => e.Connection).OfType<IMcpConnection>()];
            foreach (var entry in _entries.Values)
            {
                entry.Connection = null;
            }
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(open.Select(CloseAsync)).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    /// <summary>The open connection, or a new one (shared by everyone who asks while it starts), and the tools it listed.</summary>
    private async Task<(IMcpConnection Connection, IReadOnlyList<McpToolInfo> Tools)> ConnectAsync(McpServerConfig server, bool manual, CancellationToken cancellationToken)
    {
        Task<(IMcpConnection, IReadOnlyList<McpToolInfo>)> starting;
        TimeSpan wait = TimeSpan.Zero;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(server.Id, out var entry))
            {
                entry = _entries[server.Id] = new Entry(_options.StandardErrorLines);
            }

            if (entry.Connection is { } open && entry.Tools is { } tools)
            {
                return (open, tools);
            }

            if (entry.Starting is null)
            {
                if (manual)
                {
                    entry.Exits.Clear();
                }
                else if (entry.State == McpServerState.Halted)
                {
                    throw new McpServerException(McpFailureKind.StartFailed, entry.Error ?? HaltedMessage());
                }
                else if (entry.Exits.Count > 0)
                {
                    var delay = _options.Backoff.Count == 0 ? TimeSpan.Zero : _options.Backoff[Math.Min(entry.Exits.Count, _options.Backoff.Count) - 1];
                    wait = entry.Exits[^1] + delay - _clock.UtcNow;
                }

                entry.State = McpServerState.Starting;
                entry.Error = null;
                var attempt = new object();
                var pause = wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
                entry.Attempt = attempt;
                entry.Starting = Task.Run(() => StartCoreAsync(server, entry, attempt, pause));
            }

            starting = entry.Starting;
        }

        Raise(server.Id);
        return await starting.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(IMcpConnection, IReadOnlyList<McpToolInfo>)> StartCoreAsync(McpServerConfig server, Entry entry, object attempt, TimeSpan wait)
    {
        IMcpConnection? connection = null;
        try
        {
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _shutdown.Token).ConfigureAwait(false);
            }

            McpServerSecrets secrets;
            try
            {
                secrets = await _servers.ReadSecretsAsync(server, _shutdown.Token).ConfigureAwait(false);
            }
            catch (CredentialUnavailableException ex)
            {
                throw new McpServerException(McpFailureKind.StartFailed, ex.Message, ex);
            }

            var known = secrets.Values.ToList();
            var launch = new McpLaunch(server, secrets.Environment, secrets.HeaderValue, line => OnStandardError(server, entry, known, line));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            limit.CancelAfter(_options.StartTimeout);
            IReadOnlyList<McpToolInfo> tools;
            try
            {
                connection = await _connector.ConnectAsync(launch, limit.Token).ConfigureAwait(false);
                tools = await connection.ListToolsAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
            {
                throw new McpServerException(McpFailureKind.Timeout, Seconds("服务器在 {0} 秒内没有完成启动与握手。", _options.StartTimeout));
            }

            await _servers.ReconcileAsync(server.Id, tools, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(entry.Attempt, attempt))
                {
                    // Stopped (or the app quit) while it started.
                    throw new McpServerException(McpFailureKind.StartFailed, "服务器已停止。");
                }

                entry.Connection = connection;
                entry.Tools = tools;
                entry.State = McpServerState.Connected;
                entry.Error = null;
                entry.Starting = null;
                entry.Attempt = null;
            }

            var opened = connection;
            connection = null;
            _ = WatchAsync(server, entry, opened);
            Raise(server.Id);
            return (opened, tools);
        }
#pragma warning disable CA1031 // Whatever ends a start is recorded as the server's error and thrown as an McpServerException.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var message = ex switch
            {
                McpServerException known => known.Message,
                OperationCanceledException => "应用正在退出。",
                _ => "启动失败：" + ex.Message,
            };
            lock (_gate)
            {
                if (ReferenceEquals(entry.Attempt, attempt))
                {
                    entry.Starting = null;
                    entry.Attempt = null;
                    Exited(entry, Redaction.Scrub(message));
                }
            }

            if (connection is not null)
            {
                await CloseAsync(connection).ConfigureAwait(false);
            }

            Raise(server.Id);
            LogStartFailed(_logger, server.Name, Redaction.Scrub(message));
            throw ex is McpServerException ? ex : new McpServerException(McpFailureKind.StartFailed, message, ex);
        }
    }

    /// <summary>When the connection ends (the process exited), the server counts an exit and is started again on demand.</summary>
    private async Task WatchAsync(McpServerConfig server, Entry entry, IMcpConnection connection)
    {
        var reason = await connection.Closed.ConfigureAwait(false);
        var counted = false;
        lock (_gate)
        {
            if (ReferenceEquals(entry.Connection, connection))
            {
                entry.Connection = null;
                entry.Tools = null;
                Exited(entry, "服务器已退出" + (string.IsNullOrWhiteSpace(reason) ? "。" : "：" + Redaction.Scrub(reason)));
                counted = true;
            }
        }

        if (counted)
        {
            LogExited(_logger, server.Name, reason ?? string.Empty);
            Raise(server.Id);
            await CloseAsync(connection).ConfigureAwait(false);
        }
    }

    /// <summary>Counts an exit (or a failed start) and decides whether the server may be started again automatically.</summary>
    private void Exited(Entry entry, string error)
    {
        var now = _clock.UtcNow;
        entry.Exits.RemoveAll(t => now - t > _options.ExitWindow);
        entry.Exits.Add(now);
        if (entry.Exits.Count >= _options.MaxExits)
        {
            entry.State = McpServerState.Halted;
            entry.Error = HaltedMessage() + "最后一次：" + error;
        }
        else
        {
            entry.State = McpServerState.Failed;
            entry.Error = error;
        }
    }

    private string HaltedMessage() => string.Format(
        CultureInfo.InvariantCulture,
        "服务器在 {0} 分钟内退出或启动失败了 {1} 次，已停止自动重启；请检查后在设置中点“启动”。",
        Math.Round(_options.ExitWindow.TotalMinutes),
        _options.MaxExits);

    private void SetTools(string serverId, IReadOnlyList<McpToolInfo> tools)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(serverId, out var entry) && entry.Connection is not null)
            {
                entry.Tools = tools;
            }
        }
    }

    private void OnStandardError(McpServerConfig server, Entry entry, IReadOnlyList<string> secrets, string line)
    {
        var clean = secrets.Aggregate(line, (text, secret) => text.Replace(secret, Redaction.Mask, StringComparison.Ordinal));
        clean = Redaction.Scrub(clean);
        if (clean.Length > _options.MaxLineLength)
        {
            clean = clean[.._options.MaxLineLength] + "…";
        }

        lock (_gate)
        {
            entry.StandardError.Enqueue(clean);
            while (entry.StandardError.Count > entry.MaxLines)
            {
                entry.StandardError.Dequeue();
            }
        }

        LogStandardError(_logger, server.Name, clean);
    }

    private static string Seconds(string format, TimeSpan span) =>
        string.Format(CultureInfo.InvariantCulture, format, Math.Round(span.TotalSeconds, span < TimeSpan.FromSeconds(1) ? 1 : 0));

    private void Raise(string serverId)
    {
        try
        {
            StatusChanged?.Invoke(this, serverId);
        }
#pragma warning disable CA1031 // A subscriber's failure must not break a call or a start.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSubscriberFailed(_logger, ex);
        }
    }

    private async Task CloseAsync(IMcpConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Closing is best effort; a failure is logged and the connection forgotten.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCloseFailed(_logger, ex);
        }
    }

    private sealed class Entry(int maxLines)
    {
        public int MaxLines { get; } = maxLines;

        public IMcpConnection? Connection { get; set; }

        public Task<(IMcpConnection, IReadOnlyList<McpToolInfo>)>? Starting { get; set; }

        /// <summary>The start under way; a start that is no longer it (stopped meanwhile) leaves nothing behind.</summary>
        public object? Attempt { get; set; }

        public McpServerState State { get; set; }

        public string? Error { get; set; }

        public IReadOnlyList<McpToolInfo>? Tools { get; set; }

        public List<DateTimeOffset> Exits { get; } = [];

        public Queue<string> StandardError { get; } = new();

        public McpServerStatus Status() => new(State, Error, [.. StandardError], Connection is null ? null : Tools);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server {Server} (stderr): {Line}")]
    private static partial void LogStandardError(ILogger logger, string server, string line);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server {Server} could not start: {Reason}")]
    private static partial void LogStartFailed(ILogger logger, string server, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server {Server} exited: {Reason}")]
    private static partial void LogExited(ILogger logger, string server, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closing an MCP connection failed")]
    private static partial void LogCloseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A subscriber to MCP server status failed")]
    private static partial void LogSubscriberFailed(ILogger logger, Exception exception);
}
