using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    private static string SummaryContentHash(SummaryContent content) => KnowledgeSummaryInputPlanner.Hash(KnowledgeSummaryProtocol.Serialize(content));
    public Task<SummaryOutcome<SummaryGenerationResult>> CompleteKnowledgeSummaryAsync(SummaryGenerationCompletion completion) =>
        WriteAsync<SummaryOutcome<SummaryGenerationResult>>((c, t) =>
        {
            try { ValidateGenerationMetadata(null, completion.ActualModelId); }
            catch (ArgumentException) { return new(SummaryOutcomeKind.Rejected, Code: "InvalidSummaryMetadata"); }
            if (completion.Content is null) return new(SummaryOutcomeKind.Rejected, Code: "InvalidSummaryContent");
            SummaryContent content;
            try { content = KnowledgeSummaryProtocol.ParseComplete(KnowledgeSummaryProtocol.Serialize(completion.Content)); }
            catch (SummaryProtocolException) { return new(SummaryOutcomeKind.Rejected, Code: "InvalidSummaryContent"); }
            var hash = SummaryContentHash(content);
            var job = ReadSummaryJob(c, t, completion.TaskId);
            if (job is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryTaskMissing");
            if (job.Status == GenerationStatus.Completed)
            {
                using var q = ChatCommand(c, t, "SELECT output_content_hash,output_is_candidate FROM knowledge_summary_job WHERE id=$id", ("$id", job.Id));
                using var r = q.ExecuteReader(); r.Read();
                if (hash != r.GetString(0) || job.ActualModelId != completion.ActualModelId || completion.ExpectedRevision != job.Revision - 1)
                    return new(SummaryOutcomeKind.Conflict, Code: "SummaryCompletionChanged");
                // Intentionally no live node/body lookup: losing a commit response must not resurrect deleted output.
                return new(SummaryOutcomeKind.NoChange, new(job.Id, job.Baseline.NodeId, job.OutputVersionId!.Value, r.GetBoolean(1)));
            }
            if (!IsActive(job.Status) || job.Revision != completion.ExpectedRevision) return new(SummaryOutcomeKind.Conflict, Code: "SummaryTaskChanged");
            if (job.Revision == long.MaxValue) return new(SummaryOutcomeKind.Rejected, Code: "SummaryRevisionExhausted");
            var state = ReadSummaryState(c, t, job.ConversationId, byConversation: true);
            var versionId = Guid.NewGuid(); var decision = SummaryVersionPolicy.Complete(state, job.Baseline, versionId);
            if (decision.Kind != SummaryOutcomeKind.Applied) return new(decision.Kind, Code: decision.Code);
            if (state is not null && (state.Node.WorkspaceId != job.WorkspaceId || state.Node.ConversationId != job.ConversationId))
                return new(SummaryOutcomeKind.Rejected, Code: "SummaryTargetOwnershipMismatch");
            if (ReadPreparedSummary(c, t, job.Id) is null) return new(SummaryOutcomeKind.Blocked, Code: "SummaryRequestNotPrepared");
            var nodeRevision = checked((state?.Node.Revision ?? 0) + 1);
            var ordinal = NextSummaryOrdinal(c, t, job.Baseline.NodeId);
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            using (var identity = ChatCommand(c, t, "INSERT INTO summary_version_identity VALUES($id,$node,$ordinal,'Generated',$now)", ("$id", versionId), ("$node", job.Baseline.NodeId), ("$ordinal", ordinal), ("$now", now))) identity.ExecuteNonQuery();
            using (var version = ChatCommand(c, t, "INSERT INTO summary_version VALUES($id,$node,$title,$body,$baseline,$requested,$actual,$hash)", ("$id", versionId), ("$node", job.Baseline.NodeId),
                ("$title", content.Title), ("$body", content.Markdown), ("$baseline", job.Baseline.CurrentVersionId), ("$requested", job.InputPlan.Configuration.ModelId), ("$actual", completion.ActualModelId), ("$hash", hash))) version.ExecuteNonQuery();
            using (var source = ChatCommand(c, t, "INSERT INTO summary_version_source VALUES($id,$coverage)", ("$id", versionId), ("$coverage", JsonSerializer.Serialize(job.InputPlan.Coverage)))) source.ExecuteNonQuery();
            foreach (var sourceRef in job.InputPlan.Coverage.OwnMessages.Concat(job.InputPlan.Coverage.FrozenAncestors))
            {
                using var sourceMessage = ChatCommand(c, t, "INSERT INTO summary_version_message VALUES($id,$scope,$ordinal,$message,$conversation)", ("$id", versionId),
                    ("$scope", sourceRef.Scope.ToString()), ("$ordinal", sourceRef.Ordinal), ("$message", sourceRef.MessageId), ("$conversation", sourceRef.ConversationId));
                sourceMessage.ExecuteNonQuery();
            }
            using (var read = ChatCommand(c, t, """
                INSERT INTO summary_read_state(version_id,epoch,revision,is_read,suppress_automatic,text_hash,text_length,seen_json,visible_dwell_ticks,observation_sequence,was_active_visible)
                VALUES($id,0,0,0,0,'',0,'[]',0,0,0)
                """, ("$id", versionId))) read.ExecuteNonQuery();
            if (job.Baseline.CreatesNode)
            {
                using var node = ChatCommand(c, t, """
                    INSERT INTO summary_node(id,workspace_id,conversation_id,current_version_id,candidate_version_id,last_version_ordinal,revision,base_title,display_name,group_key,name_is_manual,name_revision,created_at)
                    VALUES($id,$workspace,$conversation,$version,NULL,$ordinal,$revision,$title,$title,$key,0,0,$now)
                    """, ("$id", job.Baseline.NodeId), ("$workspace", job.WorkspaceId), ("$conversation", job.ConversationId), ("$version", versionId),
                    ("$revision", nodeRevision), ("$ordinal", ordinal), ("$title", content.Title), ("$key", SummaryTitleNormalization.GroupKey(content.Title)), ("$now", now)); node.ExecuteNonQuery();
            }
            else
            {
                using var node = ChatCommand(c, t, "UPDATE summary_node SET candidate_version_id=$version,revision=$revision WHERE id=$id", ("$version", versionId), ("$revision", nodeRevision), ("$id", job.Baseline.NodeId));
                if (node.ExecuteNonQuery() != 1) throw new InvalidDataException("Summary target vanished inside writer transaction.");
            }
            using var completed = ChatCommand(c, t, """
                UPDATE knowledge_summary_job SET status='Completed',revision=$revision,error_code=NULL,actual_model_id=$actual,
                    output_version_id=$version,output_is_candidate=$candidate,output_content_hash=$hash WHERE id=$id
                """, ("$revision", checked(job.Revision + 1)), ("$actual", completion.ActualModelId), ("$version", versionId), ("$candidate", !job.Baseline.CreatesNode), ("$hash", hash), ("$id", job.Id));
            completed.ExecuteNonQuery(); return new(SummaryOutcomeKind.Applied, new(job.Id, job.Baseline.NodeId, versionId, !job.Baseline.CreatesNode));
        });
}
