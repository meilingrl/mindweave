using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore : IKnowledgeSummaryStore
{
    public Task<SummaryOutcome<KnowledgeSummaryJob>> AcceptKnowledgeSummaryAsync(Guid operationId, Guid conversationId, AiConfiguration configuration) =>
        WriteAsync<SummaryOutcome<KnowledgeSummaryJob>>((c, t) =>
        {
            if (operationId == Guid.Empty || conversationId == Guid.Empty || configuration is null) return new(SummaryOutcomeKind.Rejected, Code: "InvalidSummaryIdentityOrConfiguration");
            // Replays resolve before examining changed source rows, active tasks, or candidates.
            var existing = ReadSummaryJob(c, t, operationId, operation: true);
            if (existing is not null) return existing.ConversationId == conversationId && existing.InputPlan.Configuration == configuration
                ? new(SummaryOutcomeKind.NoChange, existing) : new(SummaryOutcomeKind.Conflict, Code: "SummaryOperationReused");
            try { ValidateConfiguration(configuration); }
            catch (ArgumentException) { return new(SummaryOutcomeKind.Rejected, Code: "InvalidSummaryConfiguration"); }
            var conversation = ReadConversation(c, t, conversationId);
            if (conversation is null) return new(SummaryOutcomeKind.NotFound, Code: "ConversationMissing");
            using var busy = ChatCommand(c, t, """
                SELECT EXISTS(SELECT 1 FROM chat_message WHERE conversation_id=$id AND status IN ('Queued','Running'))
                    OR EXISTS(SELECT 1 FROM generation_task WHERE conversation_id=$id AND kind='Reply' AND status IN ('Queued','Running')),
                    EXISTS(SELECT 1 FROM knowledge_summary_job WHERE conversation_id=$id AND status IN ('Queued','Running'))
                """, ("$id", conversationId));
            using var b = busy.ExecuteReader(); b.Read(); var reply = b.GetBoolean(0); var summary = b.GetBoolean(1); b.Close();
            var state = ReadSummaryState(c, t, conversationId, byConversation: true);
            var admission = SummaryVersionPolicy.Admit(state, state?.Node.Id ?? Guid.NewGuid(), reply, summary);
            if (admission.Kind != SummaryOutcomeKind.Applied) return new(admission.Kind, Code: admission.Code);
            KnowledgeSummaryInputPlan plan;
            KnowledgeSummaryMaterial material;
            try { material = ReadSummaryMaterial(c, t, conversationId); plan = KnowledgeSummaryInputPlanner.Plan(material, configuration); }
            catch (KnowledgeSummaryInputException e) { return new(SummaryOutcomeKind.Rejected, Code: e.Code); }
            var job = new KnowledgeSummaryJob(Guid.NewGuid(), operationId, conversation.WorkspaceId, conversationId, admission.Value!, GenerationStatus.Queued, 0, plan, DateTimeOffset.UtcNow, null, null, null);
            using var insert = ChatCommand(c, t, """
                INSERT INTO knowledge_summary_job(id,operation_id,workspace_id,conversation_id,planned_node_id,baseline_version_id,baseline_node_revision,creates_node,status,revision,input_plan_json,accepted_at)
                VALUES($id,$op,$workspace,$conversation,$node,$baseline,$revision,$first,'Queued',0,$plan,$now)
                """, ("$id", job.Id), ("$op", operationId), ("$workspace", job.WorkspaceId), ("$conversation", conversationId), ("$node", job.Baseline.NodeId),
                ("$baseline", job.Baseline.CurrentVersionId), ("$revision", job.Baseline.NodeRevision), ("$first", job.Baseline.CreatesNode),
                ("$plan", JsonSerializer.Serialize(plan)), ("$now", job.AcceptedAt.ToString("O", CultureInfo.InvariantCulture)));
            insert.ExecuteNonQuery();
            InsertSummarySources(c, t, job.Id, material);
            return new(SummaryOutcomeKind.Applied, job);
        });

    public Task<KnowledgeSummaryJob?> LoadKnowledgeSummaryJobAsync(Guid taskId) => ReadAsync(c => ReadSummaryJob(c, null, taskId));
    public Task<KnowledgeSummaryJob?> FindKnowledgeSummaryOperationAsync(Guid operationId) => ReadAsync(c => ReadSummaryJob(c, null, operationId, operation: true));
    public Task<ImmutableArray<KnowledgeSummaryJob>> ListActiveKnowledgeSummaryJobsAsync() => ReadAsync(c => ReadActiveSummaryJobs(c, null));

    public Task<SummaryOutcome<KnowledgeSummaryJob>> UpdateKnowledgeSummaryMetadataAsync(SummaryGenerationMetadata update) =>
        WriteAsync<SummaryOutcome<KnowledgeSummaryJob>>((c, t) =>
        {
            try { ValidateGenerationMetadata(update.ErrorCode, update.ActualModelId); }
            catch (ArgumentException) { return new(SummaryOutcomeKind.Rejected, Code: "InvalidSummaryMetadata"); }
            if (!Enum.IsDefined(update.Status) || update.Status == GenerationStatus.Completed) return new(SummaryOutcomeKind.Rejected, Code: "SummaryMetadataCannotComplete");
            var job = ReadSummaryJob(c, t, update.TaskId);
            if (job is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryTaskMissing");
            if (!IsActive(job.Status) || job.Revision != update.ExpectedRevision) return new(SummaryOutcomeKind.Conflict, Code: "SummaryTaskChanged");
            if (job.Revision == long.MaxValue || (job.Status == GenerationStatus.Running && update.Status == GenerationStatus.Queued)) return new(SummaryOutcomeKind.Rejected, Code: "InvalidSummaryTransition");
            var revision = checked(job.Revision + 1);
            using var q = ChatCommand(c, t, "UPDATE knowledge_summary_job SET status=$status,revision=$revision,error_code=$error,actual_model_id=$model WHERE id=$id",
                ("$status", update.Status.ToString()), ("$revision", revision), ("$error", update.ErrorCode), ("$model", update.ActualModelId), ("$id", job.Id));
            q.ExecuteNonQuery(); return new(SummaryOutcomeKind.Applied, job with { Status = update.Status, Revision = revision, ErrorCode = update.ErrorCode, ActualModelId = update.ActualModelId });
        });

    public Task RecoverInterruptedKnowledgeSummariesAsync() => WriteAsync((c, t) =>
    {
        foreach (var job in ReadActiveSummaryJobs(c, t))
        {
            using var q = ChatCommand(c, t, "UPDATE knowledge_summary_job SET status='Interrupted',revision=$revision,error_code='Interrupted' WHERE id=$id", ("$revision", checked(job.Revision + 1)), ("$id", job.Id));
            q.ExecuteNonQuery();
        }
        return true;
    });
}
