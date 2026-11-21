using Mindweave.Application.Languages;
using Mindweave.Application.Tools.Mcp;
using Mindweave.Infrastructure.Tests.Chat;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Languages;

/// <summary>
/// Messages a service holds for the user read in the language current when they are shown, not when they were made
/// (docs/design/bilingual-ui.md §4.3, plan Task 5).
/// </summary>
[Collection(LanguageTests.Name)]
public class ServiceTextTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Validation_problem_follows_a_switch()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
            await using var h = await ChatHarness.StartAsync();

            var preview = h.Mcp.Preview(new McpServerDraft(string.Empty, McpServerKind.Command, "npx"));

            Assert.Equal("请填写服务器的名称。", preview.Problem?.ToString());
            CurrentLanguage.Set(UiLanguage.English);
            Assert.Equal("Enter the server’s name.", preview.Problem?.ToString());
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public async Task Halted_server_error_follows_a_switch()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
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

            Assert.StartsWith("服务器在 10 分钟内退出或启动失败了 5 次", status.Error?.ToString(), StringComparison.Ordinal);
            CurrentLanguage.Set(UiLanguage.English);
            Assert.StartsWith("The server exited or failed to start 5 times within 10 minutes", status.Error?.ToString(), StringComparison.Ordinal);
            Assert.DoesNotMatch(@"\p{IsCJKUnifiedIdeographs}", status.Error!.English);
            var refused = await Assert.ThrowsAsync<McpServerException>(() => h.McpServers.VerifyAsync(server, tool, Ct));
            Assert.StartsWith("The server exited or failed to start", refused.Message, StringComparison.Ordinal);
            CurrentLanguage.Set(UiLanguage.Chinese);
            Assert.StartsWith("服务器在 10 分钟内", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public void Storage_refusal_follows_a_switch()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.Chinese);

            // Internal to Application: made the way a refused request makes it.
            var type = typeof(McpServerSettings).Assembly.GetType("Mindweave.Application.Conversations.StorageAdmissionRefusedException", throwOnError: true)!;
            var refusal = (Exception)Activator.CreateInstance(type)!;

            Assert.Equal("本地数据暂时无法保存，请求未发送。", refusal.Message);
            CurrentLanguage.Set(UiLanguage.English);
            Assert.Equal("Local data can’t be saved right now, so the request wasn’t sent.", refusal.Message);
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }
}
