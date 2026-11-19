using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Mindweave.FakeMcpServer;

/// <summary>
/// The fake server as a Streamable HTTP endpoint on 127.0.0.1 (tests only): each POST carries one JSON-RPC message and gets
/// a JSON answer, or 202 for a notification; GET (the optional event stream) is 405. A header named <c>X-Require</c> in
/// <paramref name="requiredHeader"/> makes every request without it a 401.
/// </summary>
public sealed class FakeHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly (string Name, string Value)? _required;

    public FakeHttpServer(FakeOptions? options = null, (string Name, string Value)? requiredHeader = null)
    {
        Server = new FakeServer(options ?? new FakeOptions());
        _required = requiredHeader;
        var port = FreePort();
        Url = new Uri($"http://127.0.0.1:{port}/mcp/");
        _listener.Prefixes.Add(Url.AbsoluteUri);
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public Uri Url { get; }

    public FakeServer Server { get; }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _listener.Close();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var headers = context.Request.Headers.AllKeys.OfType<string>().ToDictionary(k => k, k => context.Request.Headers[k] ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            lock (Server.Headers)
            {
                Server.Headers.Add(headers);
            }

            if (_required is { } required && (!headers.TryGetValue(required.Name, out var given) || given != required.Value))
            {
                response.StatusCode = 401;
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                response.StatusCode = context.Request.HttpMethod == "DELETE" ? 200 : 405;
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var message = JsonNode.Parse(body);
            var reply = message is null ? null : await Server.HandleAsync(message, _stop.Token);
            if (reply is null)
            {
                response.StatusCode = 202;
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException or IOException or System.Text.Json.JsonException)
        {
            response.StatusCode = 500;
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }
}
