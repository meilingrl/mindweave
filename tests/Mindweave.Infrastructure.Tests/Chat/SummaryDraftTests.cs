using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// The summary edit draft owner (architecture review F2, R4): the newest input outlives pages; reads, writes, commits
/// and discards that overlap never resurrect, drop or silently rebase input; stale sessions are refused.
/// </summary>
public class SummaryDraftTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_set_made_while_the_stored_draft_is_read_wins_over_that_read()
    {
        await using var h = await ChatHarness.StartAsync();
        var (node, current) = await SummaryAsync(h);
        await h.SummaryNodes.SaveDraftAsync(new SummaryEditDraft(node, current, "标题", "早先存下的旧草稿", DateTimeOffset.UtcNow), Ct);
        var store = Gated.Wrap(h.SummaryNodes);
        using var drafts = Drafts(h, store.Store);

        var hold = store.Hold(nameof(ISummaryStore.GetAsync));
        var opening = drafts.OpenAsync(node, current, Ct);
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.NotNull(drafts.Set(node, 0, current, "标题", "开头读取时写下的"));
        hold.Release.SetResult();

        var read = Assert.IsType<DraftRead.Draft>(await opening);
        Assert.Equal("开头读取时写下的", read.Body);
    }

    [Fact]
    public async Task A_discard_after_a_write_was_captured_leaves_no_draft_behind()
    {
        await using var h = await ChatHarness.StartAsync();
        var (node, current) = await SummaryAsync(h);
        var store = Gated.Wrap(h.SummaryNodes);
        using var drafts = Drafts(h, store.Store);
        var session = await drafts.OpenAsync(node, current, Ct);
        var revision = drafts.Set(node, session.Generation, current, "标题", "要放弃的修改")!.Value;

        var hold = store.Hold(nameof(ISummaryStore.SaveDraftAsync));
        var writing = drafts.FlushAsync();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var discarding = drafts.DiscardAsync(node, session.Generation, revision);
        hold.Release.SetResult();
        await writing;
        await discarding;

        Assert.Null((await h.SummaryNodes.GetAsync(node, Ct))!.Draft);
        Assert.IsType<DraftRead.None>(await drafts.OpenAsync(node, current, Ct));
        Assert.True(await drafts.FlushAsync());
        Assert.Null((await h.SummaryNodes.GetAsync(node, Ct))!.Draft);
    }

    [Fact]
    public async Task Input_set_on_another_page_while_a_version_is_saved_is_kept_as_a_conflict()
    {
        await using var h = await ChatHarness.StartAsync();
        var (node, current) = await SummaryAsync(h);
        var store = Gated.Wrap(h.SummaryNodes);
        using var drafts = Drafts(h, store.Store);
        var session = await drafts.OpenAsync(node, current, Ct);
        var revision = drafts.Set(node, session.Generation, current, "标题", "保存这一版")!.Value;

        var hold = store.Hold(nameof(ISummaryStore.SaveEditAsync));
        var committing = drafts.CommitAsync(node, session.Generation, revision, () => store.Store.SaveEditAsync(node, current, "标题", "保存这一版", DateTimeOffset.UtcNow, Ct));
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.NotNull(drafts.Set(node, session.Generation, current, "标题", "另一页上更新的输入"));
        hold.Release.SetResult();
        var saved = await committing;

        Assert.NotNull(saved);
        Assert.Equal("保存这一版", (await h.SummaryNodes.GetAsync(node, Ct))!.Current.Body);
        var kept = Assert.IsType<DraftRead.Conflict>(await drafts.OpenAsync(node, saved.Value, Ct));
        Assert.Equal("另一页上更新的输入", kept.Body);
        Assert.False(await drafts.FlushAsync());
    }

    [Fact]
    public async Task A_stale_session_can_neither_set_commit_nor_discard()
    {
        await using var h = await ChatHarness.StartAsync();
        var (node, current) = await SummaryAsync(h);
        using var drafts = Drafts(h, h.SummaryNodes);
        var older = await drafts.OpenAsync(node, current, Ct);
        var revision = drafts.Set(node, older.Generation, current, "标题", "先保存的修改")!.Value;
        await drafts.CommitAsync(node, older.Generation, revision, () => h.SummaryNodes.SaveEditAsync(node, current, "标题", "先保存的修改", DateTimeOffset.UtcNow, Ct));

        Assert.Null(drafts.Set(node, older.Generation, current, "标题", "旧页面上的输入"));
        var commit = await Assert.ThrowsAsync<SummaryRefusedException>(() => drafts.CommitAsync(node, older.Generation, revision, () => Task.FromResult(0)));
        Assert.Equal(SummaryRefusal.Changed, commit.Reason);
        var discard = await Assert.ThrowsAsync<SummaryRefusedException>(() => drafts.DiscardAsync(node, older.Generation, revision));
        Assert.Equal(SummaryRefusal.Changed, discard.Reason);
    }

    [Fact]
    public async Task A_discard_storage_refuses_is_retried_and_a_new_draft_written_meanwhile_wins()
    {
        await using var h = await ChatHarness.StartAsync();
        var (node, current) = await SummaryAsync(h);
        var store = Gated.Wrap(h.SummaryNodes);
        using var drafts = Drafts(h, store.Store);
        var session = await drafts.OpenAsync(node, current, Ct);
        var revision = drafts.Set(node, session.Generation, current, "标题", "存下的草稿")!.Value;
        Assert.True(await drafts.FlushAsync());

        store.Refuse(nameof(ISummaryStore.DeleteDraftAsync));
        await drafts.DiscardAsync(node, session.Generation, revision);
        Assert.True(h.Health.IsBlocked);
        Assert.Equal(1, h.Health.Outstanding);
        Assert.IsType<DraftRead.None>(await drafts.OpenAsync(node, current, Ct));

        var fresh = await drafts.OpenAsync(node, current, Ct);
        Assert.NotNull(drafts.Set(node, fresh.Generation, current, "标题", "放弃后重新写的"));
        store.Allow(nameof(ISummaryStore.DeleteDraftAsync));
        Assert.True(await drafts.FlushAsync());

        Assert.Equal("放弃后重新写的", (await h.SummaryNodes.GetAsync(node, Ct))!.Draft?.Body);
        Assert.Equal(0, h.Health.Outstanding);
        await ChatHarness.UntilAsync(() => !h.Health.IsBlocked);
    }

    [Fact]
    public async Task Unsaved_input_in_memory_keeps_the_current_version_from_being_switched()
    {
        await using var h = await ChatHarness.StartAsync();
        var (node, first) = await SummaryAsync(h);
        var second = (await h.SummaryService.SaveEditAsync(node, first, "标题", "第二版", Ct))!.Value;
        var session = await h.SummaryService.OpenDraftAsync(node, second, Ct);
        Assert.NotNull(h.SummaryService.SetDraft(node, session.Generation, second, "标题", "还在内存里的修改"));

        var refused = await Assert.ThrowsAsync<SummaryRefusedException>(() => h.SummaryService.SetCurrentAsync(node, first, Ct));
        Assert.Equal(SummaryRefusal.HasDraft, refused.Reason);
        Assert.Equal(second, (await h.SummaryService.GetAsync(node, Ct))!.Node.Current);
    }

    /// <summary>Health check 2026-11-12, item 10: the schema refuses a blank title or body, so the service refuses it first, by name.</summary>
    [Theory]
    [InlineData("", "正文")]
    [InlineData("   ", "正文")]
    [InlineData("标题", "")]
    [InlineData("标题", " \n\t ")]
    public async Task A_blank_title_or_body_is_refused_and_the_current_version_stays(string title, string body)
    {
        await using var h = await ChatHarness.StartAsync();
        var (node, current) = await SummaryAsync(h);
        var session = await h.SummaryService.OpenDraftAsync(node, current, Ct);
        var revision = h.SummaryService.SetDraft(node, session.Generation, current, title, body)!.Value;

        var refused = await Assert.ThrowsAsync<SummaryRefusedException>(() => h.SummaryService.SaveEditAsync(node, current, title, body, session.Generation, revision, Ct));

        Assert.Equal(SummaryRefusal.Empty, refused.Reason);
        Assert.Equal(current, (await h.SummaryService.GetAsync(node, Ct))!.Node.Current);
        Assert.False(h.Health.IsBlocked);
    }

    private static SummaryDrafts Drafts(ChatHarness h, ISummaryStore store) =>
        new(store, new SystemClock(), h.Health, NullLogger<SummaryDrafts>.Instance, TimeSpan.FromHours(1));

    private static async Task<(SummaryNodeId Node, SummaryVersionId Current)> SummaryAsync(ChatHarness h)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        var outcome = await h.SummaryService.SummarizeAsync(sent.Conversation, Ct);
        Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
        SummaryEntry? entry = null;
        await ChatHarness.UntilAsync(async () => (entry = await h.SummaryService.ForConversationAsync(sent.Conversation, Ct)) is not null);
        return (entry!.Node.Id, entry.Node.Current);
    }

    /// <summary>A summary store whose chosen operations can be held at entry, or refused as storage refuses writes.</summary>
#pragma warning disable CA1852 // DispatchProxy derives a runtime type from it, so it cannot be sealed.
    internal class Gated : DispatchProxy
#pragma warning restore CA1852
    {
        private readonly Dictionary<string, Held> _held = [];
        private readonly HashSet<string> _refused = [];
        private ISummaryStore _inner = null!;

        public static Gated Wrap(ISummaryStore inner)
        {
            var proxy = Create<ISummaryStore, Gated>();
            var gated = (Gated)(object)proxy;
            gated._inner = inner;
            return gated;
        }

        public ISummaryStore Store => (ISummaryStore)(object)this;

        public Held Hold(string method)
        {
            var held = new Held();
            lock (_held)
            {
                _held[method] = held;
            }

            return held;
        }

        public void Refuse(string method)
        {
            lock (_held)
            {
                _refused.Add(method);
            }
        }

        public void Allow(string method)
        {
            lock (_held)
            {
                _refused.Remove(method);
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            Held? held;
            bool refused;
            lock (_held)
            {
                _held.Remove(targetMethod.Name, out held);
                refused = _refused.Contains(targetMethod.Name);
            }

            if (refused)
            {
                return Task.FromException(new StorageWriteException(StorageFailureKind.Full, "simulated full disk", null));
            }

            if (held is null)
            {
                return targetMethod.Invoke(_inner, args);
            }

            held.Entered.TrySetResult();
            return HeldAsync(targetMethod, args, held);
        }

        private object HeldAsync(MethodInfo method, object?[]? args, Held held)
        {
            var returnType = method.ReturnType;
            if (returnType == typeof(Task))
            {
                return Later(async () =>
                {
                    await held.Release.Task;
                    await (Task)method.Invoke(_inner, args)!;
                });
            }

            var result = returnType.GetGenericArguments()[0];
            return typeof(Gated).GetMethod(nameof(LaterOf), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(result)
                .Invoke(null, [held, (Func<object>)(() => method.Invoke(_inner, args)!)])!;
        }

        private static Task Later(Func<Task> work) => work();

        private static async Task<T> LaterOf<T>(Held held, Func<object> start)
        {
            await held.Release.Task;
            return await (Task<T>)start();
        }
    }

    internal sealed class Held
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
