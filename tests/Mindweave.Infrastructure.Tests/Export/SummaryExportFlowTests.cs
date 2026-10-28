using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Export;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Export;
using Mindweave.Infrastructure.Tests.Chat;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Export;

/// <summary>
/// Exporting one saved version through the real stores (§8.3, §11 "导出到已有同名文件"): re-read by ID when written,
/// never the candidate, nothing internal in the file, and no change to versions or read state.
/// </summary>
public sealed class SummaryExportFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-export-flow", Guid.NewGuid().ToString("N"));

    public SummaryExportFlowTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Target => Path.Combine(_dir, "总结.md");

    private static string NameOf(ConversationSummary row) => row.Conversation.Title ?? "新会话";

    [Fact]
    public async Task The_current_version_is_exported_with_its_source_note_and_nothing_internal()
    {
        await using var h = await ChatHarness.StartAsync();
        var (conversation, node) = await SummarizedAsync(h);
        var detail = (await h.SummaryService.GetAsync(node, Ct))!;
        var export = Service(h);

        Assert.Equal(FakeChatClient.DefaultSummaryTitle, await export.SuggestFileNameAsync(node, detail.Node.Current, Ct));
        await export.ExportAsync(node, detail.Node.Current, sourceName: NameOf, Local(Target), Ct);

        var text = await File.ReadAllTextAsync(Target, Ct);
        var version = detail.Current;
        Assert.StartsWith($"# {FakeChatClient.DefaultSummaryTitle}\n\n> 来源会话：", text, StringComparison.Ordinal);
        Assert.Contains($"覆盖到第 {version.ToSeq + 1} 条消息 · 第 1 版（生成）", text, StringComparison.Ordinal);
        Assert.EndsWith(version.Body + "\n", text, StringComparison.Ordinal);

        // Privacy: no model, connection, address, credential or internal identity (architecture §8.1).
        var task = (await h.SummaryService.LatestTaskAsync(conversation, Ct))!.Id;
        foreach (var secret in new[] { "fake-model", "fake.example", "https://", "sk-test", "fake", node.ToString(), version.Id.ToString(), conversation.ToString(), task.ToString(), h.Workspace.ToString() })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_source_note_can_be_left_out_and_exporting_changes_no_version_or_read_state()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, node) = await SummarizedAsync(h);
        var before = (await h.SummaryService.GetAsync(node, Ct))!;
        await File.WriteAllTextAsync(Target, "已有的同名文件", Ct);

        await Service(h).ExportAsync(node, before.Node.Current, sourceName: null, Local(Target), Ct);

        Assert.Equal($"# {before.Current.Title}\n\n{before.Current.Body}\n", await File.ReadAllTextAsync(Target, Ct), ignoreLineEndingDifferences: false);
        var after = (await h.SummaryService.GetAsync(node, Ct))!;
        Assert.Equal(before.Node, after.Node);
        Assert.Equal(before.Versions, after.Versions);
        Assert.Equal(before.ReadState(before.Node.Current), after.ReadState(before.Node.Current));
        Assert.False(after.ReadState(before.Node.Current).IsRead);
    }

    [Fact]
    public async Task A_history_version_shown_on_the_page_is_exported_rather_than_the_current_one()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, node) = await SummarizedAsync(h);
        var first = (await h.SummaryService.GetAsync(node, Ct))!.Current;
        await h.SummaryService.SaveEditAsync(node, first.Id, "手改的标题", "手改的正文", Ct);

        await Service(h).ExportAsync(node, first.Id, sourceName: NameOf, Local(Target), Ct);

        var text = await File.ReadAllTextAsync(Target, Ct);
        Assert.StartsWith($"# {first.Title}\n", text, StringComparison.Ordinal);
        Assert.Contains("第 1 版（生成）", text, StringComparison.Ordinal);
        Assert.DoesNotContain("手改", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_version_deleted_while_the_save_dialog_was_open_is_refused_and_no_file_is_written()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, node) = await SummarizedAsync(h);
        var first = (await h.SummaryService.GetAsync(node, Ct))!.Current;
        await h.SummaryService.SaveEditAsync(node, first.Id, "第二版", "第二版正文", Ct);
        var export = Service(h);
        await export.SuggestFileNameAsync(node, first.Id, Ct);

        await h.SummaryService.DeleteVersionsAsync(node, new HashSet<SummaryVersionId> { first.Id }, Ct);
        var error = await Assert.ThrowsAsync<ExportRefusedException>(() => export.ExportAsync(node, first.Id, sourceName: NameOf, Local(Target), Ct));

        Assert.Equal(ExportRefusal.VersionMissing, error.Reason);
        Assert.False(File.Exists(Target));
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
        Assert.Equal(0, export.InProgress);
    }

    [Fact]
    public async Task A_deleted_summary_is_refused_like_a_deleted_version()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, node) = await SummarizedAsync(h);
        var current = (await h.SummaryService.GetAsync(node, Ct))!.Node.Current;
        await h.SummaryService.DeleteVersionsAsync(node, new HashSet<SummaryVersionId> { current }, Ct);

        var error = await Assert.ThrowsAsync<ExportRefusedException>(() => Service(h).SuggestFileNameAsync(node, current, Ct));

        Assert.Equal(ExportRefusal.VersionMissing, error.Reason);
    }

    [Fact]
    public async Task The_candidate_cannot_be_exported_until_adopted()
    {
        await using var h = await ChatHarness.StartAsync();
        var (conversation, node) = await SummarizedAsync(h);
        h.Client.SummaryReplies.Enqueue($"标题：{FakeChatClient.DefaultSummaryTitle}\n\n候选正文");
        Assert.True((await h.SummaryService.SummarizeAsync(conversation, Ct)).Accepted);
        await ChatHarness.UntilAsync(() => h.SummaryService.GetAsync(node, Ct).Result?.Pending is not null);
        var candidate = (await h.SummaryService.GetAsync(node, Ct))!.Pending!.Id;
        var export = Service(h);

        var suggest = await Assert.ThrowsAsync<ExportRefusedException>(() => export.SuggestFileNameAsync(node, candidate, Ct));
        var write = await Assert.ThrowsAsync<ExportRefusedException>(() => export.ExportAsync(node, candidate, sourceName: NameOf, Local(Target), Ct));

        Assert.Equal(ExportRefusal.IsCandidate, suggest.Reason);
        Assert.Equal(ExportRefusal.IsCandidate, write.Reason);
        Assert.False(File.Exists(Target));

        await h.SummaryService.AdoptAsync(node, candidate, Ct);
        await export.ExportAsync(node, candidate, sourceName: null, Local(Target), Ct);
        Assert.EndsWith("候选正文\n", await File.ReadAllTextAsync(Target, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_export_in_progress_is_counted_until_its_file_is_written()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, node) = await SummarizedAsync(h);
        var current = (await h.SummaryService.GetAsync(node, Ct))!.Node.Current;
        var gate = new TaskCompletionSource();
        var writer = new GatedWriter(gate.Task);
        var export = new SummaryExportService(h.SummaryNodes, h.Store, writer, NullLogger<SummaryExportService>.Instance);
        Assert.True(export.WhenIdleAsync().IsCompleted);

        var running = export.ExportAsync(node, current, sourceName: NameOf, Local(Target), Ct);
        await writer.Entered.Task;
        var idle = export.WhenIdleAsync();

        Assert.Equal(1, export.InProgress);
        Assert.False(idle.IsCompleted);
        gate.SetResult();
        await running;
        await idle;
        Assert.Equal(0, export.InProgress);
    }

    [Fact]
    public async Task A_failed_write_is_not_counted_as_in_progress_afterwards()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, node) = await SummarizedAsync(h);
        var current = (await h.SummaryService.GetAsync(node, Ct))!.Node.Current;
        var export = Service(h);

        await Assert.ThrowsAsync<ExportWriteException>(() => export.ExportAsync(node, current, sourceName: NameOf, Local(Path.Combine(_dir, "无", "x.md")), Ct));

        Assert.Equal(0, export.InProgress);
        Assert.True(export.WhenIdleAsync().IsCompleted);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static SummaryExportService Service(ChatHarness h) =>
        new(h.SummaryNodes, h.Store, new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance), NullLogger<SummaryExportService>.Instance);

    private static ExportDestination Local(string path) => new(Path.GetFileName(path), path, null);

    private static async Task<(ConversationId Conversation, SummaryNodeId Node)> SummarizedAsync(ChatHarness h)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        var outcome = await h.SummaryService.SummarizeAsync(sent.Conversation, Ct);
        Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
        await ChatHarness.UntilAsync(() => h.SummaryService.LatestTaskAsync(sent.Conversation, Ct).Result is { State: GenerationTaskState.Completed });
        return (sent.Conversation, (await h.SummaryService.ForConversationAsync(sent.Conversation, Ct))!.Node.Id);
    }

    private sealed class GatedWriter(Task gate) : IExportFileWriter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ExportWriteMode> WriteAsync(ExportDestination destination, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await gate;
            await File.WriteAllBytesAsync(destination.LocalPath!, content.ToArray(), cancellationToken);
            return ExportWriteMode.Atomic;
        }
    }
}
