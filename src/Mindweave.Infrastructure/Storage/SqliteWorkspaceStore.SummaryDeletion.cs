using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    public Task<SummaryOutcome<SummaryDeletionPreview>> PreviewDeleteSummaryVersionsAsync(Guid nodeId, ImmutableArray<Guid> versionIds) => ReadSummaryAsync((c, t) =>
    {
        var state = ReadSummaryState(c, t, nodeId);
        return state is null ? new SummaryOutcome<SummaryDeletionPreview>(SummaryOutcomeKind.NotFound, Code: "SummaryNodeMissing") : SummaryVersionPolicy.PreviewDelete(state, versionIds);
    });
    public Task<SummaryOutcome<SummaryDeletionPreview>> DeleteSummaryVersionsAsync(SummaryDeletionPreview confirmedPreview) => WriteAsync<SummaryOutcome<SummaryDeletionPreview>>((c, t) =>
    {
        var state = ReadSummaryState(c, t, confirmedPreview.Token.NodeId); if (state is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryNodeMissing");
        var decision = SummaryVersionPolicy.PreviewDelete(state, confirmedPreview.VersionIds);
        if (decision.Kind != SummaryOutcomeKind.Applied) return decision;
        var actual = decision.Value!;
        if (!SameSummaryDeletion(actual, confirmedPreview)) return new(SummaryOutcomeKind.Conflict, actual, "DeletionPreviewChanged");
        RepairSummarySelection(c, t, state.Node, actual.VersionIds, actual.DeletesNode ? null : actual.FallbackVersionId);
        if (actual.DeletesNode)
        {
            using var draft = ChatCommand(c, t, "DELETE FROM summary_edit_draft WHERE node_id=$id", ("$id", state.Node.Id)); draft.ExecuteNonQuery();
        }
        else if (actual.Impact.ChangesCurrent) ChangeSummaryCurrent(c, t, state, actual.FallbackVersionId!.Value, null);
        else
        {
            using var q = ChatCommand(c, t, "UPDATE summary_node SET revision=$revision WHERE id=$id", ("$revision", checked(state.Node.Revision + 1)), ("$id", state.Node.Id)); q.ExecuteNonQuery();
        }
        DeleteSummaryBodies(c, t, actual.VersionIds);
        if (actual.DeletesNode)
        {
            using var q = ChatCommand(c, t, "DELETE FROM summary_node WHERE id=$id", ("$id", state.Node.Id)); q.ExecuteNonQuery();
        }
        PruneSummaryIdentities(c, t, state.Node.Id);
        return new(SummaryOutcomeKind.Applied, actual);
    });
    private static bool SameSummaryDeletion(SummaryDeletionPreview a, SummaryDeletionPreview b) =>
        a.Token == b.Token && !b.VersionIds.IsDefault && a.VersionIds.SequenceEqual(b.VersionIds) && a.FallbackVersionId == b.FallbackVersionId && a.DeletesNode == b.DeletesNode &&
        a.Impact.ChangesCurrent == b.Impact.ChangesCurrent && a.Impact.AffectsCandidate == b.Impact.AffectsCandidate && a.Impact.AffectsDirtyDraft == b.Impact.AffectsDirtyDraft &&
        !b.Impact.AffectedActiveTaskIds.IsDefault && a.Impact.AffectedActiveTaskIds.SequenceEqual(b.Impact.AffectedActiveTaskIds);
    private static void DeleteSummaryBodies(SqliteConnection c, SqliteTransaction t, ImmutableArray<Guid> ids)
    {
        foreach (var id in ids)
        {
            // Naming metadata is subordinate to user deletion. A late Task 6 completion must observe terminal/stale state.
            using (var tasks = ChatCommand(c, t, """
                SELECT j.id,j.revision FROM node_naming_job j JOIN node_naming_member m ON m.task_id=j.id
                WHERE m.current_version_id=$id AND j.status IN ('Queued','Running')
                """, ("$id", id)))
            {
                using var r = tasks.ExecuteReader(); var active = new List<(Guid Id, long Revision)>();
                while (r.Read()) active.Add((Guid.Parse(r.GetString(0)), r.GetInt64(1))); r.Close();
                foreach (var job in active)
                {
                    using var stop = ChatCommand(c, t, "UPDATE node_naming_job SET status='Stopped',revision=$revision,error_code='SummaryVersionDeleted' WHERE id=$id",
                        ("$revision", checked(job.Revision + 1)), ("$id", job.Id)); stop.ExecuteNonQuery();
                }
            }
            using var q = ChatCommand(c, t, "DELETE FROM summary_version WHERE id=$id", ("$id", id)); q.ExecuteNonQuery();
        }
    }
    private static void PruneSummaryIdentities(SqliteConnection c, SqliteTransaction t, Guid nodeId)
    {
        using var q = ChatCommand(c, t, """
            DELETE FROM summary_version_identity AS i WHERE i.node_id=$node
                AND NOT EXISTS(SELECT 1 FROM summary_version v WHERE v.id=i.id OR v.baseline_version_id=i.id)
                AND NOT EXISTS(SELECT 1 FROM knowledge_summary_job j WHERE j.baseline_version_id=i.id OR j.output_version_id=i.id)
                AND NOT EXISTS(SELECT 1 FROM summary_edit_draft d WHERE d.base_version_id=i.id)
                AND NOT EXISTS(SELECT 1 FROM node_naming_member m WHERE m.current_version_id=i.id)
            """, ("$node", nodeId)); q.ExecuteNonQuery();
    }
}
