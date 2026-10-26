using System.Text.Json;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed class ContextBudgetVersionTests
{
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Target = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static ChatMessage[] Originals() =>
    [
        new(Guid.NewGuid(), Owner, 1, MessageRole.User, new string('a', 16000), GenerationStatus.Completed, 1, null, null),
        new(Guid.NewGuid(), Target, 1, MessageRole.User, "current", GenerationStatus.Completed, 1, null, null)
    ];
    private static AiConfiguration Config(int output) => BranchStorageTests.Config(10000) with { MaxOutputTokens = output };
    private static ContextChunk[] Chunks(ReplyContextPlan plan) => plan.Parts.OfType<SummaryContextPart>().Select(p => p.Chunk).ToArray();
    private static Dictionary<string, ContextSummaryContent> Summaries(ReplyContextPlan plan, int bytes = 8) =>
        Chunks(plan).ToDictionary(c => c.CacheKey, _ => new ContextSummaryContent(new string('x', bytes), "", "", "", ""));

    [Theory]
    [InlineData(128, 128)] [InlineData(1024, 1024)] [InlineData(2048, 2048)] [InlineData(4096, 4096)] [InlineData(6000, 4096)]
    public void New_plans_reserve_the_actual_summary_output_during_splitting_and_sending(int output, int expected)
    {
        var originals = Originals(); var config = Config(output);
        var plan = ReplyContextPlanner.Plan(Target, new(originals, null), config);
        var chunks = Chunks(plan); Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            Assert.Equal(expected, chunk.MaxOutputTokens);
            var request = ReplyContextPlanner.CreateSummaryRequest(Owner, chunk, originals, config);
            Assert.Equal(expected, request.Configuration.MaxOutputTokens);
            Assert.True(ChatRequestBudget.Fits(request.Configuration, request.Messages));
        }
        Assert.Equal(originals[0].Body, string.Concat(chunks.SelectMany(c => c.Slices).Select(s => originals[0].Body.Substring(s.Start, s.Length))));
        var inputs = ReplyContextPlanner.ComposeInputs(plan, originals, Summaries(plan), config);
        Assert.True(ChatRequestBudget.Fits(config, inputs));
        Assert.Equal("current", inputs[^1].Content);
        Assert.Equal(3, plan.FormatVersion);
    }

    [Fact]
    public void Larger_output_reserve_can_make_summary_wrapper_infeasible_before_request()
    {
        var config = Config(4096) with { ContextTokenBudget = 5100 };
        var originals = Originals(); originals[0] = originals[0] with { Body = new string('a', 2000) };
        Assert.True(ChatRequestBudget.Fits(config, ChatRequestBudget.Inputs(Originals().TakeLast(1))));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.Plan(Target, new(originals, null), config));
    }

    [Fact]
    public void New_final_request_checks_actual_summary_size_and_rejects_forged_cap_key_or_version()
    {
        var originals = Originals(); var config = Config(4096);
        var plan = ReplyContextPlanner.Plan(Target, new(originals, null), config);
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan, originals, Summaries(plan, 10000), config));
        var chunk = Chunks(plan)[0];
        foreach (var forged in new[] { chunk with { MaxOutputTokens = 1024 }, chunk with { MaxOutputTokens = 4097 }, chunk with { CacheKey = new string('0', 64) } })
            Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.CreateSummaryRequest(Owner, forged, originals, config));
        foreach (var version in new[] { 2, 0, 4, 99 })
            Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan with { FormatVersion = version }, originals, Summaries(plan), config));
    }

    [Theory]
    [InlineData(128)] [InlineData(1024)]
    public void Equal_caps_still_have_separate_version_identities_and_cannot_cross_plans(int output)
    {
        var originals = Originals(); var config = Config(output); var context = new ChatContext(originals, null);
        var legacy = ReplyContextPlanner.Rebuild(2, Target, context, config);
        var current = ReplyContextPlanner.Plan(Target, context, config);
        var oldChunks = Chunks(legacy); var newChunks = Chunks(current);
        Assert.Equal(oldChunks.Length, newChunks.Length);
        for (var i = 0; i < oldChunks.Length; i++)
        {
            Assert.Equal(oldChunks[i].Slices, newChunks[i].Slices);
            Assert.Equal(output, oldChunks[i].MaxOutputTokens); Assert.Equal(output, newChunks[i].MaxOutputTokens);
            Assert.NotEqual(oldChunks[i].CacheKey, newChunks[i].CacheKey);
            Assert.Equal(output, ReplyContextPlanner.CreateSummaryRequest(Owner, oldChunks[i], originals, config).Configuration.MaxOutputTokens);
            Assert.Equal(output, ReplyContextPlanner.CreateSummaryRequest(Owner, newChunks[i], originals, config).Configuration.MaxOutputTokens);
        }
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(legacy with { FormatVersion = 3 }, originals, Summaries(legacy), config));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(current with { FormatVersion = 2 }, originals, Summaries(current), config));
        foreach (var version in new[] { 0, 1, 4, 99 })
            Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.Rebuild(version, Target, context, config));
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "context-format2", name);
    private static JsonElement Golden => JsonDocument.Parse(File.ReadAllText(Fixture("manifest.json"))).RootElement.Clone();
    private static T Read<T>(JsonElement golden, string property) => golden.GetProperty(property).Deserialize<T>()!;
    private static AppPaths CopyGolden(TestDirectory dir)
    {
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        Directory.CreateDirectory(paths.DataDirectory);
        File.Copy(Fixture("format2.db"), paths.DatabasePath);
        return paths;
    }

    [Fact]
    public async Task Baseline_format2_plan_source_cache_and_prepared_reopen_without_changing_any_database_byte()
    {
        using var dir = new TestDirectory(); var paths = CopyGolden(dir); var golden = Golden;
        var before = File.ReadAllBytes(paths.DatabasePath);
        var id = Read<Guid>(golden, "TaskId");
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            var plan = (await store.LoadContextPlanAsync(id))!; Assert.Equal(2, plan.FormatVersion);
            var config = Read<AiConfiguration>(golden, "Configuration"); Assert.Equal(4096, config.MaxOutputTokens);
            var originals = Read<ChatMessage[]>(golden, "Originals");
            var expectedChunks = Read<ContextChunk[]>(golden, "Chunks");
            Assert.Equal(JsonSerializer.Serialize(expectedChunks), JsonSerializer.Serialize(Chunks(plan)));
            var summaries = Read<CompletedContextSummary[]>(golden, "Summaries");
            var requests = Read<ChatRequest[]>(golden, "SummaryRequests");
            for (var i = 0; i < expectedChunks.Length; i++)
            {
                var cached = (await store.LoadCompletedSummaryAsync(id, expectedChunks[i].CacheKey))!;
                Assert.Equal(JsonSerializer.Serialize(summaries[i]), JsonSerializer.Serialize(cached));
                var attempt = (await store.LoadCompressionAttemptAsync(cached.AttemptId))!;
                Assert.Equal(1024, attempt.Chunk.MaxOutputTokens);
                var request = ReplyContextPlanner.CreateSummaryRequest(attempt.SourceConversationId, attempt.Chunk, originals, config);
                Assert.Equal(JsonSerializer.Serialize(requests[i]), JsonSerializer.Serialize(request));
            }
            Assert.Equal(JsonSerializer.Serialize(Read<PreparedReplyRequest>(golden, "Prepared")), JsonSerializer.Serialize(await store.LoadPreparedReplyRequestAsync(id)));
            Assert.Equal(Read<string>(golden, "PreparedHash"), ContextStorageTests.Sql(paths, "SELECT request_hash FROM prepared_reply_request WHERE task_id=$id", ("$id", id)));
            Assert.Equal(2L, ContextStorageTests.Sql(paths, "SELECT format_version FROM prepared_reply_request WHERE task_id=$id", ("$id", id)));
        }
        Assert.Equal(before, File.ReadAllBytes(paths.DatabasePath));
    }

    [Fact]
    public async Task Reopened_old_database_accepts_format3_without_reusing_old_cache_or_rewriting_old_plan()
    {
        using var dir = new TestDirectory(); var paths = CopyGolden(dir); var golden = Golden;
        var id = Read<Guid>(golden, "TaskId");
        var oldPlan = ContextStorageTests.Sql(paths, "SELECT plan_json FROM reply_context_plan WHERE task_id=$id", ("$id", id));
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        var work = await store.AcceptReplyAsync(Read<Guid>(golden, "SiblingId"), null, Read<AiConfiguration>(golden, "Configuration"));
        Assert.Equal(3, work.ContextPlan!.FormatVersion);
        var oldKeys = Read<ContextChunk[]>(golden, "Chunks").Select(c => c.CacheKey).ToHashSet();
        foreach (var chunk in Chunks(work.ContextPlan))
        {
            Assert.Equal(4096, chunk.MaxOutputTokens); Assert.DoesNotContain(chunk.CacheKey, oldKeys);
            Assert.Null(await store.LoadCompletedSummaryAsync(work.Task.Id, chunk.CacheKey));
            var attempt = await store.StartCompressionAsync(work.Task.Id, chunk.CacheKey);
            await store.WriteCompressionAsync(work.Task.Id, attempt.Id, 1, "{\"Background\":\"new facts\",\"Requirements\":\"\",\"Corrections\":\"\",\"OpenQuestions\":\"\",\"References\":\"\"}", GenerationStatus.Completed, "stop");
        }
        var prepared = await store.PrepareReplyRequestAsync(work.Task.Id);
        Assert.True(ChatRequestBudget.Fits(prepared.Request.Configuration, prepared.Request.Messages));
        Assert.Equal(2L, ContextStorageTests.Sql(paths, "SELECT format_version FROM prepared_reply_request WHERE task_id=$id", ("$id", work.Task.Id)));
        Assert.Equal(oldPlan, ContextStorageTests.Sql(paths, "SELECT plan_json FROM reply_context_plan WHERE task_id=$id", ("$id", id)));
        Assert.NotNull(await store.LoadPreparedReplyRequestAsync(id));
    }

    [Fact]
    public async Task Legacy_source_cache_remains_independently_readable_after_original_requester_deletion()
    {
        using var dir = new TestDirectory(); var paths = CopyGolden(dir); var golden = Golden;
        ContextStorageTests.Sql(paths, "BEGIN; PRAGMA defer_foreign_keys=ON; UPDATE app_selection SET kind='Overview',target_id=NULL; DELETE FROM generation_task WHERE conversation_id=$id; DELETE FROM conversation WHERE id=$id; COMMIT;", ("$id", Read<Guid>(golden, "ProducerId")));
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        foreach (var cached in Read<CompletedContextSummary[]>(golden, "Summaries"))
        {
            var loaded = (await store.LoadCompressionAttemptAsync(cached.AttemptId))!;
            Assert.Equal(Read<Guid>(golden, "RootId"), loaded.SourceConversationId);
            Assert.Equal(cached.Id, (await store.LoadCompletedSummaryAsync(Read<Guid>(golden, "TaskId"), cached.Chunk.CacheKey))!.Id);
        }
        Assert.Equal(JsonSerializer.Serialize(Read<PreparedReplyRequest>(golden, "Prepared")), JsonSerializer.Serialize(await store.LoadPreparedReplyRequestAsync(Read<Guid>(golden, "TaskId"))));
        Assert.Equal(0L, ContextStorageTests.Sql(paths, "SELECT count(*) FROM pragma_foreign_key_check"));
    }

    [Theory]
    [InlineData("unknown-version")] [InlineData("cross-version")] [InlineData("missing-version")]
    [InlineData("invalid-json")] [InlineData("invalid-version-type")] [InlineData("plan-cap")] [InlineData("plan-key")]
    public async Task Legacy_plan_tampering_is_rejected_without_rewriting_evidence(string change)
    {
        using var dir = new TestDirectory(); var paths = CopyGolden(dir); var id = Read<Guid>(Golden, "TaskId");
        var node = System.Text.Json.Nodes.JsonNode.Parse((string)ContextStorageTests.Sql(paths, "SELECT plan_json FROM reply_context_plan WHERE task_id=$id", ("$id", id))!)!;
        switch (change)
        {
            case "unknown-version": node["FormatVersion"] = 99; break;
            case "cross-version": node["FormatVersion"] = 3; break;
            case "missing-version": node.AsObject().Remove("FormatVersion"); break;
            case "invalid-version-type": node["FormatVersion"] = "2"; break;
            case "plan-cap": node["Parts"]![0]!["Chunk"]!["MaxOutputTokens"] = 4096; break;
            case "plan-key": node["Parts"]![0]!["Chunk"]!["CacheKey"] = new string('0', 64); break;
        }
        var forged = change == "invalid-json" ? "{" : node.ToJsonString();
        ContextStorageTests.Sql(paths, "DROP TRIGGER context_plan_immutable; PRAGMA ignore_check_constraints=ON; UPDATE reply_context_plan SET plan_json=$json WHERE task_id=$id", ("$json", forged), ("$id", id));
        var before = File.ReadAllBytes(paths.DatabasePath);
        if (change == "invalid-json")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => SqliteWorkspaceStore.OpenAsync(paths));
            Assert.Equal(before, File.ReadAllBytes(paths.DatabasePath));
            return;
        }
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadContextPlanAsync(id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadPreparedReplyRequestAsync(id));
        }
        Assert.Equal(before, File.ReadAllBytes(paths.DatabasePath));
    }

    [Theory]
    [InlineData("cap")] [InlineData("key")]
    public async Task Legacy_source_attempt_rejects_forged_version_identity_without_requester_plan(string change)
    {
        using var dir = new TestDirectory(); var paths = CopyGolden(dir); var golden = Golden;
        var attemptId = Read<CompletedContextSummary[]>(golden, "Summaries")[0].AttemptId;
        // Inject corruption after opening so this exercises source validation, not the startup FK gate.
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        ContextStorageTests.Sql(paths, "DROP TRIGGER compression_identity_immutable; DROP TRIGGER terminal_compression_immutable; DROP TRIGGER compression_revision;");
        if (change == "cap")
            ContextStorageTests.Sql(paths, "UPDATE context_compression_attempt SET chunk_json=json_set(chunk_json,'$.MaxOutputTokens',4096) WHERE id=$id", ("$id", attemptId));
        else
            ContextStorageTests.Sql(paths, "PRAGMA foreign_keys=OFF; UPDATE context_compression_attempt SET cache_key=$key,chunk_json=json_set(chunk_json,'$.CacheKey',$key) WHERE id=$id", ("$id", attemptId), ("$key", new string('0', 64)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadCompressionAttemptAsync(attemptId));
    }

}
