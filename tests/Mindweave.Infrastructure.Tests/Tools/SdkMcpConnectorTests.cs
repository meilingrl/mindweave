using System.Text.Json;
using Mindweave.Application.Tools.Mcp;
using Mindweave.FakeMcpServer;
using Mindweave.Infrastructure.Tools.Mcp;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Tools;

/// <summary>
/// The MCP connector against the fake server as a real process and over HTTP on 127.0.0.1 (docs/design/mcp.md §4): tools
/// and calls, the environment a server gets (decision M12), its working folder, its standard error, a command that does
/// not exist, a server that exits at start, one that exits during a call, and the request header of an address.
/// </summary>
public class SdkMcpConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static McpServerConfig Config(McpServerDraft draft) => new(
        draft.Id, draft.Name, "test", draft.Kind, draft.Command, draft.Arguments ?? [], [.. (draft.Environment ?? new Dictionary<string, string>()).Keys],
        draft.Url is null ? null : new Uri(draft.Url), draft.HeaderName, null, []);

    private static McpLaunch Launch(McpServerDraft draft, List<string>? stderr = null) =>
        new(Config(draft), draft.Environment ?? new Dictionary<string, string>(), draft.HeaderValue, line =>
        {
            if (stderr is not null)
            {
                lock (stderr)
                {
                    stderr.Add(line);
                }
            }
        });

    [Fact]
    public async Task A_local_server_lists_its_tools_and_answers_calls_in_its_own_folder()
    {
        using var dir = new TempDataDirectory();
        var connector = new SdkMcpConnector(dir.Paths);
        var draft = FakeMcp.Command();

        await using var connection = await connector.ConnectAsync(Launch(draft), Ct);
        var tools = await connection.ListToolsAsync(Ct);

        Assert.Contains(tools, t => t.Name == "echo" && t.ReadOnlyHint == true && t.Description == "原样返回 text。");
        Assert.Contains(tools, t => t.Name == "write_note" && t.ReadOnlyHint is null);
        Assert.Contains("\"text\"", tools.Single(t => t.Name == "echo").InputSchema, StringComparison.Ordinal);

        var echo = await connection.CallToolAsync("echo", Args(new { text = "你好" }), Ct);
        Assert.False(echo.IsError);
        Assert.Equal("echo: 你好", Assert.IsType<McpContent.Text>(Assert.Single(echo.Content)).Value);

        var cwd = await connection.CallToolAsync("cwd", Args(new { }), Ct);
        var folder = connector.WorkingDirectoryOf(draft.Id);
        Assert.EndsWith(Path.Combine("mcp", draft.Id), ((McpContent.Text)cwd.Content[0]).Value.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
        Assert.StartsWith(Path.Combine(dir.Paths.DataDirectory, "mcp"), folder, StringComparison.Ordinal);

        connector.DeleteWorkingDirectory(draft.Id);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_local_server_gets_only_the_basic_environment_and_its_own_variables()
    {
        using var dir = new TempDataDirectory();
        Environment.SetEnvironmentVariable("MINDWEAVE_TEST_SECRET", "should-not-pass");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-should-not-pass-either");
        try
        {
            var connector = new SdkMcpConnector(dir.Paths);
            await using var connection = await connector.ConnectAsync(Launch(FakeMcp.Command(environment: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_user_value" })), Ct);
            var env = ((McpContent.Text)(await connection.CallToolAsync("env", Args(new { }), Ct)).Content[0]).Value;

            Assert.Contains("GITHUB_TOKEN=ghp_user_value", env, StringComparison.Ordinal);
            Assert.DoesNotContain("MINDWEAVE_", env, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("OPENAI_API_KEY", env, StringComparison.Ordinal);
            Assert.Contains(env.Split('\n'), l => l.StartsWith("PATH=", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MINDWEAVE_TEST_SECRET", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Standard_error_lines_reach_the_callback()
    {
        using var dir = new TempDataDirectory();
        var lines = new List<string>();
        var connector = new SdkMcpConnector(dir.Paths);
        await using var connection = await connector.ConnectAsync(
            Launch(FakeMcp.Command(environment: new Dictionary<string, string> { ["SECRET_TOKEN"] = "abcdefgh12345678" }, flags: "--stderr-secret"), lines), Ct);
        await connection.ListToolsAsync(Ct);

        await Chat.ChatHarness.UntilAsync(() => { lock (lines) { return lines.Any(l => l.Contains("starting with", StringComparison.Ordinal)); } }, 10_000);
    }

    [Fact]
    public async Task A_command_that_does_not_exist_fails_to_start_with_the_reason()
    {
        using var dir = new TempDataDirectory();
        var connector = new SdkMcpConnector(dir.Paths);
        var draft = new McpServerDraft("不存在", McpServerKind.Command, "/no/such/mindweave-command", []);

        var ex = await Assert.ThrowsAsync<McpServerException>(() => connector.ConnectAsync(Launch(draft), Ct));

        Assert.Equal(McpFailureKind.StartFailed, ex.Kind);
        Assert.Contains("/no/such/mindweave-command", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_exits_at_start_fails_to_start()
    {
        using var dir = new TempDataDirectory();
        var connector = new SdkMcpConnector(dir.Paths);

        var ex = await Assert.ThrowsAsync<McpServerException>(() => connector.ConnectAsync(Launch(FakeMcp.Command(flags: "--exit-at-start")), Ct));

        Assert.Equal(McpFailureKind.StartFailed, ex.Kind);
    }

    [Fact]
    public async Task A_server_that_exits_during_a_call_loses_the_connection_and_says_so()
    {
        using var dir = new TempDataDirectory();
        var connector = new SdkMcpConnector(dir.Paths);
        await using var connection = await connector.ConnectAsync(Launch(FakeMcp.Command()), Ct);

        var ex = await Assert.ThrowsAsync<McpServerException>(() => connection.CallToolAsync("crash", Args(new { }), Ct));

        Assert.Equal(McpFailureKind.ConnectionLost, ex.Kind);
        var reason = await connection.Closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Contains("7", reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_gets_its_header_and_without_it_is_refused()
    {
        using var dir = new TempDataDirectory();
        await using var http = new FakeHttpServer(requiredHeader: ("Authorization", "Bearer fixed-key-123456"));
        var connector = new SdkMcpConnector(dir.Paths);
        var draft = new McpServerDraft("网址", McpServerKind.Http, Url: http.Url.AbsoluteUri, HeaderName: "Authorization", HeaderValue: "Bearer fixed-key-123456");

        await using (var connection = await connector.ConnectAsync(Launch(draft), Ct))
        {
            var echo = await connection.CallToolAsync("echo", Args(new { text = "网址" }), Ct);
            Assert.Equal("echo: 网址", ((McpContent.Text)echo.Content[0]).Value);
        }

        Assert.All(http.Server.Headers.Where(h => h.ContainsKey("Content-Type")), h => Assert.Equal("Bearer fixed-key-123456", h["Authorization"]));

        var refused = await Assert.ThrowsAsync<McpServerException>(() => connector.ConnectAsync(Launch(draft with { HeaderValue = "Bearer wrong" }), Ct));
        Assert.Equal(McpFailureKind.StartFailed, refused.Kind);
        Assert.Contains("401", refused.Message, StringComparison.Ordinal);
    }
}
