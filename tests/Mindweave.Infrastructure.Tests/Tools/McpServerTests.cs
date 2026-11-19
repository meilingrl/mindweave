using System.Diagnostics;
using System.Globalization;
using Mindweave.Application.Tools.Mcp;
using Mindweave.Core.Runs;
using Mindweave.Infrastructure.Tests.Chat;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Tools;

/// <summary>
/// 设置 › 工具 › MCP 服务器 and the servers' lifecycle (requirements §10.4.6, docs/design/mcp.md M2–M6, M9, M11): what the
/// confirmation shows and refuses, names, one start at a time, repeated exits, and every process ended when the app quits.
/// </summary>
public class McpServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_confirmation_shows_the_full_command_line_the_folder_and_only_the_names_of_the_environment()
    {
        await using var h = await ChatHarness.StartAsync();
        var draft = new McpServerDraft("文件", McpServerKind.Command, "npx", ["-y", "@modelcontextprotocol/server-filesystem", "/Users/me/notes"], new Dictionary<string, string> { ["TOKEN"] = "secret-value-123" });

        var preview = h.Mcp.Preview(draft);

        Assert.True(preview.CanAdd);
        Assert.Equal("npx", preview.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "/Users/me/notes"], preview.Arguments);
        Assert.Equal(["TOKEN"], preview.EnvironmentNames);
        Assert.Equal(Path.Combine(h.DataDirectory, "mcp", draft.Id), preview.WorkingDirectory);

        var added = await h.Mcp.AddAsync(draft, Ct);
        Assert.Equal(("文件", "mcp", McpServerKind.Command), (added.Name, added.Slug, added.Kind));
        Assert.Equal(McpServerState.NotStarted, h.McpServers.StatusOf(added.Id).State);
        Assert.False(Directory.Exists(preview.WorkingDirectory));
    }

    [Fact]
    public async Task Spaces_around_an_argument_line_are_dropped_and_blank_lines_ignored()
    {
        await using var h = await ChatHarness.StartAsync();
        var draft = new McpServerDraft("文件", McpServerKind.Command, " npx ", ["-y ", "  @scope/server  ", "   ", "/Users/me/my notes"]);

        var preview = h.Mcp.Preview(draft);
        var added = await h.Mcp.AddAsync(draft, Ct);

        Assert.Equal("npx", preview.Command);
        Assert.Equal(["-y", "@scope/server", "/Users/me/my notes"], preview.Arguments);
        Assert.Equal(["-y", "@scope/server", "/Users/me/my notes"], added.Arguments);
    }

    [Theory]
    [InlineData("", McpServerKind.Command, "npx", null, null, null, "名称")]
    [InlineData("a", McpServerKind.Command, " ", null, null, null, "命令")]
    [InlineData("a", McpServerKind.Http, null, "ftp://x.example", null, null, "http")]
    [InlineData("a", McpServerKind.Http, null, "https://user:pw@x.example/mcp", null, null, "用户名")]
    [InlineData("a", McpServerKind.Http, null, "https://x.example/mcp", "Bad Header", "v", "不合法")]
    [InlineData("a", McpServerKind.Http, null, "https://x.example/mcp", "Authorization", null, "值")]
    public async Task A_draft_that_cannot_run_is_refused_with_the_reason(string name, McpServerKind kind, string? command, string? url, string? header, string? value, string problem)
    {
        await using var h = await ChatHarness.StartAsync();
        var draft = new McpServerDraft(name, kind, command, [], null, url, header, value);

        var preview = h.Mcp.Preview(draft);

        Assert.False(preview.CanAdd);
        Assert.Contains(problem, preview.Problem, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => h.Mcp.AddAsync(draft, Ct));
    }

    [Fact]
    public async Task An_address_shows_the_address_and_the_headers_name_and_keeps_the_value_secret()
    {
        await using var h = await ChatHarness.StartAsync();
        var draft = new McpServerDraft("Remote API", McpServerKind.Http, Url: "https://mcp.example.com/v1", HeaderName: "Authorization", HeaderValue: "Bearer abcdefgh-123");

        var preview = h.Mcp.Preview(draft);
        var added = await h.Mcp.AddAsync(draft, Ct);

        Assert.Equal((new Uri("https://mcp.example.com/v1"), "Authorization"), (preview.Url, preview.HeaderName));
        Assert.Equal("remote_api", added.Slug);
        Assert.DoesNotContain("abcdefgh-123", await h.Preferences.GetAsync(McpServerSettings.Key, Ct), StringComparison.Ordinal);
        Assert.Equal("Bearer abcdefgh-123", (await h.Mcp.ReadSecretsAsync(added, Ct)).HeaderValue);
    }

    [Theory]
    [InlineData("GitHub Tools", "github_tools")]
    [InlineData("文件系统", "mcp")]
    [InlineData("a very long server name indeed", "a_very_long_serv")]
    [InlineData("--x--", "x")]
    public void Short_names_take_the_ascii_letters_and_digits(string name, string slug) =>
        Assert.Equal(slug, McpNames.Slug(name, []));

    [Fact]
    public void Names_are_unique_valid_and_at_most_64_characters()
    {
        Assert.Equal("files2", McpNames.Slug("files", ["files"]));
        Assert.Equal("files__read_file", McpNames.ToolName("files", "read.file", []));
        var taken = McpNames.ToolName("files", "echo", ["files__echo"]);
        Assert.StartsWith("files__echo_", taken, StringComparison.Ordinal);
        var longName = McpNames.ToolName("files", new string('x', 100), []);
        Assert.True(longName.Length <= McpNames.MaxName);
        Assert.Matches("^[A-Za-z0-9_-]+$", longName);
        Assert.Matches("^[A-Za-z0-9_-]+$", McpNames.ToolName("mcp", "读取 文件", []));
    }

    [Fact]
    public async Task A_tools_risk_comes_from_its_hints_when_enabled_and_the_users_choice_is_kept()
    {
        await using var h = await ChatHarness.StartAsync();
        var server = await h.Mcp.AddAsync(FakeMcp.Command("files"), Ct);
        var readOnly = new McpToolInfo("read", null, "读", """{"type":"object"}""", ReadOnlyHint: true);
        var unknown = new McpToolInfo("write", null, "写", """{"type":"object"}""");

        Assert.Equal(ToolRisk.NetworkRead, (await h.Mcp.EnableToolAsync(server.Id, readOnly, Ct)).Risk);
        Assert.Equal(ToolRisk.SideEffect, (await h.Mcp.EnableToolAsync(server.Id, unknown, Ct)).Risk);
        await h.Mcp.SetRiskAsync(server.Id, "read", ToolRisk.LocalRead, Ct);
        Assert.Equal(ToolRisk.LocalRead, (await h.Mcp.GetAsync(server.Id, Ct))!.Tool("read")!.Risk);

        // Turned off and on again: the user's choice is forgotten with the setting; the name stays the same shape.
        await h.Mcp.DisableToolAsync(server.Id, "read", Ct);
        Assert.Null((await h.Mcp.GetAsync(server.Id, Ct))!.Tool("read"));
        Assert.Equal(("files__read", ToolRisk.NetworkRead), ((await h.Mcp.EnableToolAsync(server.Id, readOnly, Ct)).ModelName, ToolRisk.NetworkRead));
    }

    [Fact]
    public async Task Starting_twice_at_once_starts_one_process()
    {
        await using var h = await ChatHarness.StartAsync();
        var server = await h.Mcp.AddAsync(FakeMcp.Command("files", flags: "--pid-file"), Ct);

        var both = await Task.WhenAll(h.McpServers.StartAsync(server.Id, Ct), h.McpServers.StartAsync(server.Id, Ct));

        Assert.Equal(both[0].Count, both[1].Count);
        Assert.Single(Directory.GetFiles(Path.Combine(h.DataDirectory, "mcp", server.Id), "pid.txt"));
    }

    [Fact]
    public async Task After_five_failed_starts_the_server_is_halted_until_started_by_hand()
    {
        await using var h = await ChatHarness.StartAsync();
        var server = await h.Mcp.AddAsync(FakeMcp.Command("broken", flags: "--exit-at-start"), Ct);
        var tool = await h.Mcp.EnableToolAsync(server.Id, new McpToolInfo("echo", null, "回声", """{"type":"object"}""", ReadOnlyHint: true), Ct);
        server = (await h.Mcp.GetAsync(server.Id, Ct))!;

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<McpServerException>(() => h.McpServers.VerifyAsync(server, tool, Ct));
        }

        var status = h.McpServers.StatusOf(server.Id);
        Assert.Equal(McpServerState.Halted, status.State);
        Assert.Contains("已停止自动重启", status.Error, StringComparison.Ordinal);
        var refused = await Assert.ThrowsAsync<McpServerException>(() => h.McpServers.VerifyAsync(server, tool, Ct));
        Assert.Contains("已停止自动重启", refused.Message, StringComparison.Ordinal);

        // 启动 by hand tries again (and fails again, but no longer refuses at once).
        await Assert.ThrowsAsync<McpServerException>(() => h.McpServers.StartAsync(server.Id, Ct));
        Assert.Equal(McpServerState.Failed, h.McpServers.StatusOf(server.Id).State);
    }

    [Fact]
    public async Task A_server_that_never_answers_the_handshake_times_out()
    {
        await using var h = await ChatHarness.StartAsync();
        await using var manager = new McpServerManager(
            new Infrastructure.Tools.Mcp.SdkMcpConnector(h.Paths), h.Mcp,
            new McpOptions { StartTimeout = TimeSpan.FromSeconds(2) }, new Application.SystemClock(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<McpServerManager>.Instance);
        var server = await h.Mcp.AddAsync(FakeMcp.Command("silent", flags: ["--no-handshake", "--pid-file"]), Ct);

        var ex = await Assert.ThrowsAsync<McpServerException>(() => manager.StartAsync(server.Id, Ct));

        Assert.Equal(McpFailureKind.Timeout, ex.Kind);
        Assert.Contains("2 秒内没有完成启动与握手", ex.Message, StringComparison.Ordinal);
        await AssertEndsAsync(Path.Combine(h.DataDirectory, "mcp", server.Id, "pid.txt"));
    }

    [Fact]
    public async Task Quitting_ends_every_server_process()
    {
        await using var h = await ChatHarness.StartAsync();
        var first = await h.Mcp.AddAsync(FakeMcp.Command("one", flags: "--pid-file"), Ct);
        var second = await h.Mcp.AddAsync(FakeMcp.Command("two", flags: "--pid-file"), Ct);
        await h.McpServers.StartAsync(first.Id, Ct);
        await h.McpServers.StartAsync(second.Id, Ct);
        var pids = new[] { first, second }.Select(s => Path.Combine(h.DataDirectory, "mcp", s.Id, "pid.txt")).ToList();
        Assert.All(pids, p => Assert.True(Running(p)));

        await h.McpServers.DisposeAsync();

        foreach (var pid in pids)
        {
            await AssertEndsAsync(pid);
        }
    }

    [Fact]
    public async Task Stopping_a_server_ends_its_process_and_it_starts_again_on_demand()
    {
        await using var h = await ChatHarness.StartAsync();
        var server = await h.Mcp.AddAsync(FakeMcp.Command("one", flags: "--pid-file"), Ct);
        await h.McpServers.StartAsync(server.Id, Ct);
        var pid = Path.Combine(h.DataDirectory, "mcp", server.Id, "pid.txt");

        await h.McpServers.StopAsync(server.Id);

        await AssertEndsAsync(pid);
        Assert.Equal(McpServerState.NotStarted, h.McpServers.StatusOf(server.Id).State);
        await h.McpServers.StartAsync(server.Id, Ct);
        Assert.Equal(McpServerState.Connected, h.McpServers.StatusOf(server.Id).State);
    }

    [Fact]
    public async Task A_chatty_server_logs_at_most_the_cap_and_one_note_and_keeps_its_latest_lines()
    {
        await using var h = await ChatHarness.StartAsync();
        var log = new ListLogger();
        await using var manager = new McpServerManager(
            new Infrastructure.Tools.Mcp.SdkMcpConnector(h.Paths), h.Mcp, new McpOptions(), new Application.SystemClock(), log);
        var server = await h.Mcp.AddAsync(FakeMcp.Command("chatty", flags: ["--stderr-lines", "250"]), Ct);

        await manager.StartAsync(server.Id, Ct);
        await ChatHarness.UntilAsync(() => manager.StatusOf(server.Id).StandardError.Contains("chatty line 250"), 10_000);

        var lines = log.Messages.Where(m => m.Contains("(stderr)", StringComparison.Ordinal)).ToList();
        Assert.Equal(101, lines.Count);
        Assert.EndsWith("chatty line 100", lines[99], StringComparison.Ordinal);
        Assert.Contains("the rest is not logged", lines[100], StringComparison.Ordinal);
        Assert.Equal(20, manager.StatusOf(server.Id).StandardError.Count);
    }

    /// <summary>Keeps the formatted messages a component logs.</summary>
    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<McpServerManager>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }

    private static bool Running(string pidFile)
    {
        var id = int.Parse(File.ReadAllText(pidFile), CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Task AssertEndsAsync(string pidFile) => ChatHarness.UntilAsync(() => File.Exists(pidFile) && !Running(pidFile), 10_000);
}
