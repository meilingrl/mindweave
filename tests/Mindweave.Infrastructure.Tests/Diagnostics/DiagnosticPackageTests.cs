using System.IO.Compression;
using System.Text;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Export;
using Mindweave.Application.Models;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.Diagnostics;

/// <summary>
/// The request list and the diagnostic package (D2 §2.2, acceptance scenarios 3, 4 and 5 of design §2.7): built from allowed
/// fields, scrubbed with every stored key, and made without the data when it cannot be opened.
/// </summary>
public sealed class DiagnosticPackageTests : IDisposable
{
    private const string Marker = "ZEBRA-MARKER-7731";
    private const string ShapelessKey = "plainword-4711-no-known-shape";

    private readonly string _out = Path.Combine(Path.GetTempPath(), "mindweave-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }

    /// <summary>Scenario 3: a failed title shows nothing on the page, and is one row of the list.</summary>
    [Fact]
    public async Task A_failed_title_is_a_row_of_the_list_and_failures_only_leaves_the_answers_out()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.Titles.FailNext(GenerationErrorKind.ServerError);
        h.Client.CacheReadTokens = _ => null;
        h.Client.PromptTokens = _ => 100;
        h.Client.CompletionTokens = _ => 20;
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await h.TitleEndedAsync(sent.Conversation);
        var list = h.Get<IRequestList>();
        await ChatHarness.UntilAsync(async () => (await list.RecentAsync(DateTimeOffset.UnixEpoch, false, 100, Ct)).Count >= 2);

        var all = await list.RecentAsync(DateTimeOffset.UnixEpoch, failuresOnly: false, 100, Ct);
        var failed = Assert.Single(await list.RecentAsync(DateTimeOffset.UnixEpoch, failuresOnly: true, 100, Ct));

        Assert.Equal(("title", Application.Diagnostics.AiRequestOutcome.Failed, "fake-model", "fake.example"), (failed.Purpose, failed.Outcome, failed.Model, failed.Host));
        var answer = Assert.Single(all, r => r.Purpose == "answer");
        Assert.Equal(sent.Answer, answer.Answer);
        Assert.Equal(100, answer.PromptTokens);
        Assert.Null(answer.CacheReadTokens); // Not reported stays null, never zero.
        Assert.True(all[0].StartedAt >= all[^1].StartedAt);
    }

    /// <summary>Scenario 4: a marker in the conversations and a key without a known shape are in no file of the package.</summary>
    [Fact]
    public async Task Neither_conversation_text_nor_a_shapeless_key_is_in_any_file_of_the_package()
    {
        await using var h = await ChatHarness.StartAsync(model: new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", ShapelessKey));
        h.Client.Reply("回答里也有 " + Marker);
        var sent = await h.Conversations.StartAsync(h.Workspace, "问题 " + Marker, Ct);
        await h.SettleAsync(sent.Conversation);

        // A service that echoes the key in its error, as some do, and a log line that quotes it with a home path.
        h.Client.FailWithStatus(GenerationErrorKind.Unauthorized, 401, detail: "HTTP 401: bad key " + ShapelessKey);
        await h.Conversations.AskAsync(sent.Conversation, "再问 " + Marker, Ct);
        await ChatHarness.UntilAsync(async () => (await h.Get<IRequestList>().RecentAsync(DateTimeOffset.UnixEpoch, true, 10, Ct)).Count > 0);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Directory.CreateDirectory(h.Paths.LogDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(h.Paths.LogDirectory, "mindweave-20261127.log"),
            $"2026-11-27T10:00:00.000Z WRN test: echo {ShapelessKey} at {Path.Combine(home, "Library")}\n",
            Ct);

        var file = await SaveAsync(h, new DiagnosticChoice());

        var entries = Read(file);
        Assert.Equal(["logs/mindweave-20261127.log", "requests.csv", "settings.txt", "summary.txt"], entries.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, text) in entries)
        {
            Assert.DoesNotContain(Marker, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ShapelessKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain("测试", text, StringComparison.Ordinal); // The workspace's name.
            Assert.True(home.Length < 2 || !text.Contains(home, StringComparison.Ordinal), name);
        }

        Assert.Contains("echo [redacted] at ~", entries["logs/mindweave-20261127.log"], StringComparison.Ordinal);
        Assert.Contains("HTTP 401: bad key [redacted]", entries["requests.csv"], StringComparison.Ordinal);
        Assert.Contains("Logs scrubbed with: 1 stored keys", entries["summary.txt"], StringComparison.Ordinal);
        Assert.Contains("conversations: 1", entries["summary.txt"], StringComparison.Ordinal);
        Assert.StartsWith("started_at,purpose,model,service_host,outcome,", entries["requests.csv"], StringComparison.Ordinal);
        Assert.Contains("\r\n", entries["requests.csv"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leaving_out_model_names_and_hosts_and_the_logs_takes_them_out_of_every_file()
    {
        await using var h = await ChatHarness.StartAsync();
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await h.SettleAsync(sent.Conversation);
        Directory.CreateDirectory(h.Paths.LogDirectory);
        await File.WriteAllTextAsync(Path.Combine(h.Paths.LogDirectory, "mindweave-20261127.log"), "a line\n", Ct);

        var entries = Read(await SaveAsync(h, new DiagnosticChoice(ModelsAndHosts: false, Logs: false)));

        Assert.DoesNotContain(entries.Keys, k => k.StartsWith("logs/", StringComparison.Ordinal));
        foreach (var text in entries.Values)
        {
            Assert.DoesNotContain("fake-model", text, StringComparison.Ordinal);
            Assert.DoesNotContain("fake.example", text, StringComparison.Ordinal);
        }

        Assert.Contains("model: model 1", entries["settings.txt"], StringComparison.Ordinal);
        Assert.Contains(",model 1,host 1,", entries["requests.csv"], StringComparison.Ordinal);
    }

    /// <summary>Scenario 5: when the data cannot be opened, the package holds the summary and the logs.</summary>
    [Fact]
    public async Task Without_the_data_the_package_holds_the_summary_and_the_logs()
    {
        await using var h = await ChatHarness.StartAsync();
        Directory.CreateDirectory(h.Paths.LogDirectory);
        await File.WriteAllTextAsync(Path.Combine(h.Paths.LogDirectory, "mindweave-20261127.log"), "start\n", Ct);

        var service = h.Get<DiagnosticPackageService>();
        var files = await service.BuildAsync(new DiagnosticChoice(), dataAvailable: false, Ct);

        Assert.Equal(["summary.txt", "logs/mindweave-20261127.log"], files.Select(f => f.Path));
        Assert.Contains("Storage: the data could not be opened", files[0].Text, StringComparison.Ordinal);
        Assert.Contains("credential shapes only", files[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Counts", files[0].Text, StringComparison.Ordinal);
    }

    private async Task<string> SaveAsync(ChatHarness h, DiagnosticChoice choice)
    {
        Directory.CreateDirectory(_out);
        var file = Path.Combine(_out, "package.zip");
        await h.Get<DiagnosticPackageService>().SaveAsync(new ExportDestination("package.zip", file, null), choice, dataAvailable: true, Ct);
        return file;
    }

    private static Dictionary<string, string> Read(string file)
    {
        using var zip = ZipFile.OpenRead(file);
        return zip.Entries.ToDictionary(
            e => e.FullName,
            e =>
            {
                using var reader = new StreamReader(e.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            },
            StringComparer.Ordinal);
    }
}
