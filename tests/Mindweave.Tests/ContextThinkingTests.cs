using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Xunit;

namespace Mindweave.Tests;

public sealed class ContextThinkingTests
{
    // Inject through the persisted JSON boundary so these tests also execute against the old API.
    internal static T Property<T>(T value, string name, int number)
    {
        var node = JsonSerializer.SerializeToNode(value)!; node[name] = number;
        return node.Deserialize<T>()!;
    }
    internal static AiConfiguration Config(int mode = 0, string url = "https://api.deepseek.com/v1") =>
        Property(BranchStorageTests.Config(10000) with { BaseUrl = url, MaxOutputTokens = 4096 }, "SummaryThinking", mode);
    internal static int Number<T>(T value, string name) => JsonSerializer.SerializeToNode(value)![name]?.GetValue<int>() ?? 0;
    internal static ChatMessage[] Originals(Guid owner, Guid target) =>
    [
        new(Guid.NewGuid(), owner, 1, MessageRole.User, new string('a', 16000), GenerationStatus.Completed, 1, null, null),
        new(Guid.NewGuid(), target, 1, MessageRole.User, "current", GenerationStatus.Completed, 1, null, null)
    ];

    [Fact]
    public void Public_policy_validates_only_new_boundary_and_keeps_default_general_rules_unchanged()
    {
        Assert.Equal(0, (int)SummaryThinkingMode.ServiceDefault); Assert.Equal(1, (int)SummaryThinkingMode.Disabled);
        Assert.Equal(0, (int)ChatRequestPurpose.Standard); Assert.Equal(1, (int)ChatRequestPurpose.ContextSummary);
        var config = Config() with { BaseUrl = "not a URI", ModelId = "", ContextTokenBudget = 0 };
        SummaryThinkingPolicy.ValidateConfiguration(config);
        Assert.False(SummaryThinkingPolicy.IsOfficialDeepSeekEndpoint(config.BaseUrl));
        Assert.True(SummaryThinkingPolicy.IsOfficialDeepSeekEndpoint("https://api.deepseek.com/v1/"));
        Assert.Throws<ArgumentException>(() => SummaryThinkingPolicy.ValidateConfiguration(config with { SummaryThinking = SummaryThinkingMode.Disabled }));
        var request = new ChatRequest(Config(), [new("user", "hello")], ChatRequestPurpose.ContextSummary);
        Assert.Equal(ChatRequestPurpose.ContextSummary, request.Purpose);
        Assert.Equal(SummaryThinkingMode.ServiceDefault, request.Configuration.SummaryThinking);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Forged_legacy_disabled_key_cannot_authenticate_even_with_matching_cap(int version)
    {
        var owner = Guid.NewGuid(); var target = Guid.NewGuid(); var originals = Originals(owner, target);
        var config = Config(); var disabled = config with { SummaryThinking = SummaryThinkingMode.Disabled };
        var plan = ReplyContextPlanner.Rebuild(version, target, new(originals, null), config);
        var chunk = plan.Parts.OfType<SummaryContextPart>().First().Chunk;
        var byId = originals.ToDictionary(m => m.Id);
        var forgedKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            FormatVersion = version, PromptVersion = version == 4 ? "context-summary-v2" : "context-summary-v1",
            EstimatorProfile = ChatRequestBudget.EstimatorProfile, SourceConversationId = owner, Configuration = disabled,
            Slices = chunk.Slices.Select(slice => new { Slice = slice, byId[slice.MessageId].ConversationId,
                byId[slice.MessageId].Role, byId[slice.MessageId].Status, byId[slice.MessageId].ReplyToUserId, byId[slice.MessageId].SupersedesMessageId })
        }))));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.CreateSummaryRequest(owner, chunk with { CacheKey = forgedKey }, originals, disabled));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan, originals,
            plan.Parts.OfType<SummaryContextPart>().ToDictionary(p => p.Chunk.CacheKey, _ => new ContextSummaryContent("facts", "", "", "", "")), disabled));
    }

    [Fact]
    public void New_policy_roundtrips_and_default_fields_alone_are_omitted()
    {
        var config = Config(); var request = new ChatRequest(config, [new("user", "current")]);
        Assert.DoesNotContain("SummaryThinking", JsonSerializer.Serialize(config));
        Assert.DoesNotContain("Purpose", JsonSerializer.Serialize(request));
        Assert.Equal(1, Number(Config(1), "SummaryThinking"));
        Assert.Equal(1, Number(Property(request, "Purpose", 1), "Purpose"));
        // A global ignore-default option would omit these identity-bearing zero fields.
        var slice = new ContextSourceSlice(Guid.NewGuid(), 0, "hash", 0, 0);
        Assert.Contains("\"Start\":0", JsonSerializer.Serialize(slice));
        Assert.Contains("\"Length\":0", JsonSerializer.Serialize(slice));
        Assert.Contains("\"Revision\":0", JsonSerializer.Serialize(slice));
    }

    [Fact]
    public void Format5_modes_have_disjoint_keys_and_only_new_summaries_have_explicit_purpose()
    {
        var owner = Guid.NewGuid(); var target = Guid.NewGuid(); var originals = Originals(owner, target);
        var config = Config(); var disabled = Property(config, "SummaryThinking", 1);
        var normal = ReplyContextPlanner.Plan(target, new(originals, null), config);
        var off = ReplyContextPlanner.Plan(target, new(originals, null), disabled);
        Assert.Equal(5, normal.FormatVersion); Assert.Equal(5, off.FormatVersion);
        var normalChunks = normal.Parts.OfType<SummaryContextPart>().Select(p => p.Chunk).ToArray();
        var offChunks = off.Parts.OfType<SummaryContextPart>().Select(p => p.Chunk).ToArray();
        Assert.NotEmpty(normalChunks);
        Assert.Equal(normalChunks.Length, offChunks.Length);
        Assert.Empty(normalChunks.Select(c => c.CacheKey).Intersect(offChunks.Select(c => c.CacheKey)));
        foreach (var (plan, frozen) in new[] { (normal, config), (off, disabled) })
        {
            var chunks = plan.Parts.OfType<SummaryContextPart>().Select(p => p.Chunk).ToArray();
            foreach (var c in chunks)
            {
                var request = ReplyContextPlanner.CreateSummaryRequest(owner, c, originals, frozen);
                Assert.Equal(1, Number(request, "Purpose")); Assert.Equal(4096, request.Configuration.MaxOutputTokens);
                Assert.True(ChatRequestBudget.Fits(request.Configuration, request.Messages));
                Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.CreateSummaryRequest(owner, c, originals, frozen == config ? disabled : config));
                Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.CreateSummaryRequest(target, c, originals, frozen));
            }
            var summaries = chunks.ToDictionary(c => c.CacheKey, _ => new ContextSummaryContent("facts", "", "", "", ""));
            Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan with { FormatVersion = 4 }, originals, summaries, frozen));
        }
        foreach (var version in new[] { 2, 3, 4 })
        {
            var old = ReplyContextPlanner.Rebuild(version, target, new(originals, null), config);
            var chunk = old.Parts.OfType<SummaryContextPart>().First().Chunk;
            Assert.Equal(0, Number(ReplyContextPlanner.CreateSummaryRequest(owner, chunk, originals, config), "Purpose"));
            Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.Rebuild(version, target, new(originals, null), disabled));
            // Even a forged old-format key generated with the new config must not grant permission.
            Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.CreateSummaryRequest(owner, chunk, originals, disabled));
        }
    }
}
