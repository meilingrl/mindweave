using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore : INodeNamingStore
{
    public Task<NodeNamingSnapshot> ReadNodeNamingGroupAsync(Guid workspaceId, string normalizedGroupKey) => ReadSummaryAsync((c, t) => ReadNamingGroup(c, t, workspaceId, normalizedGroupKey));
    private static NodeNamingSnapshot ReadNamingGroup(SqliteConnection c, SqliteTransaction t, Guid workspace, string key)
    {
        if (SummaryTitleNormalization.GroupKey(key) != key) throw new ArgumentException("Naming key must be normalized.", nameof(key));
        using var epochQuery = ChatCommand(c, t, "SELECT epoch FROM node_naming_group WHERE workspace_id=$w AND group_key=$g", ("$w", workspace), ("$g", key));
        var epoch = Convert.ToInt64(epochQuery.ExecuteScalar() ?? 0L);
        using var q = ChatCommand(c, t, """
            SELECT n.id,n.current_version_id,n.name_revision,n.name_is_manual,v.content_hash,v.title,v.markdown
            FROM summary_node n JOIN summary_version v ON v.id=n.current_version_id AND v.node_id=n.id
            WHERE n.workspace_id=$w AND n.group_key=$g ORDER BY n.id
            """, ("$w", workspace), ("$g", key));
        using var r = q.ExecuteReader(); var members = ImmutableArray.CreateBuilder<NodeNamingMember>();
        while (r.Read())
        {
            var hash = SummaryContentHash(new(r.GetString(5), r.GetString(6)));
            if (hash != r.GetString(4)) throw new InvalidDataException("Naming source hash mismatch.");
            members.Add(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetInt64(2), r.GetBoolean(3), hash));
        }
        var snapshot = new NodeNamingSnapshot(workspace, key, epoch, members.ToImmutable(), "");
        return snapshot with { SnapshotHash = NamingSnapshotHash(snapshot) };
    }
    private static string NamingSnapshotHash(NodeNamingSnapshot s) => KnowledgeSummaryInputPlanner.Hash(JsonSerializer.Serialize(s with { SnapshotHash = "" }));
    private static bool SameNamingSnapshot(NodeNamingSnapshot a, NodeNamingSnapshot b) =>
        !a.Members.IsDefault && a.WorkspaceId == b.WorkspaceId && a.GroupKey == b.GroupKey && a.GroupEpoch == b.GroupEpoch && a.SnapshotHash == b.SnapshotHash && a.SnapshotHash == NamingSnapshotHash(a) && a.Members.SequenceEqual(b.Members);
    public Task<SummaryOutcome<NodeNamingJob>> AcceptNodeNamingAsync(Guid operationId, NodeNamingSnapshot expected, AiConfiguration configuration) => WriteAsync<SummaryOutcome<NodeNamingJob>>((c, t) =>
    {
        if (operationId == Guid.Empty || expected.Members.IsDefaultOrEmpty) return new(SummaryOutcomeKind.Rejected, Code: "InvalidNamingIdentity");
        var known = ReadNamingJob(c, t, operationId, true);
        if (known is not null)
        {
            if (!SameNamingSnapshot(expected, known.Snapshot) || known.Configuration != configuration) return new(SummaryOutcomeKind.Conflict, Code: "NamingOperationReused");
            ClearNamingIntent(c, t, expected.WorkspaceId, expected.GroupKey, operationId);
            return new(SummaryOutcomeKind.NoChange, known);
        }
        try { ValidateConfiguration(configuration); } catch (ArgumentException) { return new(SummaryOutcomeKind.Rejected, Code: "InvalidNamingConfiguration"); }
        var live = ReadNamingGroup(c, t, expected.WorkspaceId, expected.GroupKey);
        if (!SameNamingSnapshot(expected, live)) return new(SummaryOutcomeKind.Obsolete, Code: "NamingGroupChanged");
        using var busy = ChatCommand(c, t, "SELECT count(*) FROM node_naming_job WHERE workspace_id=$w AND group_key=$g AND status IN ('Queued','Running')", ("$w", expected.WorkspaceId), ("$g", expected.GroupKey));
        if (Convert.ToInt64(busy.ExecuteScalar()) != 0) return new(SummaryOutcomeKind.Blocked, Code: "NamingGroupActive");
        var job = new NodeNamingJob(Guid.NewGuid(), operationId, expected, configuration, GenerationStatus.Queued, 0, DateTimeOffset.UtcNow, null);
        using var q = ChatCommand(c, t, """
            INSERT INTO node_naming_job(id,operation_id,workspace_id,group_key,group_epoch,snapshot_json,configuration_json,status,revision,accepted_at)
            VALUES($id,$op,$w,$g,$epoch,$snapshot,$config,'Queued',0,$now)
            """, ("$id", job.Id), ("$op", operationId), ("$w", expected.WorkspaceId), ("$g", expected.GroupKey), ("$epoch", expected.GroupEpoch), ("$snapshot", JsonSerializer.Serialize(expected)), ("$config", JsonSerializer.Serialize(configuration)), ("$now", job.AcceptedAt.ToString("O")));
        q.ExecuteNonQuery();
        foreach (var (m, i) in expected.Members.Select((m, i) => (m, i)))
        {
            using var member = ChatCommand(c, t, "INSERT INTO node_naming_member VALUES($task,$i,$node,$version,$revision,$manual,$hash)", ("$task", job.Id), ("$i", i), ("$node", m.NodeId), ("$version", m.CurrentVersionId), ("$revision", m.NameRevision), ("$manual", m.NameIsManual), ("$hash", m.ContentHash)); member.ExecuteNonQuery();
        }
        ClearNamingIntent(c, t, expected.WorkspaceId, expected.GroupKey, operationId);
        return new(SummaryOutcomeKind.Applied, job);
    });
    private static NodeNamingJob? ReadNamingJob(SqliteConnection c, SqliteTransaction t, Guid id, bool operation = false)
    {
        using var q = ChatCommand(c, t, "SELECT id,operation_id,snapshot_json,configuration_json,status,revision,accepted_at,error_code,workspace_id,group_key,group_epoch FROM node_naming_job WHERE " + (operation ? "operation_id" : "id") + "=$id", ("$id", id));
        using var r = q.ExecuteReader(); if (!r.Read()) return null;
        var snapshot = SummaryJson<NodeNamingSnapshot>(r.GetString(2));
        if (snapshot.WorkspaceId != Guid.Parse(r.GetString(8)) || snapshot.GroupKey != r.GetString(9) || snapshot.GroupEpoch != r.GetInt64(10) || snapshot.SnapshotHash != NamingSnapshotHash(snapshot)) throw new InvalidDataException("Naming snapshot identity mismatch.");
        var job = new NodeNamingJob(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), snapshot, SummaryJson<AiConfiguration>(r.GetString(3)), Enum.Parse<GenerationStatus>(r.GetString(4)), r.GetInt64(5), ContextTime(r.GetString(6)), SummaryString(r, 7)); r.Close();
        using var m = ChatCommand(c, t, "SELECT ordinal,node_id,current_version_id,name_revision,name_is_manual,content_hash FROM node_naming_member WHERE task_id=$id ORDER BY ordinal", ("$id", job.Id));
        using var rows = m.ExecuteReader(); var i = 0;
        while (rows.Read())
        {
            if (i >= snapshot.Members.Length || rows.GetInt32(0) != i || snapshot.Members[i++] != new NodeNamingMember(Guid.Parse(rows.GetString(1)), Guid.Parse(rows.GetString(2)), rows.GetInt64(3), rows.GetBoolean(4), rows.GetString(5))) throw new InvalidDataException("Naming member identity mismatch.");
        }
        if (i != snapshot.Members.Length) throw new InvalidDataException("Naming members missing.");
        return job;
    }
    private static ImmutableArray<NodeNamingJob> ReadActiveNamingJobs(SqliteConnection c, SqliteTransaction t)
    {
        using var q = ChatCommand(c, t, "SELECT id FROM node_naming_job WHERE status IN ('Queued','Running') ORDER BY accepted_at,id");
        using var r = q.ExecuteReader(); var ids = new List<Guid>(); while (r.Read()) ids.Add(Guid.Parse(r.GetString(0))); r.Close();
        return ids.Select(id => ReadNamingJob(c, t, id)!).ToImmutableArray();
    }
    public Task<NodeNamingJob?> LoadNodeNamingJobAsync(Guid taskId) => ReadSummaryAsync((c, t) => ReadNamingJob(c, t, taskId));
    public Task<NodeNamingJob?> FindNodeNamingOperationAsync(Guid operationId) => ReadSummaryAsync((c, t) => ReadNamingJob(c, t, operationId, true));
    public Task<ImmutableArray<NodeNamingJob>> ListActiveNodeNamingJobsAsync() => ReadSummaryAsync(ReadActiveNamingJobs);
    public Task<SummaryOutcome<ImmutableArray<NodeNamingMaterial>>> LoadNodeNamingMaterialsAsync(Guid taskId) => ReadSummaryAsync((c, t) =>
    {
        var job = ReadNamingJob(c, t, taskId);
        if (job is null) return new SummaryOutcome<ImmutableArray<NodeNamingMaterial>>(SummaryOutcomeKind.NotFound, Code: "NamingTaskMissing");
        return ReadNamingMaterials(c, t, job);
    });
    private static SummaryOutcome<ImmutableArray<NodeNamingMaterial>> ReadNamingMaterials(SqliteConnection c, SqliteTransaction t, NodeNamingJob job)
    {
        if (!IsActive(job.Status) || !SameNamingSnapshot(job.Snapshot, ReadNamingGroup(c, t, job.Snapshot.WorkspaceId, job.Snapshot.GroupKey))) return new(SummaryOutcomeKind.Obsolete, Code: "NamingGroupChanged");
        var materials = job.Snapshot.Members.Select(m =>
        {
            var node = ReadSummaryState(c, t, m.NodeId)!.Node; var v = ReadSummaryVersion(c, t, m.CurrentVersionId)!;
            return new NodeNamingMaterial(m, node.BaseTitle, node.DisplayName, new(v.Header.Title, v.Markdown));
        }).ToImmutableArray();
        return new(SummaryOutcomeKind.Applied, materials);
    }
    public Task<SummaryOutcome<NodeNamingJob>> UpdateNodeNamingMetadataAsync(SummaryGenerationMetadata update) => WriteAsync<SummaryOutcome<NodeNamingJob>>((c, t) =>
    {
        try { ValidateGenerationMetadata(update.ErrorCode, update.ActualModelId); } catch (ArgumentException) { return new(SummaryOutcomeKind.Rejected, Code: "InvalidNamingMetadata"); }
        if (!Enum.IsDefined(update.Status) || update.Status == GenerationStatus.Completed || update.ActualModelId is not null) return new(SummaryOutcomeKind.Rejected, Code: "InvalidNamingTransition");
        var job = ReadNamingJob(c, t, update.TaskId); if (job is null) return new(SummaryOutcomeKind.NotFound, Code: "NamingTaskMissing");
        if (!IsActive(job.Status) || job.Revision != update.ExpectedRevision) return new(SummaryOutcomeKind.Conflict, Code: "NamingTaskChanged");
        if (job.Status == GenerationStatus.Running && update.Status == GenerationStatus.Queued) return new(SummaryOutcomeKind.Rejected, Code: "InvalidNamingTransition");
        using var q = ChatCommand(c, t, "UPDATE node_naming_job SET status=$status,revision=$revision,error_code=$error WHERE id=$id", ("$status", update.Status.ToString()), ("$revision", checked(job.Revision + 1)), ("$error", update.ErrorCode), ("$id", job.Id)); q.ExecuteNonQuery();
        return new(SummaryOutcomeKind.Applied, job with { Status = update.Status, Revision = job.Revision + 1, ErrorCode = update.ErrorCode });
    });
    public Task<SummaryOutcome<NodeNamingResult>> CompleteNodeNamingAsync(NodeNamingCompletion completion) => WriteAsync<SummaryOutcome<NodeNamingResult>>((c, t) =>
    {
        var job = ReadNamingJob(c, t, completion.TaskId); if (job is null) return new(SummaryOutcomeKind.NotFound, Code: "NamingTaskMissing");
        if (completion.ExpectedSnapshotHash != job.Snapshot.SnapshotHash) return new(SummaryOutcomeKind.Obsolete, Code: "NamingSnapshotChanged");
        ImmutableArray<NodeNameProposal> names;
        try { names = NodeNamingProtocol.ValidateNames(completion.Names, job.Snapshot); } catch (SummaryProtocolException) { return new(SummaryOutcomeKind.Rejected, Code: "InvalidNodeNames"); }
        var outputHash = KnowledgeSummaryInputPlanner.Hash(JsonSerializer.Serialize(names));
        if (job.Status == GenerationStatus.Completed)
        {
            using var prior = ChatCommand(c, t, "SELECT output_hash,changed_node_ids_json FROM node_naming_job WHERE id=$id", ("$id", job.Id)); using var r = prior.ExecuteReader(); r.Read();
            return r.GetString(0) == outputHash && job.Revision == checked(completion.ExpectedRevision + 1) ? new(SummaryOutcomeKind.NoChange, new(job.Id, SummaryJson<ImmutableArray<Guid>>(r.GetString(1)))) : new(SummaryOutcomeKind.Conflict, Code: "NamingCompletionChanged");
        }
        if (!IsActive(job.Status) || job.Revision != completion.ExpectedRevision || !SameNamingSnapshot(job.Snapshot, ReadNamingGroup(c, t, job.Snapshot.WorkspaceId, job.Snapshot.GroupKey))) return new(SummaryOutcomeKind.Obsolete, Code: "NamingGroupChanged");
        using (var prepared = ChatCommand(c, t, "SELECT prepared_format_version,protocol_version,request_hash FROM node_naming_job WHERE id=$id", ("$id", job.Id)))
        {
            using var r = prepared.ExecuteReader(); r.Read();
            if (r.IsDBNull(0)) return new(SummaryOutcomeKind.Rejected, Code: "NamingNotPrepared");
            var materials = ReadNamingMaterials(c, t, job);
            var request = NodeNamingProtocol.CreateRequest(job, materials.Value);
            if (r.GetInt32(0) != NodeNamingProtocol.FormatVersion || r.GetString(1) != NodeNamingProtocol.Version || r.GetString(2) != NodeNamingProtocol.RequestHash(request)) throw new InvalidDataException("Prepared naming request mismatch.");
        }
        var changed = ImmutableArray.CreateBuilder<Guid>();
        foreach (var name in names)
        {
            var node = ReadSummaryState(c, t, name.NodeId)!.Node;
            if (node.DisplayName == name.DisplayName) continue;
            using var q = ChatCommand(c, t, "UPDATE summary_node SET display_name=$name,name_revision=$revision WHERE id=$id", ("$name", name.DisplayName), ("$revision", checked(node.NameRevision + 1)), ("$id", node.Id)); q.ExecuteNonQuery(); changed.Add(node.Id);
        }
        var result = new NodeNamingResult(job.Id, changed.ToImmutable());
        using var done = ChatCommand(c, t, "UPDATE node_naming_job SET status='Completed',revision=$revision,output_hash=$hash,changed_node_ids_json=$ids WHERE id=$id", ("$revision", checked(job.Revision + 1)), ("$hash", outputHash), ("$ids", JsonSerializer.Serialize(result.ChangedNodeIds)), ("$id", job.Id)); done.ExecuteNonQuery();
        return new(SummaryOutcomeKind.Applied, result);
    });
    public Task RecoverInterruptedNodeNamingAsync() => WriteAsync((c, t) =>
    {
        foreach (var job in ReadActiveNamingJobs(c, t))
        {
            using var q = ChatCommand(c, t, "UPDATE node_naming_job SET status='Interrupted',revision=$revision,error_code='Interrupted' WHERE id=$id", ("$revision", checked(job.Revision + 1)), ("$id", job.Id)); q.ExecuteNonQuery();
        }
        using var clear = ChatCommand(c, t, "UPDATE node_naming_group SET pending_operation_id=NULL,pending_snapshot_json=NULL,pending_configuration_json=NULL,pending_at=NULL"); clear.ExecuteNonQuery(); return true;
    });
}
