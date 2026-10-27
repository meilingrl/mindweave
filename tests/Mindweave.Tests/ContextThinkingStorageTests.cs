using System.Text.Json;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Infrastructure.AI;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;
using static Mindweave.Tests.ContextThinkingTests;

namespace Mindweave.Tests;

public sealed class ContextThinkingStorageTests
{
    private sealed class AuditVault : ICredentialVault
    {
        public int Accesses { get; private set; }
        public Task<string> ReadAsync(string reference) { Accesses++; return Task.FromResult("synthetic"); }
        public Task<string> WriteNewAsync(string secret) { Accesses++; return Task.FromResult("synthetic-reference"); }
        public Task DeleteAsync(string reference) { Accesses++; return Task.CompletedTask; }
    }
    [Theory] [InlineData(1, "https://other.invalid/v1")] [InlineData(2, "https://api.deepseek.com/v1")]
    public async Task Invalid_new_policy_never_accesses_vault_http_or_mutates_sqlite(int mode, string url)
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        var conversation = await store.CreateConversationAsync((await store.CreateAsync("synthetic")).Id);
        var before = File.ReadAllBytes(paths.DatabasePath);
        var config = Config(mode, url); var vault = new AuditVault();
        using var handler = new ContextThinkingTransportTests.CaptureHandler(); using var client = new HttpClient(handler);
        var service = new ChatConfigurationService(store, vault, new ChatCompletionsTransport(client));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(config, "synthetic-new"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DiscoverModelsAsync(config, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DiscoverModelsAsync(config, "synthetic-unsaved", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveConfigurationAsync(config));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AcceptReplyAsync(conversation.Id, "question", config));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AcceptTitleAsync(conversation.Id, config));
        Assert.Equal(0, vault.Accesses); Assert.Empty(handler.Bodies);
        Assert.Empty(await store.ReadMessagesAsync(conversation.Id)); Assert.Null(await store.LoadActiveConfigurationAsync());
        Assert.Equal(before, File.ReadAllBytes(paths.DatabasePath));
    }

    [Fact]
    public async Task Disabled_configuration_roundtrips_on_reopen_and_tampered_unknown_mode_is_rejected()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path); var config = Config(1);
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths)) await store.SaveConfigurationAsync(config);
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            Assert.Equal(config, await store.LoadActiveConfigurationAsync());
            Assert.Equal(1, Number((await store.LoadActiveConfigurationAsync())!, "SummaryThinking"));
            ContextStorageTests.Sql(paths, "UPDATE active_ai_configuration SET configuration_json=json_set(configuration_json,'$.SummaryThinking',99)");
            await Assert.ThrowsAsync<ArgumentException>(() => store.LoadActiveConfigurationAsync());
        }
    }

    [Fact]
    public async Task Format6_disabled_cache_survives_requester_deletion_and_reopen_without_default_reuse()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        var config = Config(1) with { ContextTokenBudget = 16000 };
        Guid owner, requester, survivor, survivorTask;
        var attempts = new List<Guid>(); var caches = new List<Guid>(); var keys = new List<string>();
        string expectedPrepared;
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            owner = (await store.CreateConversationAsync((await store.CreateAsync("synthetic")).Id)).Id;
            for (var i = 0; i < 5; i++) await BranchStorageTests.Answer(store, owner, "q" + i, new string('x', 2500));
            var message = (await store.ReadMessagesAsync(owner))[^1];
            var selection = BranchStorageTests.Selection(message) with { Quote = "x" };
            requester = (await store.CreateBranchAsync(owner, selection, Guid.NewGuid())).Id;
            survivor = (await store.CreateBranchAsync(owner, selection, Guid.NewGuid())).Id;
            var other = await store.CreateBranchAsync(owner, selection, Guid.NewGuid());
            var left = await store.AcceptReplyAsync(requester, "current", config);
            var right = await store.AcceptReplyAsync(survivor, "current", config); survivorTask = right.Task.Id;
            var normal = await store.AcceptReplyAsync(other.Id, "current", config with { SummaryThinking = SummaryThinkingMode.ServiceDefault });
            Assert.Equal(6, left.ContextPlan!.FormatVersion);
            foreach (var chunk in left.ContextPlan.Parts.OfType<SummaryContextPart>().Select(p => p.Chunk))
            {
                var attempt = await store.StartCompressionAsync(left.Task.Id, chunk.CacheKey); attempts.Add(attempt.Id); keys.Add(chunk.CacheKey);
                await store.WriteCompressionAsync(left.Task.Id, attempt.Id, 1,
                    ContextSummaryProtocol.Serialize(new("synthetic", "", "", "", "")), GenerationStatus.Completed, "stop");
                caches.Add((await store.LoadCompletedSummaryAsync(right.Task.Id, chunk.CacheKey))!.Id);
            }
            Assert.NotEmpty(keys);
            foreach (var chunk in normal.ContextPlan!.Parts.OfType<SummaryContextPart>().Select(p => p.Chunk))
            {
                Assert.DoesNotContain(chunk.CacheKey, keys); Assert.Null(await store.LoadCompletedSummaryAsync(normal.Task.Id, chunk.CacheKey));
            }
            var prepared = await store.PrepareReplyRequestAsync(right.Task.Id);
            Assert.Equal(ChatRequestPurpose.Standard, prepared.Request.Purpose);
            Assert.Equal(SummaryThinkingMode.Disabled, prepared.Request.Configuration.SummaryThinking);
            expectedPrepared = JsonSerializer.Serialize(prepared);
            await store.WriteGenerationAsync(left.Task.Id, 1, "left", GenerationStatus.Completed);
            await store.WriteGenerationAsync(right.Task.Id, 1, "right", GenerationStatus.Completed);
            await store.WriteGenerationAsync(normal.Task.Id, 1, "", GenerationStatus.Stopped);
        }
        ContextStorageTests.Sql(paths, "BEGIN; PRAGMA defer_foreign_keys=ON; UPDATE app_selection SET kind='Overview',target_id=NULL; DELETE FROM generation_task WHERE conversation_id=$id; DELETE FROM conversation WHERE id=$id; COMMIT;", ("$id", requester));
        await using (var reopened = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            for (var i = 0; i < attempts.Count; i++)
            {
                var attempt = (await reopened.LoadCompressionAttemptAsync(attempts[i]))!;
                Assert.Equal(owner, attempt.SourceConversationId); Assert.Equal(config, attempt.Configuration);
                Assert.Equal(caches[i], (await reopened.LoadCompletedSummaryAsync(survivorTask, keys[i]))!.Id);
                var request = ReplyContextPlanner.CreateSummaryRequest(owner, attempt.Chunk, await reopened.ReadMessagesAsync(owner), attempt.Configuration);
                Assert.Equal(ChatRequestPurpose.ContextSummary, request.Purpose);
            }
            Assert.Equal(expectedPrepared, JsonSerializer.Serialize(await reopened.LoadPreparedReplyRequestAsync(survivorTask)));
            Assert.Equal(0L, ContextStorageTests.Sql(paths, "SELECT count(*) FROM pragma_foreign_key_check"));
            Assert.Equal(6L, ContextStorageTests.Sql(paths, "PRAGMA user_version"));
            Assert.Equal(2L, ContextStorageTests.Sql(paths, "SELECT format_version FROM prepared_reply_request WHERE task_id=$id", ("$id", survivorTask)));
        }
    }

    [Theory]
    [InlineData("SummaryThinking", "0", false)] [InlineData("SummaryThinking", "99", true)]
    [InlineData("BaseUrl", "\"https://other.invalid/v1\"", true)]
    public async Task Source_attempt_rejects_tampered_frozen_policy_without_requester_lookup(string property, string value, bool argument)
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        var owner = (await store.CreateConversationAsync((await store.CreateAsync("synthetic")).Id)).Id;
        for (var i = 0; i < 5; i++) await BranchStorageTests.Answer(store, owner, "q" + i, new string('x', 2500));
        var work = await store.AcceptReplyAsync(owner, "current", Config(1) with { ContextTokenBudget = 16000 });
        var attempt = await store.StartCompressionAsync(work.Task.Id, work.ContextPlan!.Parts.OfType<SummaryContextPart>().First().Chunk.CacheKey);
        var node = JsonSerializer.SerializeToNode(attempt.Configuration)!;
        node[property] = System.Text.Json.Nodes.JsonNode.Parse(value);
        if (property == "SummaryThinking" && value == "0") node.AsObject().Remove(property);
        ContextStorageTests.Sql(paths, "DROP TRIGGER compression_identity_immutable; DROP TRIGGER compression_revision; UPDATE context_compression_attempt SET configuration_json=$json WHERE id=$id", ("$json", node.ToJsonString()), ("$id", attempt.Id));
        if (argument) await Assert.ThrowsAsync<ArgumentException>(() => store.LoadCompressionAttemptAsync(attempt.Id));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadCompressionAttemptAsync(attempt.Id));
    }

    [Fact]
    public async Task Old_format4_complete_physical_json_and_active_default_are_unchanged()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "context-format4");
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var data = golden.RootElement;
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        Directory.CreateDirectory(paths.DataDirectory); File.Copy(Path.Combine(root, "format4.db"), paths.DatabasePath);
        await SummaryMigrationTests.UpgradePreservingOldRows(paths);
        var before = File.ReadAllBytes(paths.DatabasePath);
        using var handler = new ContextThinkingTransportTests.CaptureHandler(); using var client = new HttpClient(handler); var transport = new ChatCompletionsTransport(client);
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            var config = (await store.LoadActiveConfigurationAsync())!;
            Assert.Equal(0, Number(config, "SummaryThinking")); Assert.DoesNotContain("SummaryThinking", JsonSerializer.Serialize(config));
            var originals = data.GetProperty("Originals").Deserialize<ChatMessage[]>()!;
            foreach (var item in data.GetProperty("Attempts").EnumerateArray())
            {
                var attempt = (await store.LoadCompressionAttemptAsync(item.GetProperty("Id").GetGuid()))!;
                var request = ReplyContextPlanner.CreateSummaryRequest(attempt.SourceConversationId, attempt.Chunk, originals, config);
                Assert.Equal(0, Number(request, "Purpose")); await ContextThinkingTransportTests.Send(transport, request);
            }
            await ContextThinkingTransportTests.Send(transport, (await store.LoadPreparedReplyRequestAsync(data.GetProperty("TaskId").GetGuid()))!.Request);
        }
        Assert.Equal(data.GetProperty("PhysicalBodies").Deserialize<string[]>(), handler.Bodies);
        Assert.Equal(before, File.ReadAllBytes(paths.DatabasePath));
    }
}
