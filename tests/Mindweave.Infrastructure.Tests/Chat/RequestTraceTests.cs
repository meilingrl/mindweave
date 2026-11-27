using Mindweave.Application.Diagnostics;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

public class RequestTraceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_completed_answer_records_one_request_without_content()
    {
        await using var h = await ChatHarness.StartAsync();

        var sent = await h.Conversations.StartAsync(h.Workspace, "火车", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);

        var record = Assert.Single(await RequestsForAsync(h, sent.Answer!.Value));
        Assert.Equal(AiRequestOutcome.Completed, record.Outcome);
        Assert.Equal("答：火车".Length, record.OutputCharacters);
        Assert.NotNull(record.TimeToFirstText);
        Assert.True(record.FinishedAt >= record.StartedAt);
        Assert.Null(record.ErrorKind);
        Assert.Null(record.ErrorDetail);
    }

    [Fact]
    public async Task A_failed_request_keeps_its_classification_and_a_scrubbed_detail()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.Fail(GenerationErrorKind.Unauthorized, "Incorrect API key provided: sk-live-AbCdEf123456", "半");

        var sent = await h.Conversations.StartAsync(h.Workspace, "q", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);

        var record = Assert.Single(await RequestsForAsync(h, sent.Answer!.Value));
        Assert.Equal(AiRequestOutcome.Failed, record.Outcome);
        Assert.Equal(GenerationErrorKind.Unauthorized, record.ErrorKind);
        Assert.Equal(1, record.OutputCharacters);
        Assert.DoesNotContain("sk-live", record.ErrorDetail, StringComparison.Ordinal);
        Assert.Contains(Redaction.Mask, record.ErrorDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stopped_answer_is_recorded_as_stopped()
    {
        await using var h = await ChatHarness.StartAsync();
        var held = h.Client.Hold("部分");
        var sent = await h.Conversations.StartAsync(h.Workspace, "q", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await h.Conversations.StopAsync(sent.Answer!.Value);
        await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(AiRequestOutcome.Stopped, Assert.Single(await RequestsForAsync(h, sent.Answer!.Value)).Outcome);
    }

    /// <summary>
    /// Prompt caching, step 1 (requirements §10.11.4): what the provider read from its cache and wrote to it is stored with the
    /// request and read back; a provider that reports neither leaves both null, not zero.
    /// </summary>
    [Fact]
    public async Task Cached_input_the_provider_reports_is_recorded_and_its_absence_stays_null()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.PromptTokens = _ => 2000;
        h.Client.CompletionTokens = _ => 40;
        h.Client.CacheReadTokens = _ => 1536;
        h.Client.CacheWriteTokens = _ => 0;

        var cached = await h.Conversations.StartAsync(h.Workspace, "火车", Ct);
        await h.WaitForAnswerAsync(cached.Answer!.Value);
        var record = Assert.Single(await RequestsForAsync(h, cached.Answer!.Value));
        Assert.Equal((2000, 1536, 0), (record.PromptTokens, record.CacheReadTokens, record.CacheWriteTokens));

        h.Client.CacheReadTokens = null;
        h.Client.CacheWriteTokens = null;
        var silent = await h.Conversations.StartAsync(h.Workspace, "轮船", Ct);
        await h.WaitForAnswerAsync(silent.Answer!.Value);
        record = Assert.Single(await RequestsForAsync(h, silent.Answer!.Value));
        Assert.Equal((2000, (int?)null, (int?)null), (record.PromptTokens, record.CacheReadTokens, record.CacheWriteTokens));
    }

    /// <summary>The trace is written after the answer's terminal state, so wait for it.</summary>
    private static async Task<IReadOnlyList<AiRequestRecord>> RequestsForAsync(ChatHarness h, MessageId answer)
    {
        var task = await h.Database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null, "SELECT id FROM generation_task WHERE target_message_id = $id;", ("$id", answer.ToString()));
                return GenerationTaskId.Parse((string)cmd.ExecuteScalar()!);
            },
            Ct);
        IReadOnlyList<AiRequestRecord> records = [];
        await ChatHarness.UntilAsync(async () => (records = await h.Requests.ForTaskAsync(task, Ct)).Count > 0);
        return records;
    }
}
