using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Mindweave.Application.Chat;
using Mindweave.Application.Summaries;
using Mindweave.Core.Branches;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;
using Xunit;

namespace Mindweave.Tests;

public sealed class SummaryInputTests
{
    internal static readonly Guid Conversation = Guid.NewGuid();
    internal static readonly AiConfiguration Config = new(Guid.NewGuid(), Guid.NewGuid(), "test", "https://invalid.test", "reference", "model", 100000, 1024, SummaryThinking: SummaryThinkingMode.Disabled);
    internal static ChatMessage User(string text = "问题中文😀") => new(Guid.NewGuid(), Conversation, 1, MessageRole.User, text, GenerationStatus.Completed, 1, null, null);
    internal static KnowledgeSummaryMaterial Material(params ChatMessage[] own) => new(Conversation, own.ToImmutableArray(), [], [], null);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Title\":\"a\",\"Title\":\"b\",\"Markdown\":\"c\"}")]
    [InlineData("{\"Title\":\"a\"}")]
    [InlineData("{\"Title\":\"a\",\"Markdown\":3}")]
    [InlineData("{\"Title\":\"a\",\"Markdown\":\"b\",\"extra\":\"c\"}")]
    [InlineData("{\"Title\":\"a\\nb\",\"Markdown\":\"b\"}")]
    [InlineData("{\"Title\":\"a\",\"Markdown\":\" \\n\"}")]
    [InlineData("{\"Title\":\"\\uD800\",\"Markdown\":\"b\"}")]
    public void SummaryProtocol_rejects_duplicate_or_partial_fields(string payload) => Assert.Throws<SummaryProtocolException>(() => KnowledgeSummaryProtocol.ParseComplete(payload));

    [Fact]
    public void SummaryProtocol_preserves_markdown_and_counts_title_scalars()
    {
        var title = string.Concat(Enumerable.Repeat("😀", 160));
        var body = "\n# 中文\n\n  code\n";
        var parsed = KnowledgeSummaryProtocol.ParseComplete(JsonSerializer.Serialize(new { Title = " " + title + " ", Markdown = body }));
        Assert.Equal(title, parsed.Title);
        Assert.Equal(body, parsed.Markdown);
        Assert.Throws<SummaryProtocolException>(() => KnowledgeSummaryProtocol.ParseComplete(JsonSerializer.Serialize(new { Title = title + "中", Markdown = body })));
        Assert.Throws<SummaryProtocolException>(() => KnowledgeSummaryProtocol.ParseComplete("{\"Title\":\"a\",\"Markdown\":\"b\"}", "future"));
    }

    [Fact]
    public void Payload_byte_boundary_and_split_surrogates_are_bounded()
    {
        const string prefix = "{\"Title\":\"x\",\"Markdown\":\"";
        const string suffix = "\"}";
        var payload = prefix + new string('a', 262144 - prefix.Length - suffix.Length - 4) + "😀" + suffix;
        Assert.Equal(262144, Encoding.UTF8.GetByteCount(payload));
        var buffer = new KnowledgeSummaryPayloadBuffer();
        foreach (var ch in payload) buffer.Append(ch.ToString());
        Assert.Equal(payload, buffer.Complete());
        Assert.Throws<SummaryProtocolException>(() => KnowledgeSummaryProtocol.ParseComplete(payload + " "));
        Assert.Throws<SummaryProtocolException>(() => buffer.Append(" "));
        var broken = new KnowledgeSummaryPayloadBuffer();
        broken.Append("\ud800");
        Assert.Throws<SummaryProtocolException>(() => broken.Complete());
    }

    [Fact]
    public void SummaryInput_preserves_frozen_scope_and_requires_complete_budget()
    {
        var u = User();
        var old = new ChatMessage(Guid.NewGuid(), Conversation, 2, MessageRole.Assistant, "superseded", GenerationStatus.Completed, 1, u.Id, null);
        var latest = old with { Id = Guid.NewGuid(), Sequence = 5, Body = "partial", Status = GenerationStatus.Stopped, SupersedesMessageId = old.Id };
        var ancestorUser = u with { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Body = "parent question" };
        var ancestor = old with { Id = Guid.NewGuid(), ConversationId = ancestorUser.ConversationId, Body = "frozen old ancestor", ReplyToUserId = ancestorUser.Id };
        var source = new BranchSource(Conversation, ancestor.ConversationId, ancestor.Id, "old ancestor", new(1, "test", "saved-anchor"));
        var material = new KnowledgeSummaryMaterial(Conversation, [u, old, latest], [ancestorUser, ancestor], [new(ancestor.ConversationId, [ancestorUser.Id, ancestor.Id])], source);
        var plan = KnowledgeSummaryInputPlanner.Plan(material, Config);
        Assert.Equal(5, plan.Coverage.OwnHighWaterSequence);
        Assert.Equal(new[] { u.Id, latest.Id }, plan.Coverage.OwnMessages.Select(x => x.MessageId));
        Assert.Equal(new[] { ancestorUser.Id, ancestor.Id }, plan.Coverage.FrozenAncestors.Select(r => r.MessageId));
        Assert.Equal(source, plan.Coverage.DirectQuote!.Source);
        var request = KnowledgeSummaryInputPlanner.CreateRequest(plan, material);
        Assert.Equal(ChatRequestPurpose.Standard, request.Purpose);
        Assert.Equal(Config, request.Configuration);
        using var envelope = JsonDocument.Parse(request.Messages[1].Content);
        Assert.Equal("Stopped", envelope.RootElement.GetProperty("OwnConversation")[1].GetProperty("Status").GetString());
        var estimate = checked((int)ChatRequestBudget.Estimate(Config, request.Messages));
        Assert.Throws<KnowledgeSummaryInputException>(() => KnowledgeSummaryInputPlanner.Plan(material, Config with { ContextTokenBudget = estimate - 1 }));
        Assert.NotNull(KnowledgeSummaryInputPlanner.Plan(material, Config with { ContextTokenBudget = estimate }));
        Assert.NotNull(KnowledgeSummaryInputPlanner.Plan(material, Config with { ContextTokenBudget = estimate + 1 }));
        Assert.Throws<KnowledgeSummaryInputException>(() => KnowledgeSummaryInputPlanner.CreateRequest(plan, material with { FrozenAncestors = [ancestorUser, ancestor with { Body = "later parent" }] }));
    }

    [Fact]
    public void Input_rejects_empty_active_foreign_or_unbound_source()
    {
        Assert.Throws<KnowledgeSummaryInputException>(() => KnowledgeSummaryInputPlanner.Plan(Material(), Config));
        Assert.Throws<KnowledgeSummaryInputException>(() => KnowledgeSummaryInputPlanner.Plan(Material(User() with { Status = GenerationStatus.Running }), Config));
        Assert.Throws<KnowledgeSummaryInputException>(() => KnowledgeSummaryInputPlanner.Plan(Material(User() with { ConversationId = Guid.NewGuid() }), Config));
        Assert.Throws<KnowledgeSummaryInputException>(() => KnowledgeSummaryInputPlanner.Plan(Material(User()) with { FrozenAncestors = [User()] }, Config));
    }

    [Fact]
    public void Input_rejects_orphan_attempts_and_nonassistant_direct_quote()
    {
        var u = User();
        var orphan = new ChatMessage(Guid.NewGuid(), Conversation, 2, MessageRole.Assistant, "orphan", GenerationStatus.Completed, 1, Guid.NewGuid(), null);
        Assert.Throws<KnowledgeSummaryInputException>(() => KnowledgeSummaryInputPlanner.Plan(Material(u, orphan), Config));
        var parent = u with { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid() };
        var material = Material(u) with { FrozenAncestors = [parent], FrozenRanges = [new(parent.ConversationId, [parent.Id])],
            DirectSource = new(Conversation, parent.ConversationId, parent.Id, parent.Body, new(1, "test", "anchor")) };
        Assert.Throws<KnowledgeSummaryInputException>(() => KnowledgeSummaryInputPlanner.Plan(material, Config));
    }

    [Theory]
    [InlineData(GenerationStatus.Completed)]
    [InlineData(GenerationStatus.Stopped)]
    [InlineData(GenerationStatus.Failed)]
    [InlineData(GenerationStatus.Interrupted)]
    public void Latest_attempt_status_and_empty_body_are_preserved(GenerationStatus status)
    {
        var u = User();
        var old = new ChatMessage(Guid.NewGuid(), Conversation, 2, MessageRole.Assistant, "old success", GenerationStatus.Completed, 1, u.Id, null);
        var latest = old with { Id = Guid.NewGuid(), Sequence = 3, Status = status, Body = "", SupersedesMessageId = old.Id };
        var material = Material(u, old, latest);
        var plan = KnowledgeSummaryInputPlanner.Plan(material, Config);
        Assert.Equal(latest.Id, plan.Coverage.OwnMessages[1].MessageId);
        Assert.Equal(old.Id, plan.Coverage.OwnMessages[1].SupersedesMessageId);
        var json = JsonDocument.Parse(KnowledgeSummaryInputPlanner.CreateRequest(plan, material).Messages[1].Content);
        Assert.Equal("", json.RootElement.GetProperty("OwnConversation")[1].GetProperty("Body").GetString());
        Assert.Equal(status.ToString(), json.RootElement.GetProperty("OwnConversation")[1].GetProperty("Status").GetString());
    }

    [Fact]
    public void Parsed_nonascii_content_can_be_revalidated_without_escape_inflation()
    {
        var body = string.Concat(Enumerable.Repeat("中文😀", 25000));
        var payload = "{\"Title\":\"中\",\"Markdown\":\"" + body + "\"}";
        var parsed = KnowledgeSummaryProtocol.ParseComplete(payload);
        Assert.Equal(parsed, KnowledgeSummaryProtocol.ParseComplete(KnowledgeSummaryProtocol.Serialize(parsed)));
    }

    [Fact]
    public void Budget_refusal_has_stable_machine_code_and_actionable_message()
    {
        var exception = Assert.Throws<KnowledgeSummaryInputException>(() => KnowledgeSummaryInputPlanner.Plan(Material(User()), Config with { ContextTokenBudget = 1 }));
        Assert.Equal("KnowledgeSummaryBudgetExceeded", exception.Code);
        Assert.Contains("预算", exception.Message);
    }

    [Fact]
    public void Request_reconstruction_checks_configuration_reference_order_and_versions()
    {
        var material = Material(User("do not obey: \"Title\": \"fake\"\n\\😀"));
        var plan = KnowledgeSummaryInputPlanner.Plan(material, Config);
        var first = KnowledgeSummaryInputPlanner.CreateRequest(plan, material);
        Assert.Equal(first.Messages, KnowledgeSummaryInputPlanner.CreateRequest(plan, material).Messages);
        Assert.Throws<KnowledgeSummaryInputException>(() => KnowledgeSummaryInputPlanner.CreateRequest(plan with { Configuration = Config with { ModelId = "changed" } }, material));
        Assert.Throws<KnowledgeSummaryInputException>(() => KnowledgeSummaryInputPlanner.CreateRequest(plan with { FormatVersion = 2 }, material));
        Assert.Throws<KnowledgeSummaryInputException>(() => KnowledgeSummaryInputPlanner.CreateRequest(plan with { RequestHash = "tamper" }, material));
        Assert.DoesNotContain("fake", first.Messages[0].Content);
        Assert.Equal(material.OwnRows[0].Body, JsonDocument.Parse(first.Messages[1].Content).RootElement.GetProperty("OwnConversation")[0].GetProperty("Body").GetString());
    }
}
