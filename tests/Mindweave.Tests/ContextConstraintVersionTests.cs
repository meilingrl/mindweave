using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Infrastructure.AI;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed class ContextConstraintVersionTests
{
    private static ContextChunk[] Chunks(ReplyContextPlan plan) => plan.Parts.OfType<SummaryContextPart>().Select(p => p.Chunk).ToArray();
    private static Dictionary<string, ContextSummaryContent> Summaries(ReplyContextPlan plan) =>
        Chunks(plan).ToDictionary(c => c.CacheKey, _ => new ContextSummaryContent("facts", "", "", "", ""));
    // Independent wire identity envelope: version and prompt must agree, even with identical caps.
    private static string Key(ContextChunk chunk, ChatMessage[] originals, AiConfiguration config, int version, string prompt)
    {
        var byId = originals.ToDictionary(m => m.Id);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            FormatVersion = version, PromptVersion = prompt, ChatRequestBudget.EstimatorProfile,
            chunk.SourceConversationId, Configuration = config,
            Slices = chunk.Slices.Select(s => new { Slice = s, byId[s.MessageId].ConversationId, byId[s.MessageId].Role,
                byId[s.MessageId].Status, byId[s.MessageId].ReplyToUserId, byId[s.MessageId].SupersedesMessageId })
        }))));
    }

    [Fact]
    public void Current_summary_request_instructs_preservation_of_the_rule_not_only_its_referent()
    {
        var owner = Guid.NewGuid(); var target = Guid.NewGuid(); var originals = ContextThinkingTests.Originals(owner, target);
        var config = ContextThinkingTests.Config();
        var plan = ReplyContextPlanner.Plan(target, new(originals, null), config);
        var instruction = ReplyContextPlanner.CreateSummaryRequest(owner, Chunks(plan)[0], originals, config).Messages[0].Content;
        Assert.Contains("object, action, negation/prohibition, conditions and scope", instruction);
        Assert.Contains("Resolving a pronoun does not replace retaining the actual rule", instruction);
        Assert.Contains("Remove low-value background first; do not sacrifice effective constraints to meet the brevity target", instruction);
    }

    [Theory] [InlineData(0)] [InlineData(1)]
    public async Task New_acceptance_sends_complete_constraint_instruction_with_v3_identity_and_summary_only_policy(int mode)
    {
        var owner = Guid.NewGuid(); var target = Guid.NewGuid(); var originals = ContextThinkingTests.Originals(owner, target);
        originals[0] = originals[0] with { Body = "The sample crate is fragile. It must not be stacked during transit. " + originals[0].Body };
        var config = ContextThinkingTests.Config(mode);
        var plan = ReplyContextPlanner.Plan(target, new(originals, null), config);
        Assert.Equal(6, plan.FormatVersion);
        var legacy = ReplyContextPlanner.Rebuild(5, target, new(originals, null), config);
        Assert.NotEmpty(Chunks(plan)); Assert.Empty(Chunks(plan).Select(c => c.CacheKey).Intersect(Chunks(legacy).Select(c => c.CacheKey)));
        using var handler = new ContextThinkingTransportTests.CaptureHandler(); using var client = new HttpClient(handler);
        var transport = new ChatCompletionsTransport(client);
        foreach (var chunk in Chunks(plan))
        {
            var request = ReplyContextPlanner.CreateSummaryRequest(owner, chunk, originals, config);
            Assert.Equal(Key(chunk, originals, config, 6, "context-summary-v3"), chunk.CacheKey);
            var instruction = request.Messages[0].Content;
            Assert.Contains("object, action, negation/prohibition, conditions and scope", instruction);
            Assert.Contains("Resolving a pronoun does not replace retaining the actual rule", instruction);
            Assert.Contains("Remove low-value background first; do not sacrifice effective constraints to meet the brevity target", instruction);
            Assert.Contains("Avoid repeating facts within or across fields", instruction);
            Assert.Contains("1024 UTF-8 bytes", instruction);
            Assert.DoesNotContain("crate", instruction); // No scenario-specific answer injected into the protocol.
            Assert.DoesNotContain("current", request.Messages[1].Content);
            Assert.Equal(ChatRequestPurpose.ContextSummary, request.Purpose);
            Assert.True(ChatRequestBudget.Fits(request.Configuration, request.Messages));
            await ContextThinkingTransportTests.Send(transport, request);
        }
        foreach (var body in handler.Bodies)
        {
            using var wire = JsonDocument.Parse(body!);
            Assert.Equal(mode == 1, wire.RootElement.TryGetProperty("thinking", out _));
        }
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan, originals, Summaries(legacy), config));
        var inputs = ReplyContextPlanner.ComposeInputs(plan, originals, Summaries(plan), config);
        Assert.True(ChatRequestBudget.Fits(config, inputs));
        await ContextThinkingTransportTests.Send(transport, new ChatRequest(config, inputs));
        using var reply = JsonDocument.Parse(handler.Bodies[^1]!); Assert.False(reply.RootElement.TryGetProperty("thinking", out _));
    }

    [Theory]
    [InlineData(2, 0, "context-summary-v1", 1024, 0)]
    [InlineData(3, 0, "context-summary-v1", 4096, 0)]
    [InlineData(4, 0, "context-summary-v2", 4096, 0)]
    [InlineData(5, 0, "context-summary-v2", 4096, 1)] [InlineData(5, 1, "context-summary-v2", 4096, 1)]
    [InlineData(6, 0, "context-summary-v3", 4096, 1)] [InlineData(6, 1, "context-summary-v3", 4096, 1)]
    public void Exact_version_matrix_authenticates_prompt_cap_purpose_and_entire_frozen_configuration(int version, int mode, string prompt, int cap, int purpose)
    {
        var owner = Guid.NewGuid(); var target = Guid.NewGuid(); var originals = ContextThinkingTests.Originals(owner, target);
        var config = ContextThinkingTests.Config(mode); var plan = ReplyContextPlanner.Rebuild(version, target, new(originals, null), config);
        foreach (var chunk in Chunks(plan))
        {
            var request = ReplyContextPlanner.CreateSummaryRequest(owner, chunk, originals, config);
            Assert.Equal(cap, chunk.MaxOutputTokens); Assert.Equal(cap, request.Configuration.MaxOutputTokens);
            Assert.Equal(purpose, (int)request.Purpose); Assert.Equal(Key(chunk, originals, config, version, prompt), chunk.CacheKey);
            foreach (var changed in new[] { config with { ConnectionId = Guid.NewGuid() }, config with { ModelConfigId = Guid.NewGuid() },
                config with { Name = "renamed" }, config with { TokenLimitParameter = "max_completion_tokens" }, config with { ModelId = "other" }, config with { CredentialReference = "other" },
                config with { BaseUrl = "https://api.deepseek.com" }, config with { ContextTokenBudget = 11000 },
                config with { MaxOutputTokens = 6000 }, config with { SummaryThinking = mode == 0 ? SummaryThinkingMode.Disabled : SummaryThinkingMode.ServiceDefault } })
                Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.CreateSummaryRequest(owner, chunk, originals, changed));
            foreach (var wrongPrompt in new[] { "context-summary-v1", "context-summary-v2", "context-summary-v3" }.Where(p => p != prompt))
                Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.CreateSummaryRequest(owner,
                    chunk with { CacheKey = Key(chunk, originals, config, version, wrongPrompt) }, originals, config));
            Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.CreateSummaryRequest(owner,
                chunk with { CacheKey = new string('0', 64) }, originals, config));
        }
        foreach (var other in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 99 }.Where(v => v != version))
            Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan with { FormatVersion = other }, originals, Summaries(plan), config));
    }

    private static JsonElement Golden(string mode) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "context-format5", mode, "manifest.json"))).RootElement.Clone();
    private static T Read<T>(JsonElement golden, string property) => golden.GetProperty(property).Deserialize<T>()!;
    private static AppPaths CopyGolden(TestDirectory dir, string mode)
    {
        var paths = AppPaths.ForTrack("codex-steady", dir.Path); Directory.CreateDirectory(paths.DataDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "context-format5", mode, "format5.db"), paths.DatabasePath);
        return paths;
    }

    [Theory] [InlineData("default", false)] [InlineData("disabled", false)] [InlineData("default", true)] [InlineData("disabled", true)]
    public async Task Format5_complete_requests_cache_and_prepared_remain_identical_even_without_original_requester(string mode, bool deleteRequester)
    {
        using var dir = new TestDirectory(); var paths = CopyGolden(dir, mode); var golden = Golden(mode);
        if (deleteRequester)
            ContextStorageTests.Sql(paths, "BEGIN; PRAGMA defer_foreign_keys=ON; UPDATE app_selection SET kind='Overview',target_id=NULL; DELETE FROM generation_task WHERE conversation_id=$id; DELETE FROM conversation WHERE id=$id; COMMIT;", ("$id", Read<Guid>(golden, "ProducerId")));
        var before = File.ReadAllBytes(paths.DatabasePath); var id = Read<Guid>(golden, "TaskId");
        using var handler = new ContextThinkingTransportTests.CaptureHandler(); using var client = new HttpClient(handler); var transport = new ChatCompletionsTransport(client);
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            var config = Read<AiConfiguration>(golden, "Configuration"); Assert.Equal(config, await store.LoadActiveConfigurationAsync());
            var plan = (await store.LoadContextPlanAsync(id))!; Assert.Equal(5, plan.FormatVersion);
            Assert.Equal(JsonSerializer.Serialize(Read<ContextChunk[]>(golden, "Chunks")), JsonSerializer.Serialize(Chunks(plan)));
            var requests = Read<ChatRequest[]>(golden, "SummaryRequests"); var attempts = Read<ContextCompressionAttempt[]>(golden, "Attempts");
            var summaries = Read<CompletedContextSummary[]>(golden, "Summaries");
            for (var i = 0; i < attempts.Length; i++)
            {
                var attempt = (await store.LoadCompressionAttemptAsync(attempts[i].Id))!;
                Assert.Equal(JsonSerializer.Serialize(attempts[i]), JsonSerializer.Serialize(attempt));
                Assert.Equal(Read<Guid>(golden, "RootId"), attempt.SourceConversationId);
                Assert.Equal(JsonSerializer.Serialize(summaries[i]), JsonSerializer.Serialize(await store.LoadCompletedSummaryAsync(id, attempt.Chunk.CacheKey)));
                var request = ReplyContextPlanner.CreateSummaryRequest(attempt.SourceConversationId, attempt.Chunk,
                    await store.ReadMessagesAsync(attempt.SourceConversationId), attempt.Configuration);
                Assert.Equal(JsonSerializer.Serialize(requests[i]), JsonSerializer.Serialize(request));
                await ContextThinkingTransportTests.Send(transport, request);
            }
            var prepared = (await store.LoadPreparedReplyRequestAsync(id))!;
            Assert.Equal(JsonSerializer.Serialize(Read<PreparedReplyRequest>(golden, "Prepared")), JsonSerializer.Serialize(prepared));
            Assert.Equal(Read<string>(golden, "PreparedHash"), ContextStorageTests.Sql(paths, "SELECT request_hash FROM prepared_reply_request WHERE task_id=$id", ("$id", id)));
            await ContextThinkingTransportTests.Send(transport, prepared.Request);
            Assert.Equal(0L, ContextStorageTests.Sql(paths, "SELECT count(*) FROM pragma_foreign_key_check"));
        }
        Assert.Equal(Read<string[]>(golden, "PhysicalBodies"), handler.Bodies);
        Assert.Equal(before, File.ReadAllBytes(paths.DatabasePath));
    }

    [Theory] [InlineData("default")] [InlineData("disabled")]
    public async Task Old_format5_database_accepts_new6_without_reusing_or_rewriting_old_cache(string mode)
    {
        using var dir = new TestDirectory(); var paths = CopyGolden(dir, mode); var golden = Golden(mode);
        var oldTask = Read<Guid>(golden, "TaskId");
        var oldPlan = ContextStorageTests.Sql(paths, "SELECT plan_json FROM reply_context_plan WHERE task_id=$id", ("$id", oldTask));
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        var work = await store.AcceptReplyAsync(Read<Guid>(golden, "SiblingId"), null, Read<AiConfiguration>(golden, "Configuration"));
        Assert.Equal(6, work.ContextPlan!.FormatVersion); Assert.NotEmpty(Chunks(work.ContextPlan));
        foreach (var chunk in Chunks(work.ContextPlan))
        {
            Assert.DoesNotContain(chunk.CacheKey, Read<ContextChunk[]>(golden, "Chunks").Select(c => c.CacheKey));
            Assert.Null(await store.LoadCompletedSummaryAsync(work.Task.Id, chunk.CacheKey));
            var attempt = await store.StartCompressionAsync(work.Task.Id, chunk.CacheKey);
            await store.WriteCompressionAsync(work.Task.Id, attempt.Id, 1, ContextSummaryProtocol.Serialize(new("facts", "", "", "", "")), GenerationStatus.Completed, "stop");
        }
        var prepared = await store.PrepareReplyRequestAsync(work.Task.Id);
        Assert.True(ChatRequestBudget.Fits(prepared.Request.Configuration, prepared.Request.Messages));
        Assert.Equal(oldPlan, ContextStorageTests.Sql(paths, "SELECT plan_json FROM reply_context_plan WHERE task_id=$id", ("$id", oldTask)));
        Assert.Equal(JsonSerializer.Serialize(Read<PreparedReplyRequest>(golden, "Prepared")), JsonSerializer.Serialize(await store.LoadPreparedReplyRequestAsync(oldTask)));
        foreach (var cached in Read<CompletedContextSummary[]>(golden, "Summaries"))
            Assert.Equal(JsonSerializer.Serialize(cached), JsonSerializer.Serialize(await store.LoadCompletedSummaryAsync(oldTask, cached.Chunk.CacheKey)));
    }
}
