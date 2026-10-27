using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    private const string SummaryJobColumns = "id,operation_id,workspace_id,conversation_id,planned_node_id,baseline_version_id,baseline_node_revision,creates_node,status,revision,input_plan_json,accepted_at,error_code,actual_model_id,output_version_id";
    private static Guid? SummaryGuid(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));
    private static string? SummaryString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static T SummaryJson<T>(string json) => JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("Missing summary metadata.");
    private static KnowledgeSummaryJob ReadSummaryJob(SqliteDataReader r)
    {
        var plan = SummaryJson<KnowledgeSummaryInputPlan>(r.GetString(10));
        ValidateConfiguration(plan.Configuration);
        return new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), Guid.Parse(r.GetString(3)),
            new(Guid.Parse(r.GetString(4)), SummaryGuid(r, 5), r.GetInt64(6), r.GetBoolean(7)), Enum.Parse<GenerationStatus>(r.GetString(8)), r.GetInt64(9),
            plan, ContextTime(r.GetString(11)), SummaryString(r, 12), SummaryString(r, 13), SummaryGuid(r, 14));
    }
    private static KnowledgeSummaryJob? ReadSummaryJob(SqliteConnection c, SqliteTransaction? t, Guid id, bool operation = false)
    {
        using var q = ChatCommand(c, t, $"SELECT {SummaryJobColumns} FROM knowledge_summary_job WHERE {(operation ? "operation_id" : "id")}=$id", ("$id", id));
        using var r = q.ExecuteReader(); return r.Read() ? ReadSummaryJob(r) : null;
    }
    private static ImmutableArray<KnowledgeSummaryJob> ReadActiveSummaryJobs(SqliteConnection c, SqliteTransaction? t)
    {
        using var q = ChatCommand(c, t, $"SELECT {SummaryJobColumns} FROM knowledge_summary_job WHERE status IN ('Queued','Running') ORDER BY accepted_at,id");
        using var r = q.ExecuteReader(); var jobs = ImmutableArray.CreateBuilder<KnowledgeSummaryJob>();
        while (r.Read()) jobs.Add(ReadSummaryJob(r)); return jobs.ToImmutable();
    }
    // All aggregate readers take an explicit transaction; Task 3 may reuse them within its own read/write snapshot.
    private static SummaryVersionState? ReadSummaryState(SqliteConnection c, SqliteTransaction t, Guid id, bool byConversation = false)
    {
        using var q = ChatCommand(c, t, $"""
            SELECT id,workspace_id,conversation_id,current_version_id,candidate_version_id,revision,base_title,display_name,name_is_manual,name_revision,created_at
            FROM summary_node WHERE {(byConversation ? "conversation_id" : "id")}=$id
            """, ("$id", id));
        using var r = q.ExecuteReader(); if (!r.Read()) return null;
        var node = new SummaryNode(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), Guid.Parse(r.GetString(3)), SummaryGuid(r, 4),
            r.GetInt64(5), r.GetString(6), r.GetString(7), r.GetBoolean(8), r.GetInt64(9), ContextTime(r.GetString(10))); r.Close();
        long epoch = 0, revision = 0; var dirty = false;
        using (var draft = ChatCommand(c, t, "SELECT epoch,revision,is_dirty FROM summary_edit_draft WHERE node_id=$id", ("$id", node.Id)))
        using (var d = draft.ExecuteReader()) if (d.Read()) { epoch = d.GetInt64(0); revision = d.GetInt64(1); dirty = d.GetBoolean(2); }
        using var active = ChatCommand(c, t, "SELECT id FROM knowledge_summary_job WHERE planned_node_id=$id AND status IN ('Queued','Running') ORDER BY accepted_at,id", ("$id", node.Id));
        using var a = active.ExecuteReader(); var ids = ImmutableArray.CreateBuilder<Guid>(); while (a.Read()) ids.Add(Guid.Parse(a.GetString(0))); a.Close();
        return new(node, ReadSummaryHeaders(c, t, node.Id), dirty, epoch, revision, ids.ToImmutable());
    }
    private static long NextSummaryOrdinal(SqliteConnection c, SqliteTransaction t, Guid nodeId)
    {
        using var q = ChatCommand(c, t, "SELECT last_version_ordinal FROM summary_node WHERE id=$id", ("$id", nodeId));
        return checked((q.ExecuteScalar() is long ordinal ? ordinal : 0) + 1);
    }
    private static ImmutableArray<SummaryVersionHeader> ReadSummaryHeaders(SqliteConnection c, SqliteTransaction t, Guid nodeId)
    {
        using var q = ChatCommand(c, t, """
            SELECT i.id,i.node_id,i.ordinal,i.origin,i.created_at,v.title,v.baseline_version_id,v.requested_model_id,v.actual_model_id
            FROM summary_version_identity i JOIN summary_version v ON v.id=i.id AND v.node_id=i.node_id WHERE i.node_id=$id ORDER BY i.ordinal
            """, ("$id", nodeId));
        using var r = q.ExecuteReader(); var result = ImmutableArray.CreateBuilder<SummaryVersionHeader>();
        while (r.Read()) result.Add(new(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetInt64(2), Enum.Parse<SummaryVersionOrigin>(r.GetString(3)), ContextTime(r.GetString(4))),
            r.GetString(5), SummaryGuid(r, 6), SummaryString(r, 7), SummaryString(r, 8)));
        return result.ToImmutable();
    }
}
