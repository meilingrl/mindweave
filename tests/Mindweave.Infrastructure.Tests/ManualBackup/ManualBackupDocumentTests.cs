using System.Text.Json;
using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Application.ManualBackup;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Share;

namespace Mindweave.Infrastructure.Tests.ManualBackup;

/// <summary>The document of kind device, built from stored records (docs/design/sharing-backup.md §1): the one place that decides.</summary>
public sealed class ManualBackupDocumentTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_received_conversation_says_so_and_what_stays_home_is_not_in_the_document()
    {
        var workspace = new Workspace(WorkspaceId.New(), WorkspaceName.FromStored("Trains"), At);
        var conversation = new Conversation(ConversationId.New(), workspace.Id, "A title the user wrote", TitleSource.Manual, At, At) { IsImported = true };
        var question = new Message(MessageId.New(), conversation.Id, 0, MessageRole.User, [new TextBlock("A question?")], MessageState.Complete, 0, null, null, null, At, null);
        var failed = new Message(MessageId.New(), conversation.Id, 1, MessageRole.Assistant, [new TextBlock("")], MessageState.Failed, 1, null,
            GenerationErrorKind.Network, "PLANTEDDETAIL connecting to https://planted.example/v1", At, At.AddSeconds(2));
        var answer = new Message(MessageId.New(), conversation.Id, 2, MessageRole.Assistant, [new TextBlock("An answer.")], MessageState.Complete, 1, failed.Id, null, null, At, At.AddSeconds(5));
        var run = new RunRecord(answer.Id, [new RunStepRecord(GenerationTaskId.New(), 0, RunStepKind.Model, RunStepState.Completed, At, At.AddSeconds(1), AiRequestId: Guid.CreateVersion7(), ErrorDetail: "PLANTEDSTEPDETAIL") { Target = "PLANTEDHOST.example" }], null);

        var node = SummaryNodeId.New();
        SummaryVersion Version(int seq, string body) => new(SummaryVersionId.New(), node, seq, "Title " + seq, body, 2, SummaryOrigin.Generated, null, null, "summary-model", At.AddMinutes(seq));
        var (older, current, candidate) = (Version(1, "The older body"), Version(2, "The current body"), Version(3, "PLANTEDCANDIDATE body"));
        var summary = new SummaryDetail(
            new SummaryNode(node, workspace.Id, conversation.Id, current.Id, candidate.Id, "Base", "Trains", NameSource.Manual, At, At),
            [older, current, candidate],
            new Dictionary<SummaryVersionId, SummaryReadState> { [older.Id] = new(older.Id, ReadSource.Manual, 0.5, "PLANTEDLOCATOR") },
            new SummaryEditDraft(node, current.Id, "PLANTEDEDITDRAFT", "PLANTEDEDITDRAFT body", At),
            2,
            false);
        var contents = new ManualBackupContents(
            workspace,
            [new ManualBackupConversation(new ConversationSummary(conversation, "A question?", MessageState.Complete), [question, failed, answer], null, summary,
                new Dictionary<MessageId, RunRecord> { [answer.Id] = run }, new Dictionary<MessageId, string> { [answer.Id] = "answer-model" }, new ImportMark("The share's title", At))],
            [],
            new Dictionary<ResourceId, StoredResource>(),
            new Dictionary<ResourceId, StoredDerivative>());

        var document = ManualBackupDocument.Build(contents, At, "0.7.0");
        var json = SharePage.Data(document);

        Assert.DoesNotContain("PLANTED", json, StringComparison.Ordinal);
        Assert.DoesNotContain("planted.example", json, StringComparison.Ordinal);
        Assert.DoesNotContain(candidate.Id.ToString(), json, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Steps[0].AiRequestId!.Value.ToString(), json, StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(json);
        var stored = parsed.RootElement.GetProperty("conversations")[0];
        Assert.Equal("A title the user wrote", stored.GetProperty("name").GetString());
        Assert.True(stored.GetProperty("title_by_user").GetBoolean());
        Assert.Equal("The share's title", stored.GetProperty("received").GetProperty("title").GetString());
        var messages = stored.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal([0, 1, 2], messages.Select(m => m.GetProperty("seq").GetInt32()));
        Assert.Equal(("network", failed.Id.ToString(), "answer-model"), (messages[1].GetProperty("error").GetString(), messages[2].GetProperty("supersedes").GetString(), messages[2].GetProperty("model").GetString()));
        Assert.Equal("completed", messages[2].GetProperty("run").GetProperty("steps")[0].GetProperty("state").GetString());
        var history = stored.GetProperty("summary_history");
        Assert.Equal(("Trains", "manual", current.Id.ToString()), (history.GetProperty("name").GetString(), history.GetProperty("name_source").GetString(), history.GetProperty("current").GetString()));
        var versions = history.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(["The older body", "The current body"], versions.Select(v => v.GetProperty("body").GetString()));
        Assert.Equal("manual", versions[0].GetProperty("read").GetString());
        Assert.False(versions[1].TryGetProperty("read", out _));
        Assert.Equal("summary-model", versions[1].GetProperty("model").GetString());
    }

    /// <summary>Stored values are named in the format by their code names, so a renamed member would change the format: these are pinned.</summary>
    [Fact]
    public void Stored_values_have_fixed_names_in_the_format()
    {
        Assert.Equal("local_read", SharePage.Name(ToolRisk.LocalRead));
        Assert.Equal("awaiting_approval", SharePage.Name(RunStepState.AwaitingApproval));
        Assert.Equal("quota_or_rate_limited", SharePage.Name(GenerationErrorKind.QuotaOrRateLimited));
        Assert.Equal("resource_part", SharePage.Name(ToolFindingKind.ResourcePart));
        Assert.Equal("mcp", SharePage.Name(ResourceOrigin.Mcp));
        Assert.Equal("model_requests", SharePage.Name(RunLimit.ModelRequests));
    }

    /// <summary>Each kind of block has its own form in the format; a new kind must be given one before a backup can hold it.</summary>
    [Fact]
    public void Every_kind_of_block_has_a_form()
    {
        var kinds = typeof(ContentBlock).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(ContentBlock)) && !t.IsAbstract).Select(t => t.Name).Order();
        Assert.Equal(
            new[] { nameof(TextBlock), nameof(AttachmentBlock), nameof(AbsentAttachmentBlock), nameof(ToolCallBlock), nameof(ToolResultBlock), nameof(ActivityCountBlock), nameof(CitationBlock) }.Order(),
            kinds);
    }
}
