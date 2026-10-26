using System.Text;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;
using Xunit;

namespace Mindweave.Tests;

public sealed class ContextPlannerTests
{
    private static readonly Guid Target = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Ancestor = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly AiConfiguration Config = new(Guid.NewGuid(), Guid.NewGuid(), "test", "https://invalid.test", "test-reference", "test-model", 6000, 1024);
    private static ChatMessage User(string text, Guid? conversation = null) => new(Guid.NewGuid(), conversation ?? Target, 1, MessageRole.User, text, GenerationStatus.Completed, 1, null, null);
    private static ChatMessage Answer(ChatMessage user, string text, GenerationStatus status = GenerationStatus.Completed) => new(Guid.NewGuid(), user.ConversationId, 2, MessageRole.Assistant, text, status, 2, user.Id, null);
    private static ChatMessage[] History()
    {
        var a = User(new string('a', 6500), Ancestor);
        var sourceUser = User("source question", Guid.NewGuid());
        var source = Answer(sourceUser, "source answer old attempt");
        var recentUser = User("recent question");
        return [a, sourceUser, source, recentUser, Answer(recentUser, "partial answer", GenerationStatus.Stopped), User("current question")];
    }

    [Fact]
    public void Fits_keeps_exact_original_order_and_raw_quote_wrapper()
    {
        var user = User("中文😀");
        var messages = new[] { user, Answer(user, "answer"), User("next") };
        var plan = ReplyContextPlanner.Plan(Target, new(messages, "<quote>"), Config);
        Assert.All(plan.Parts, p => Assert.IsType<VerbatimContextPart>(p));
        Assert.Equal(messages.Select(m => m.Id), plan.OriginalMessageIds);
        Assert.Equal(ChatRequestBudget.Inputs(messages, "<quote>"), ReplyContextPlanner.ComposeInputs(plan, messages, new Dictionary<string, ContextSummaryContent>(), Config));
    }

    [Fact]
    public void Over_budget_keeps_source_pair_and_recent_terminal_pair_and_covers_older_text()
    {
        var messages = History();
        var plan = ReplyContextPlanner.Plan(Target, new(messages, "answer", messages[2].Id), Config);
        Assert.Equal(messages.Skip(1).Select(m => m.Id), plan.Parts.OfType<VerbatimContextPart>().Select(p => p.MessageId));
        var chunks = plan.Parts.OfType<SummaryContextPart>().Select(p => p.Chunk).ToArray();
        Assert.True(chunks.Length >= 2);
        var slices = chunks.SelectMany(c => c.Slices).ToArray();
        Assert.All(slices, s => Assert.Equal(messages[0].Id, s.MessageId));
        Assert.Equal(messages[0].Body, string.Concat(slices.Select(s => messages[0].Body.Substring(s.Start, s.Length))));
        Assert.Equal(0, slices[0].Start);
        for (var i = 1; i < slices.Length; i++) Assert.Equal(slices[i - 1].Start + slices[i - 1].Length, slices[i].Start);
        foreach (var chunk in chunks)
        {
            var request = ReplyContextPlanner.CreateSummaryRequest(Target, chunk, messages, Config);
            Assert.True(ChatRequestBudget.Fits(request.Configuration, request.Messages));
            Assert.Equal(1024, request.Configuration.MaxOutputTokens);
            Assert.DoesNotContain("partial answer", string.Join("\n", request.Messages.Select(m => m.Content)));
        }
        var summaries = chunks.ToDictionary(c => c.CacheKey, _ => new ContextSummaryContent("old background", "", "", "", ""));
        var inputs = ReplyContextPlanner.ComposeInputs(plan, messages, summaries, Config);
        Assert.Contains(inputs, i => i.Content.Contains("old background"));
        Assert.Contains(inputs, i => i.Content == "partial answer");
        Assert.True(ChatRequestBudget.Fits(Config, inputs));
    }

    [Fact]
    public void Many_small_turns_pack_contiguous_older_messages_instead_of_rejecting_wrapper_overhead()
    {
        var messages = Enumerable.Range(0, 90).SelectMany(_ =>
        {
            var user = User("old question");
            return new[] { user, Answer(user, "old answer") };
        }).Append(User("now")).ToArray();
        var plan = ReplyContextPlanner.Plan(Target, new(messages, null), Config);
        Assert.Contains(plan.Parts.OfType<SummaryContextPart>(), p => p.Chunk.Slices.Count > 1);
        var summaries = plan.Parts.OfType<SummaryContextPart>().ToDictionary(p => p.Chunk.CacheKey, _ => new ContextSummaryContent("background", "", "", "", ""));
        Assert.True(ChatRequestBudget.Fits(Config, ReplyContextPlanner.ComposeInputs(plan, messages, summaries, Config)));
        Assert.Equal(messages.TakeLast(3).Select(m => m.Id), plan.Parts.OfType<VerbatimContextPart>().TakeLast(3).Select(p => p.MessageId));
    }

    [Fact]
    public void Emoji_chinese_and_empty_sources_have_complete_ordered_scalar_safe_coverage()
    {
        var messages = new[] { User("", Ancestor), User(string.Concat(Enumerable.Repeat("中😀", 500)), Ancestor), User("now") };
        var plan = ReplyContextPlanner.Plan(Target, new(messages, null), Config with { ContextTokenBudget = 3500, MaxOutputTokens = 128 });
        var slices = plan.Parts.OfType<SummaryContextPart>().SelectMany(p => p.Chunk.Slices).ToArray();
        Assert.Contains(slices, slice => slice.MessageId == messages[0].Id && slice.Length == 0);
        var bodySlices = slices.Where(slice => slice.MessageId == messages[1].Id).ToArray();
        Assert.Equal(messages[1].Body, string.Concat(bodySlices.Select(slice => messages[1].Body.Substring(slice.Start, slice.Length))));
        Assert.All(bodySlices, slice => Assert.False(slice.Start > 0 && char.IsLowSurrogate(messages[1].Body[slice.Start])));
        foreach (var part in plan.Parts.OfType<SummaryContextPart>())
        {
            var request = ReplyContextPlanner.CreateSummaryRequest(Target, part.Chunk, messages, Config with { ContextTokenBudget = 3500, MaxOutputTokens = 128 });
            Assert.Equal(128, request.Configuration.MaxOutputTokens);
            Assert.True(ChatRequestBudget.Fits(request.Configuration, request.Messages));
        }
    }

    [Fact]
    public void Cached_full_prefix_survives_append_but_material_configuration_target_and_order_changes_invalidate()
    {
        var messages = History();
        var context = new ChatContext(messages, null, messages[2].Id);
        var plan = ReplyContextPlanner.Plan(Target, context, Config);
        var first = plan.Parts.OfType<SummaryContextPart>().First().Chunk;
        Assert.Equal(first.CacheKey, ReplyContextPlanner.Plan(Target, context, Config).Parts.OfType<SummaryContextPart>().First().Chunk.CacheKey);
        var appended = messages.Concat(new[] { Answer(messages[^1], "new answer"), User("new question") }).ToArray();
        Assert.Equal(first.CacheKey, ReplyContextPlanner.Plan(Target, context with { Messages = appended }, Config).Parts.OfType<SummaryContextPart>().First().Chunk.CacheKey);
        var changed = messages.ToArray();
        changed[0] = changed[0] with { Revision = 8 };
        Assert.NotEqual(first.CacheKey, ReplyContextPlanner.Plan(Target, context with { Messages = changed }, Config).Parts.OfType<SummaryContextPart>().First().Chunk.CacheKey);
        changed[0] = messages[0] with { Body = "z" + messages[0].Body[1..] };
        Assert.NotEqual(first.CacheKey, ReplyContextPlanner.Plan(Target, context with { Messages = changed }, Config).Parts.OfType<SummaryContextPart>().First().Chunk.CacheKey);
        AiConfiguration[] configs = [Config with { ConnectionId = Guid.NewGuid() }, Config with { ModelConfigId = Guid.NewGuid() },
            Config with { Name = "renamed" }, Config with { BaseUrl = "https://other.invalid" }, Config with { CredentialReference = "other" },
            Config with { ModelId = "other" }, Config with { ContextTokenBudget = 6100 }, Config with { MaxOutputTokens = 1000 }, Config with { TokenLimitParameter = "max_completion_tokens" }];
        Assert.All(configs, config => Assert.NotEqual(first.CacheKey, ReplyContextPlanner.Plan(Target, context, config).Parts.OfType<SummaryContextPart>().First().Chunk.CacheKey));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.CreateSummaryRequest(Guid.NewGuid(), first, messages, Config));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.CreateSummaryRequest(Target, first, changed, Config));
        var summaries = plan.Parts.OfType<SummaryContextPart>().ToDictionary(p => p.Chunk.CacheKey, _ => new ContextSummaryContent("background", "", "", "", ""));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan with { EstimatorProfile = "future" }, messages, summaries, Config));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan with { FormatVersion = 2 }, messages, summaries, Config));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan with { Parts = plan.Parts.Reverse().ToArray() }, messages, summaries, Config));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan with { Parts = plan.Parts.Skip(1).ToArray() }, messages, summaries, Config));
    }

    [Fact]
    public void Mandatory_current_or_source_oversize_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.Plan(Target, new([User(new string('x', 9000))], null), Config));
        var user = User("origin", Ancestor);
        var source = Answer(user, new string('x', 9000));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.Plan(Target, new([user, source, User("now")], "x", source.Id), Config));
    }

    [Fact]
    public void Quote_without_direct_identity_and_impossible_minimum_requests_refuse_before_any_request()
    {
        var messages = History();
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.Plan(Target, new(messages, "source"), Config));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.Plan(Target, new([User(new string('a', 2000), Ancestor), User("now")], null),
            Config with { ContextTokenBudget = 900, MaxOutputTokens = 64 }));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.Plan(Target, new(messages, null, Guid.NewGuid()), Config));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.Plan(Target, new([User("now")], null), Config with { MaxOutputTokens = 0 }));
    }

    [Theory]
    [InlineData(GenerationStatus.Completed)]
    [InlineData(GenerationStatus.Stopped)]
    [InlineData(GenerationStatus.Failed)]
    [InlineData(GenerationStatus.Interrupted)]
    public void Latest_own_terminal_exchange_is_kept_and_older_partial_state_is_explicit_in_summary_request(GenerationStatus status)
    {
        var oldUser = User(new string('a', 7000));
        var oldAnswer = Answer(oldUser, "older partial", status);
        var recent = User("recent");
        var messages = new[] { oldUser, oldAnswer, recent, Answer(recent, "latest", status), User("now") };
        var plan = ReplyContextPlanner.Plan(Target, new(messages, null), Config);
        Assert.Equal(messages.TakeLast(3).Select(m => m.Id), plan.Parts.OfType<VerbatimContextPart>().Select(p => p.MessageId));
        var chunk = plan.Parts.OfType<SummaryContextPart>().Single(p => p.Chunk.Slices.Any(s => s.MessageId == oldAnswer.Id)).Chunk;
        var request = ReplyContextPlanner.CreateSummaryRequest(Target, chunk, messages, Config);
        Assert.Contains("\"Status\":\"" + status + "\"", request.Messages[1].Content);
    }

    [Fact]
    public void Request_rejects_surrogate_cut_and_changed_slice_identity()
    {
        var messages = new[] { User(string.Concat(Enumerable.Repeat("😀", 2000)), Ancestor), User("now") };
        var plan = ReplyContextPlanner.Plan(Target, new(messages, null), Config);
        var chunk = plan.Parts.OfType<SummaryContextPart>().First().Chunk;
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.CreateSummaryRequest(Target,
            chunk with { Slices = [chunk.Slices[0] with { Start = 1 }] }, messages, Config));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.CreateSummaryRequest(Target,
            chunk with { Slices = [chunk.Slices[0] with { Length = chunk.Slices[0].Length - 2 }] }, messages, Config));
    }

    [Fact]
    public void Forged_plan_cannot_summarize_current_question_or_direct_source()
    {
        var messages = History();
        var plan = ReplyContextPlanner.Plan(Target, new(messages, null, messages[2].Id), Config);
        var summaries = plan.Parts.OfType<SummaryContextPart>().ToDictionary(p => p.Chunk.CacheKey, _ => new ContextSummaryContent("background", "", "", "", ""));
        // Reassigning the direct source to a compressed ancestor must fail even though
        // the old chunk remains byte-identical and its cache is valid.
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan with { DirectSourceMessageId = messages[0].Id }, messages, summaries, Config));
    }

    [Fact]
    public void Estimate_counts_utf8_overhead_output_and_integer_boundary_without_overflow()
    {
        ChatInput[] input = [new("user", "中😀")];
        Assert.Equal(1319L, ChatRequestBudget.Estimate(Config, input));
        Assert.True(ChatRequestBudget.Fits(Config with { ContextTokenBudget = 1319 }, input));
        Assert.False(ChatRequestBudget.Fits(Config with { ContextTokenBudget = 1318 }, input));
        Assert.False(ChatRequestBudget.Fits(Config with { MaxOutputTokens = int.MaxValue, ContextTokenBudget = int.MaxValue }, []));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Background\":\"x\",\"Requirements\":\"\",\"Corrections\":\"\",\"OpenQuestions\":\"\",\"References\":\"\",\"extra\":0}")]
    [InlineData("{\"Background\":\"x\",\"Background\":\"y\",\"Requirements\":\"\",\"Corrections\":\"\",\"OpenQuestions\":\"\",\"References\":\"\"}")]
    [InlineData("{\"Background\":3,\"Requirements\":\"\",\"Corrections\":\"\",\"OpenQuestions\":\"\",\"References\":\"\"}")]
    [InlineData("{\"Background\":\" \",\"Requirements\":\"\",\"Corrections\":\"\",\"OpenQuestions\":\"\",\"References\":\"\"}")]
    [InlineData("{\"Background\":")]
    public void Invalid_summary_is_rejected(string json) => Assert.Throws<InvalidOperationException>(() => ContextSummaryProtocol.Parse(json));

    [Fact]
    public void Summary_roundtrip_size_limit_and_final_budget_are_enforced()
    {
        var summary = new ContextSummaryContent("中文😀", "preserve requirement", "correction", "open", "reference");
        Assert.Equal(summary, ContextSummaryProtocol.Parse(ContextSummaryProtocol.Serialize(summary)));
        Assert.Throws<InvalidOperationException>(() => ContextSummaryProtocol.Serialize(summary with { Background = new string('中', 22000) }));
        var messages = History();
        var plan = ReplyContextPlanner.Plan(Target, new(messages, null, messages[2].Id), Config);
        var summaries = plan.Parts.OfType<SummaryContextPart>().ToDictionary(p => p.Chunk.CacheKey, _ => summary with { Background = new string('x', 6500) });
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan, messages, summaries, Config));
        Assert.Throws<InvalidOperationException>(() => ReplyContextPlanner.ComposeInputs(plan, messages, new Dictionary<string, ContextSummaryContent>(), Config));
    }
}
