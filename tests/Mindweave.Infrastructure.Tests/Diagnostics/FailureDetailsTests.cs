using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.Diagnostics;

/// <summary>
/// What a failure's details are read from (D2 §2.1): an answer's last failed request, or what its task kept when the
/// request left no record; and the connection test's own details and trace.
/// </summary>
public class FailureDetailsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_failed_answer_has_the_status_request_id_model_and_duration_of_its_request()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.FailWithStatus(GenerationErrorKind.Unauthorized, 401, detail: "HTTP 401: bad key", requestId: "req_wrong_key");

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await ChatHarness.UntilAsync(() => h.Conversations.Observe(sent.Answer!.Value)?.Snapshot().State == MessageState.Failed);
        await h.Generation.SaveUnsavedAsync();

        FailureDetailsRead? details = null;
        await ChatHarness.UntilAsync(() =>
        {
            details = new(h.Failures.ForAnswerAsync(sent.Answer!.Value, Ct).GetAwaiter().GetResult());
            return details.Value.Details?.HttpStatus is not null;
        });
        var found = details!.Value.Details!;
        Assert.Equal((401, "req_wrong_key", "fake-model"), (found.HttpStatus, found.ProviderRequestId, found.Model));
        Assert.Equal("HTTP 401: bad key", found.ServiceText);
        Assert.NotNull(found.Duration);
        Assert.NotNull(found.At);
    }

    [Fact]
    public async Task An_answer_whose_request_left_no_record_still_names_its_model()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.Fail(GenerationErrorKind.Network, "connection reset");
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await ChatHarness.UntilAsync(() => h.Conversations.Observe(sent.Answer!.Value)?.Snapshot().State == MessageState.Failed);
        await h.Database.WriteAsync((c, t) => c.Execute(t, "DELETE FROM ai_request;"), Ct);

        var found = await h.Failures.ForAnswerAsync(sent.Answer!.Value, Ct);

        Assert.NotNull(found);
        Assert.Equal("fake-model", found.Model);
        Assert.Null(found.HttpStatus);
        Assert.Null(await h.Failures.ForAnswerAsync(new MessageId(Guid.NewGuid()), Ct));
    }

    [Fact]
    public async Task A_failed_connection_test_has_its_details_and_its_typed_key_reaches_the_log_scrub()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        h.Client.FailWithStatus(GenerationErrorKind.Unauthorized, 401, detail: "HTTP 401: no such key", requestId: "req_test_1");

        var result = await h.Models.TestAsync("https://relay.example/v1", " some-model ", "typed-plain-key-123", Ct);

        Assert.False(result.Succeeded);
        var details = result.Details!;
        Assert.Equal((401, "req_test_1", "some-model", "HTTP 401: no such key"), (details.HttpStatus, details.ProviderRequestId, details.Model, details.ServiceText));
        Assert.Contains("typed-plain-key-123", h.Secrets.Values);
        Assert.Equal("x [redacted] y", h.Secrets.Scrub("x typed-plain-key-123 y"));
    }

    private readonly record struct FailureDetailsRead(Application.Diagnostics.FailureDetails? Details);
}
