using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;
namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    public Task<SummaryOutcome<NodeNamingIntent>> SaveNodeNamingIntentAsync(NodeNamingIntent intent) => WriteAsync<SummaryOutcome<NodeNamingIntent>>((c, t) =>
    {
        if (intent.OperationId == Guid.Empty || intent.Snapshot.Members.IsDefaultOrEmpty) return new(SummaryOutcomeKind.Rejected, Code: "InvalidNamingIntent");
        try { ValidateConfiguration(intent.Configuration); } catch (ArgumentException) { return new(SummaryOutcomeKind.Rejected, Code: "InvalidNamingConfiguration"); }
        if (!SameNamingSnapshot(intent.Snapshot, ReadNamingGroup(c, t, intent.Snapshot.WorkspaceId, intent.Snapshot.GroupKey))) return new(SummaryOutcomeKind.Obsolete, Code: "NamingGroupChanged");
        // Writer order, not time stamps, determines latest. An exact operation is immutable.
        var prior = ReadNamingIntents(c, t).FirstOrDefault(i => i.OperationId == intent.OperationId);
        if (prior is not null) return SameNamingSnapshot(prior.Snapshot, intent.Snapshot) && prior.Configuration == intent.Configuration ? new(SummaryOutcomeKind.NoChange, prior) : new(SummaryOutcomeKind.Conflict, Code: "NamingIntentReused");
        using var q = ChatCommand(c, t, """
            UPDATE node_naming_group SET pending_operation_id=$op,pending_snapshot_json=$snapshot,pending_configuration_json=$config,pending_at=$now
            WHERE workspace_id=$w AND group_key=$g
            """, ("$op", intent.OperationId), ("$snapshot", JsonSerializer.Serialize(intent.Snapshot)), ("$config", JsonSerializer.Serialize(intent.Configuration)), ("$now", intent.RequestedAt.ToString("O")), ("$w", intent.Snapshot.WorkspaceId), ("$g", intent.Snapshot.GroupKey));
        q.ExecuteNonQuery(); return new(SummaryOutcomeKind.Applied, intent);
    });
    private static void ClearNamingIntent(SqliteConnection c, SqliteTransaction t, Guid workspace, string key, Guid operation)
    {
        using var q = ChatCommand(c, t, """
            UPDATE node_naming_group SET pending_operation_id=NULL,pending_snapshot_json=NULL,pending_configuration_json=NULL,pending_at=NULL
            WHERE workspace_id=$w AND group_key=$g AND pending_operation_id=$op
            """, ("$w", workspace), ("$g", key), ("$op", operation)); q.ExecuteNonQuery();
    }
    public Task ClearNodeNamingIntentAsync(Guid workspaceId, string groupKey, Guid operationId) => WriteAsync((c, t) => { ClearNamingIntent(c, t, workspaceId, groupKey, operationId); return true; });
    private static ImmutableArray<NodeNamingIntent> ReadNamingIntents(SqliteConnection c, SqliteTransaction t)
    {
        using var q = ChatCommand(c, t, "SELECT pending_operation_id,pending_snapshot_json,pending_configuration_json,pending_at,workspace_id,group_key FROM node_naming_group WHERE pending_operation_id IS NOT NULL ORDER BY workspace_id,group_key");
        using var r = q.ExecuteReader(); var intents = ImmutableArray.CreateBuilder<NodeNamingIntent>();
        while (r.Read())
        {
            var s = SummaryJson<NodeNamingSnapshot>(r.GetString(1));
            if (s.WorkspaceId != Guid.Parse(r.GetString(4)) || s.GroupKey != r.GetString(5) || s.SnapshotHash != NamingSnapshotHash(s)) throw new InvalidDataException("Naming intent mismatch.");
            intents.Add(new(Guid.Parse(r.GetString(0)), s, SummaryJson<AiConfiguration>(r.GetString(2)), ContextTime(r.GetString(3))));
        }
        return intents.ToImmutable();
    }
    public Task<ImmutableArray<NodeNamingIntent>> ListNodeNamingIntentsAsync() => ReadSummaryAsync(ReadNamingIntents);
    public Task<SummaryOutcome<PreparedNodeNamingRequest>> PrepareNodeNamingRequestAsync(Guid taskId, long expectedRevision) => WriteAsync<SummaryOutcome<PreparedNodeNamingRequest>>((c, t) =>
    {
        var job = ReadNamingJob(c, t, taskId); if (job is null) return new(SummaryOutcomeKind.NotFound, Code: "NamingTaskMissing");
        if (!IsActive(job.Status) || job.Revision != expectedRevision) return new(SummaryOutcomeKind.Obsolete, Code: "NamingTaskChanged");
        var materials = ReadNamingMaterials(c, t, job); if (materials.Kind != SummaryOutcomeKind.Applied) return new(materials.Kind, Code: materials.Code);
        if (!NodeNamingProtocol.NeedsNaming(materials.Value)) return new(SummaryOutcomeKind.Rejected, Code: "NamingUnnecessary");
        Mindweave.Application.Chat.ChatRequest request;
        try { request = NodeNamingProtocol.CreateRequest(job, materials.Value); } catch (SummaryProtocolException) { return new(SummaryOutcomeKind.Rejected, Code: "NamingMaterialOverBudget"); }
        var hash = NodeNamingProtocol.RequestHash(request);
        using var prior = ChatCommand(c, t, "SELECT prepared_format_version,protocol_version,request_hash FROM node_naming_job WHERE id=$id", ("$id", taskId)); using var r = prior.ExecuteReader(); r.Read();
        if (!r.IsDBNull(0))
        {
            if (r.GetInt32(0) != NodeNamingProtocol.FormatVersion || r.GetString(1) != NodeNamingProtocol.Version || r.GetString(2) != hash) throw new InvalidDataException("Prepared naming request mismatch.");
            return new(SummaryOutcomeKind.NoChange, new(taskId, job.Revision, NodeNamingProtocol.FormatVersion, hash, request));
        }
        r.Close(); var revision = checked(job.Revision + 1);
        using var q = ChatCommand(c, t, "UPDATE node_naming_job SET prepared_format_version=$format,protocol_version=$protocol,request_hash=$hash,revision=$revision WHERE id=$id", ("$format", NodeNamingProtocol.FormatVersion), ("$protocol", NodeNamingProtocol.Version), ("$hash", hash), ("$revision", revision), ("$id", taskId)); q.ExecuteNonQuery();
        return new(SummaryOutcomeKind.Applied, new(taskId, revision, NodeNamingProtocol.FormatVersion, hash, request));
    });
}
