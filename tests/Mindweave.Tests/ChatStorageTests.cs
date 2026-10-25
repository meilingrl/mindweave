using Microsoft.Data.Sqlite;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed class ChatStorageTests
{
    private static AiConfiguration Config() => new(Guid.NewGuid(), Guid.NewGuid(), "测试服务", "https://example.invalid/v1",
        "Mindweave.codex-steady/version-1", "model-a", 32768, 2048);
    private static IChatStore Chat(SqliteWorkspaceStore store) => Assert.IsAssignableFrom<IChatStore>(store);
    private static async Task<Conversation> ConversationAsync(SqliteWorkspaceStore store) =>
        await Chat(store).CreateConversationAsync((await store.CreateAsync("历史")).Id);

    [Fact]
    public async Task Accepted_reply_reopens_with_messages_task_frozen_configuration_and_input_identity()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        Guid conversationId, taskId, userId;
        var original = Config();
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            var chat = Chat(store);
            Assert.Null(await chat.LoadActiveConfigurationAsync());
            var conversation = await ConversationAsync(store);
            conversationId = conversation.Id;
            Assert.Equal("新会话", conversation.Title);
            Assert.False(conversation.TitleIsManual);
            Assert.Equal(conversation, await chat.LoadConversationAsync(conversation.Id));
            Assert.Single(await chat.ListConversationsAsync(conversation.WorkspaceId));
            await chat.SaveConfigurationAsync(original);
            var work = await chat.AcceptReplyAsync(conversation.Id, "问题原文", original);
            taskId = work.Task.Id;
            userId = Assert.Single(work.InputMessages).Id;
            Assert.Equal(new[] { userId }, work.Task.InputMessageIds);
            await chat.SaveConfigurationAsync(original with { CredentialReference = "Mindweave.codex-steady/version-2", ModelId = "model-b" });
        }
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(paths);
        var reopenedChat = Chat(reopened);
        var task = Assert.IsType<GenerationRecord>(await reopenedChat.LoadGenerationAsync(taskId));
        Assert.Equal(original, task.Configuration);
        Assert.Equal(new[] { userId }, task.InputMessageIds);
        Assert.Equal(GenerationStatus.Queued, task.Status);
        Assert.Equal("model-b", (await reopenedChat.LoadActiveConfigurationAsync())!.ModelId);
        var messages = await reopenedChat.ReadMessagesAsync(conversationId);
        Assert.Equal(new long[] { 1, 2 }, messages.Select(m => m.Sequence));
        Assert.Equal("问题原文", messages[0].Body);
        Assert.Equal(userId, messages[1].ReplyToUserId);
        Assert.Equal(task.AnswerMessageId, messages[1].Id);
        Assert.Single(await reopenedChat.ListActiveGenerationsAsync());
    }

    [Fact]
    public async Task Concurrent_accepts_allow_one_active_reply_and_rejected_accept_is_atomic()
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var conversation = await ConversationAsync(store);
        var chat = Chat(store);
        var requests = Enumerable.Range(0, 8).Select(async i =>
        {
            try { await chat.AcceptReplyAsync(conversation.Id, $"问题{i}", Config()); return true; }
            catch (InvalidOperationException) { return false; }
        });
        Assert.Single(await Task.WhenAll(requests), accepted => accepted);
        Assert.Equal(2, (await chat.ReadMessagesAsync(conversation.Id)).Count);
        Assert.Single(await chat.ListActiveGenerationsAsync());
    }

    [Theory]
    [InlineData(GenerationStatus.Completed)]
    [InlineData(GenerationStatus.Stopped)]
    [InlineData(GenerationStatus.Failed)]
    [InlineData(GenerationStatus.Interrupted)]
    public async Task Checkpoints_are_monotonic_and_every_terminal_state_is_immutable(GenerationStatus terminal)
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var chat = Chat(store);
        var conversation = await ConversationAsync(store);
        var work = await chat.AcceptReplyAsync(conversation.Id, "问题", Config());
        Assert.True(await chat.WriteGenerationAsync(work.Task.Id, 2, "最新正文", GenerationStatus.Running));
        Assert.False(await chat.WriteGenerationAsync(work.Task.Id, 1, "旧正文", GenerationStatus.Running));
        Assert.False(await chat.WriteGenerationAsync(work.Task.Id, 2, "同版正文", GenerationStatus.Running));
        Assert.False(await chat.WriteGenerationAsync(work.Task.Id, 3, "倒退排队", GenerationStatus.Queued));
        Assert.True(await chat.WriteGenerationAsync(work.Task.Id, 3, "终态正文", terminal, actualModelId: "actual-a"));
        Assert.False(await chat.WriteGenerationAsync(work.Task.Id, 1, "old terminal event", GenerationStatus.Stopped));
        Assert.False(await chat.WriteGenerationAsync(work.Task.Id, 99, "late", GenerationStatus.Running));
        Assert.False(await chat.WriteGenerationAsync(work.Task.Id, 100, "late terminal", GenerationStatus.Completed));
        var task = (await chat.LoadGenerationAsync(work.Task.Id))!;
        var answer = (await chat.ReadMessagesAsync(conversation.Id)).Last();
        Assert.Equal(terminal, task.Status);
        Assert.Equal(terminal, answer.Status);
        Assert.Equal("actual-a", task.ActualModelId);
        Assert.Equal(3, answer.Revision);
        Assert.Equal("终态正文", answer.Body);
        Assert.Empty(await chat.ListActiveGenerationsAsync());
    }

    [Fact]
    public async Task Retry_preserves_old_attempts_and_future_context_uses_only_latest_attempt()
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var chat = Chat(store);
        var conversation = await ConversationAsync(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.AcceptReplyAsync(conversation.Id, null, Config()));
        var first = await chat.AcceptReplyAsync(conversation.Id, "第一问", Config());
        await chat.WriteGenerationAsync(first.Task.Id, 1, "停止的旧回答", GenerationStatus.Stopped);
        var retry = await chat.AcceptReplyAsync(conversation.Id, null, Config());
        Assert.Equal(first.Task.InputMessageIds, retry.Task.InputMessageIds);
        await chat.WriteGenerationAsync(retry.Task.Id, 1, "新回答", GenerationStatus.Completed);
        var messages = await chat.ReadMessagesAsync(conversation.Id);
        Assert.Equal(3, messages.Count);
        Assert.Equal(first.Task.AnswerMessageId, messages[2].SupersedesMessageId);
        Assert.Equal("停止的旧回答", messages[1].Body);
        var next = await chat.AcceptReplyAsync(conversation.Id, "第二问", Config());
        Assert.Equal(new[] { "第一问", "新回答", "第二问" }, next.InputMessages.Select(m => m.Body));
        Assert.DoesNotContain(first.Task.AnswerMessageId!.Value, next.Task.InputMessageIds);
        await chat.WriteGenerationAsync(next.Task.Id, 1, "第二答", GenerationStatus.Completed);
        var secondRetry = await chat.AcceptReplyAsync(conversation.Id, null, Config());
        Assert.Equal(new[] { "第一问", "新回答", "第二问" }, secondRetry.InputMessages.Select(m => m.Body));
    }

    [Fact]
    public async Task Recovery_interrupts_queued_and_running_tasks_without_changing_body_or_reissuing()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        Guid queuedId, runningId, doneId, runningConversation;
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            var chat = Chat(store);
            queuedId = (await chat.AcceptReplyAsync((await ConversationAsync(store)).Id, "排队", Config())).Task.Id;
            runningConversation = (await ConversationAsync(store)).Id;
            runningId = (await chat.AcceptReplyAsync(runningConversation, "运行", Config())).Task.Id;
            await chat.WriteGenerationAsync(runningId, 7, "已经接收的正文", GenerationStatus.Running);
            doneId = (await chat.AcceptReplyAsync((await ConversationAsync(store)).Id, "完成", Config())).Task.Id;
            await chat.WriteGenerationAsync(doneId, 1, "已完成", GenerationStatus.Completed);
        }
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(paths);
        var recovered = Chat(reopened);
        await recovered.RecoverInterruptedAsync();
        await recovered.RecoverInterruptedAsync();
        Assert.Empty(await recovered.ListActiveGenerationsAsync());
        Assert.Equal(GenerationStatus.Interrupted, (await recovered.LoadGenerationAsync(queuedId))!.Status);
        var running = (await recovered.LoadGenerationAsync(runningId))!;
        Assert.Equal(GenerationStatus.Interrupted, running.Status);
        Assert.Equal(8, running.Revision);
        Assert.Equal("已经接收的正文", (await recovered.ReadMessagesAsync(runningConversation)).Last().Body);
        Assert.Equal(GenerationStatus.Completed, (await recovered.LoadGenerationAsync(doneId))!.Status);
        Assert.False(await recovered.WriteGenerationAsync(runningId, 100, "迟到", GenerationStatus.Completed));
    }

    [Fact]
    public async Task Auto_title_requires_success_and_cannot_overwrite_manual_title_or_terminal_task()
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var chat = Chat(store);
        var conversation = await ConversationAsync(store);
        Assert.Null(await chat.AcceptTitleAsync(conversation.Id, Config()));
        var reply = await chat.AcceptReplyAsync(conversation.Id, "问题", Config());
        await chat.WriteGenerationAsync(reply.Task.Id, 1, "成功回答", GenerationStatus.Completed);
        var title = (await chat.AcceptTitleAsync(conversation.Id, Config()))!;
        Assert.Equal(GenerationKind.Title, title.Task.Kind);
        Assert.Equal(0, title.Task.ExpectedTitleRevision);
        Assert.Null(await chat.AcceptTitleAsync(conversation.Id, Config()));
        await chat.RenameConversationAsync(conversation.Id, "我的标题");
        Assert.False(await chat.CompleteTitleAsync(title.Task.Id, "自动标题"));
        var renamed = (await chat.LoadConversationAsync(conversation.Id))!;
        Assert.True(renamed.TitleIsManual);
        Assert.Equal(1, renamed.TitleRevision);
        Assert.Equal("我的标题", renamed.Title);
        Assert.Empty(await chat.ListActiveGenerationsAsync());
        Assert.False(await chat.CompleteTitleAsync(title.Task.Id, "迟到标题"));
    }

    [Fact]
    public async Task Automatic_title_success_applies_once_and_does_not_persist_partial_title()
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var chat = Chat(store);
        var conversation = await ConversationAsync(store);
        var reply = await chat.AcceptReplyAsync(conversation.Id, "问题", Config());
        await chat.WriteGenerationAsync(reply.Task.Id, 1, "回答", GenerationStatus.Completed);
        var title = (await chat.AcceptTitleAsync(conversation.Id, Config()))!;
        Assert.True(await chat.WriteGenerationAsync(title.Task.Id, 1, "部分标题", GenerationStatus.Running));
        Assert.Equal("新会话", (await chat.LoadConversationAsync(conversation.Id))!.Title);
        Assert.True(await chat.CompleteTitleAsync(title.Task.Id, "生成标题"));
        Assert.Equal("生成标题", (await chat.LoadConversationAsync(conversation.Id))!.Title);
        Assert.False(await chat.CompleteTitleAsync(title.Task.Id, "覆盖"));
        Assert.Equal(2, (await chat.ReadMessagesAsync(conversation.Id)).Count);
    }

    [Fact]
    public async Task Draft_revision_tombstone_prevents_resurrection_and_accept_preserves_different_later_input()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        Guid id;
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            var chat = Chat(store);
            id = (await ConversationAsync(store)).Id;
            Assert.Equal(new ConversationDraft("", 0), await chat.LoadDraftAsync(id));
            await chat.SaveDraftAsync(id, "发送内容", 5);
            await chat.SaveDraftAsync(id, "旧内容", 4);
            var first = await chat.AcceptReplyAsync(id, "发送内容", Config());
            Assert.Equal(new ConversationDraft("", 6), await chat.LoadDraftAsync(id));
            await chat.SaveDraftAsync(id, "发送内容", 5);
            await chat.SaveDraftAsync(id, "同版旧内容", 6);
            Assert.Equal(new ConversationDraft("", 6), await chat.LoadDraftAsync(id));
            await chat.WriteGenerationAsync(first.Task.Id, 1, "回答", GenerationStatus.Completed);
            await chat.SaveDraftAsync(id, "后续输入", 7);
            await chat.AcceptReplyAsync(id, "另一个已提交输入", Config());
            Assert.Equal(new ConversationDraft("后续输入", 7), await chat.LoadDraftAsync(id));
        }
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(paths);
        Assert.Equal(new ConversationDraft("后续输入", 7), await Chat(reopened).LoadDraftAsync(id));
    }

    [Fact]
    public async Task Draft_at_maximum_revision_can_be_cleared_without_resurrecting_submitted_text()
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var chat = Chat(store);
        var conversation = await ConversationAsync(store);
        await chat.SaveDraftAsync(conversation.Id, "已提交", long.MaxValue);
        await chat.AcceptReplyAsync(conversation.Id, "已提交", Config());
        Assert.Equal(new ConversationDraft("", long.MaxValue), await chat.LoadDraftAsync(conversation.Id));
        await chat.SaveDraftAsync(conversation.Id, "已提交", long.MaxValue);
        Assert.Equal(new ConversationDraft("", long.MaxValue), await chat.LoadDraftAsync(conversation.Id));
    }

    [Fact]
    public async Task Invalid_configuration_or_empty_input_leaves_no_partial_accept()
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var chat = Chat(store);
        var conversation = await ConversationAsync(store);
        var config = Config();
        await chat.SaveConfigurationAsync(config);
        AiConfiguration[] invalid = [config with { MaxOutputTokens = 0 }, config with { ContextTokenBudget = 2048 },
            config with { TokenLimitParameter = "unsupported" }, config with { BaseUrl = "http://example.invalid" },
            config with { BaseUrl = "https://user:password@example.invalid" }, config with { BaseUrl = "https://example.invalid?token=x" },
            config with { BaseUrl = "https://example.invalid/#fragment" }, config with { CredentialReference = "" }];
        foreach (var item in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => chat.SaveConfigurationAsync(item));
            await Assert.ThrowsAsync<ArgumentException>(() => chat.AcceptReplyAsync(conversation.Id, "sensitive original", item));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => chat.AcceptReplyAsync(conversation.Id, "  ", config));
        Assert.Empty(await chat.ReadMessagesAsync(conversation.Id));
        Assert.Empty(await chat.ListActiveGenerationsAsync());
        Assert.Equal(config, await chat.LoadActiveConfigurationAsync());
        await chat.SaveConfigurationAsync(config with { BaseUrl = "http://127.0.0.1:12345/v1" });
    }

    [Fact]
    public async Task Late_transaction_failure_rolls_back_accept_and_checkpoint_without_losing_draft()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        var chat = Chat(store);
        var conversation = await ConversationAsync(store);
        await chat.SaveDraftAsync(conversation.Id, "保留输入", 1);
        Execute(paths, "CREATE TRIGGER reject_task BEFORE INSERT ON generation_task BEGIN SELECT RAISE(ABORT,'Injected task failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => chat.AcceptReplyAsync(conversation.Id, "保留输入", Config()));
        Assert.Empty(await chat.ReadMessagesAsync(conversation.Id));
        Assert.Empty(await chat.ListActiveGenerationsAsync());
        Assert.Equal(new ConversationDraft("保留输入", 1), await chat.LoadDraftAsync(conversation.Id));
        Execute(paths, "DROP TRIGGER reject_task;");
        var work = await chat.AcceptReplyAsync(conversation.Id, "保留输入", Config());
        Execute(paths, "CREATE TRIGGER reject_update BEFORE UPDATE ON generation_task BEGIN SELECT RAISE(ABORT,'Injected update failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => chat.WriteGenerationAsync(work.Task.Id, 1, "应回滚正文", GenerationStatus.Completed));
        Assert.Equal(GenerationStatus.Queued, (await chat.LoadGenerationAsync(work.Task.Id))!.Status);
        var answer = (await chat.ReadMessagesAsync(conversation.Id)).Last();
        Assert.Equal("", answer.Body);
        Assert.Equal(0, answer.Revision);
        Assert.Equal(GenerationStatus.Queued, answer.Status);
        Execute(paths, "DROP TRIGGER reject_update;");
        Assert.True(await chat.WriteGenerationAsync(work.Task.Id, 1, "保存成功", GenerationStatus.Completed));
    }

    [Fact]
    public async Task Database_rejects_cross_conversation_parent_wrong_role_duplicate_sequence_and_terminal_edits()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        var chat = Chat(store);
        var first = await ConversationAsync(store);
        var second = await ConversationAsync(store);
        var work = await chat.AcceptReplyAsync(first.Id, "问题", Config());
        await chat.WriteGenerationAsync(work.Task.Id, 1, "最终正文", GenerationStatus.Completed);
        var userId = work.Task.InputMessageIds[0];
        Assert.Throws<SqliteException>(() => Execute(paths,
            "INSERT INTO chat_message VALUES($id,$conversation,1,'Assistant','','Queued',0,$parent,NULL)",
            ("$id", Guid.NewGuid()), ("$conversation", second.Id), ("$parent", userId)));
        Assert.Throws<SqliteException>(() => Execute(paths,
            "INSERT INTO chat_message VALUES($id,$conversation,3,'Assistant','','Queued',0,$parent,NULL)",
            ("$id", Guid.NewGuid()), ("$conversation", first.Id), ("$parent", work.Task.AnswerMessageId)));
        Assert.Throws<SqliteException>(() => Execute(paths,
            "INSERT INTO chat_message VALUES($id,$conversation,1,'User','duplicate','Completed',0,NULL,NULL)",
            ("$id", Guid.NewGuid()), ("$conversation", first.Id)));
        Assert.Throws<SqliteException>(() => Execute(paths, "UPDATE chat_message SET body='overwrite' WHERE id=$id", ("$id", work.Task.AnswerMessageId)));
        Assert.Throws<SqliteException>(() => Execute(paths, "UPDATE generation_task SET status='Running' WHERE id=$id", ("$id", work.Task.Id)));
        Assert.Throws<SqliteException>(() => Execute(paths, "UPDATE generation_task SET input_ids_json='[]' WHERE id=$id", ("$id", work.Task.Id)));
        Assert.Equal("最终正文", (await chat.ReadMessagesAsync(first.Id)).Last().Body);
        Assert.Empty(await chat.ReadMessagesAsync(second.Id));
    }

    private static void Execute(AppPaths paths, string sql, params (string Name, object? Value)[] parameters)
    {
        // Test-owned connection installs SQLite failure fixtures; production continues using its single writer.
        using var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Foreign Keys=True;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value is Guid id ? id.ToString("D") : value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Migration_v1_preserves_workspaces_and_backs_up_before_installing_chat_schema()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        Guid workspace;
        // v1 fixture is intentionally the submitted S0 migration, not the latest migration set.
        await using (var old = await SqliteWorkspaceStore.OpenAsync(paths, [DatabaseSchema.Migrations[0]]))
            workspace = (await old.CreateAsync("v1 正文")).Id;
        await using var upgraded = await SqliteWorkspaceStore.OpenAsync(paths);
        Assert.Equal("v1 正文", Assert.Single(await upgraded.ListAsync()).Name);
        await Chat(upgraded).CreateConversationAsync(workspace);
        var backup = Assert.Single(Directory.GetFiles(paths.BackupDirectory, "*.db"));
        using var connection = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT name FROM workspace";
        Assert.Equal("v1 正文", command.ExecuteScalar());
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='conversation'";
        Assert.Equal(0L, command.ExecuteScalar());
    }
}
