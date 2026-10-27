using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Summaries;
using Mindweave.Core.Chat;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    private static KnowledgeSummaryMaterial ReadSummaryMaterial(SqliteConnection c, SqliteTransaction t, Guid conversationId, long? highWater = null)
    {
        var conversation = ReadConversation(c, t, conversationId) ?? throw new KnowledgeSummaryInputException("SummarySourceConversationMissing");
        var own = ReadMessages(c, t, conversationId).Where(m => highWater is null || m.Sequence <= highWater).ToImmutableArray();
        ImmutableArray<SummaryFrozenRange> ranges;
        try { ranges = ReadValidatedRanges(c, t, conversationId).Select(r => new SummaryFrozenRange(r.ConversationId, r.MessageIds.ToImmutableArray())).ToImmutableArray(); }
        catch (InvalidDataException) { throw new KnowledgeSummaryInputException("KnowledgeSummaryUnauthorizedBackground"); }
        var ancestors = ImmutableArray.CreateBuilder<ChatMessage>();
        foreach (var range in ranges)
        {
            if (ReadConversation(c, t, range.ConversationId)?.WorkspaceId != conversation.WorkspaceId) throw new KnowledgeSummaryInputException("KnowledgeSummaryUnauthorizedBackground");
            var rows = ReadMessages(c, t, range.ConversationId).ToDictionary(m => m.Id);
            foreach (var id in range.MessageIds)
                ancestors.Add(rows.TryGetValue(id, out var message) ? message : throw new KnowledgeSummaryInputException("SummaryFrozenSourceMissing"));
        }
        var source = ReadBranchSource(c, t, conversationId);
        if (source is not null)
        {
            var original = ancestors.SingleOrDefault(m => m.Id == source.MessageId && m.ConversationId == source.ParentConversationId);
            using var q = ChatCommand(c, t, "SELECT message_revision,source_body_hash FROM branch_source WHERE child_conversation_id=$id", ("$id", conversationId));
            using var r = q.ExecuteReader();
            if (!r.Read() || original is null || original.Revision != r.GetInt64(0) || KnowledgeSummaryInputPlanner.Hash(original.Body) != r.GetString(1))
                throw new KnowledgeSummaryInputException("KnowledgeSummaryUnboundQuote");
        }
        return new(conversationId, own, ancestors.ToImmutable(), ranges, source);
    }

    private static PreparedKnowledgeSummaryRequest RebuildSummaryRequest(SqliteConnection c, SqliteTransaction t, KnowledgeSummaryJob job)
    {
        if (job.InputPlan.Coverage.ConversationId != job.ConversationId || ReadConversation(c, t, job.ConversationId)?.WorkspaceId != job.WorkspaceId)
            throw new KnowledgeSummaryInputException("KnowledgeSummaryOwnershipMismatch");
        ValidateConfiguration(job.InputPlan.Configuration);
        var material = ReadSummaryMaterial(c, t, job.ConversationId, job.InputPlan.Coverage.OwnHighWaterSequence);
        ValidateSummarySources(c, t, job.Id, material);
        var request = KnowledgeSummaryInputPlanner.CreateRequest(job.InputPlan, material);
        return new(job.Id, job.InputPlan.PreparedFormatVersion, job.InputPlan.RequestHash, request);
    }
    private static IEnumerable<(string Scope, int Ordinal, KnowledgeSummarySourceRef Reference)> SummarySourceRows(KnowledgeSummaryMaterial material)
    {
        static KnowledgeSummarySourceRef Reference(ChatMessage m, int i, SummarySourceScope scope) =>
            new(i, scope, m.Id, m.ConversationId, m.Sequence, m.Revision, KnowledgeSummaryInputPlanner.Hash(m.Body), m.Role, m.Status, m.ReplyToUserId, m.SupersedesMessageId);
        return material.OwnRows.Select((m, i) => ("OwnRow", i, Reference(m, i, SummarySourceScope.Own)))
            .Concat(material.FrozenAncestors.Select((m, i) => ("FrozenAncestor", i, Reference(m, i, SummarySourceScope.FrozenAncestor))));
    }
    private static void InsertSummarySources(SqliteConnection c, SqliteTransaction t, Guid taskId, KnowledgeSummaryMaterial material)
    {
        foreach (var (scope, ordinal, reference) in SummarySourceRows(material))
        {
            using var q = ChatCommand(c, t, "INSERT INTO knowledge_summary_source VALUES($id,$scope,$ordinal,$message,$conversation,$reference)",
                ("$id", taskId), ("$scope", scope), ("$ordinal", ordinal), ("$message", reference.MessageId), ("$conversation", reference.ConversationId), ("$reference", JsonSerializer.Serialize(reference)));
            q.ExecuteNonQuery();
        }
    }
    private static void ValidateSummarySources(SqliteConnection c, SqliteTransaction t, Guid taskId, KnowledgeSummaryMaterial material)
    {
        var expected = SummarySourceRows(material).OrderBy(r => r.Scope, StringComparer.Ordinal).ThenBy(r => r.Ordinal).ToArray();
        using var q = ChatCommand(c, t, "SELECT scope,ordinal,message_id,conversation_id,reference_json FROM knowledge_summary_source WHERE task_id=$id ORDER BY scope,ordinal", ("$id", taskId));
        using var r = q.ExecuteReader(); var i = 0;
        while (r.Read())
        {
            if (i >= expected.Length) throw new KnowledgeSummaryInputException("KnowledgeSummarySourceReferencesMismatch");
            var e = expected[i++];
            if (e.Scope != r.GetString(0) || e.Ordinal != r.GetInt32(1) || e.Reference.MessageId != Guid.Parse(r.GetString(2)) ||
                e.Reference.ConversationId != Guid.Parse(r.GetString(3)) || JsonSerializer.Serialize(e.Reference) != r.GetString(4))
                throw new KnowledgeSummaryInputException("KnowledgeSummarySourceReferencesMismatch");
        }
        if (i != expected.Length) throw new KnowledgeSummaryInputException("KnowledgeSummarySourceReferencesMismatch");
    }

    public Task<SummaryOutcome<PreparedKnowledgeSummaryRequest>> PrepareKnowledgeSummaryRequestAsync(Guid taskId, long expectedRevision) =>
        WriteAsync<SummaryOutcome<PreparedKnowledgeSummaryRequest>>((c, t) =>
        {
            var job = ReadSummaryJob(c, t, taskId);
            if (job is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryTaskMissing");
            if (!IsActive(job.Status) || job.Revision != expectedRevision) return new(SummaryOutcomeKind.Conflict, Code: "SummaryTaskChanged");
            var existing = ReadPreparedSummary(c, t, taskId);
            if (existing is not null) return new(SummaryOutcomeKind.NoChange, existing);
            PreparedKnowledgeSummaryRequest prepared;
            try { prepared = RebuildSummaryRequest(c, t, job); }
            catch (KnowledgeSummaryInputException e) { return new(SummaryOutcomeKind.Rejected, Code: e.Code); }
            using (var q = ChatCommand(c, t, "INSERT INTO knowledge_summary_prepared VALUES($id,$format,$config,$hash)", ("$id", taskId), ("$format", prepared.FormatVersion),
                ("$config", JsonSerializer.Serialize(prepared.Request.Configuration)), ("$hash", prepared.RequestHash))) q.ExecuteNonQuery();
            foreach (var m in job.InputPlan.Materials)
            {
                using var q = ChatCommand(c, t, "INSERT INTO knowledge_summary_material VALUES($id,$ordinal,$kind,$index,$path,$message,$hash)", ("$id", taskId), ("$ordinal", m.Ordinal),
                    ("$kind", m.Kind.ToString()), ("$index", m.TransportIndex), ("$path", m.JsonPath), ("$message", m.MessageId), ("$hash", m.Hash)); q.ExecuteNonQuery();
            }
            return new(SummaryOutcomeKind.Applied, prepared);
        });

    public Task<PreparedKnowledgeSummaryRequest?> LoadPreparedKnowledgeSummaryRequestAsync(Guid taskId) => ReadAsync(c =>
    {
        using var t = c.BeginTransaction(deferred: true); return ReadPreparedSummary(c, t, taskId);
    });
    private static PreparedKnowledgeSummaryRequest? ReadPreparedSummary(SqliteConnection c, SqliteTransaction t, Guid id)
    {
        using var q = ChatCommand(c, t, "SELECT format_version,configuration_json,request_hash FROM knowledge_summary_prepared WHERE task_id=$id", ("$id", id));
        using var r = q.ExecuteReader(); if (!r.Read()) return null;
        var format = r.GetInt32(0); var config = r.GetString(1); var hash = r.GetString(2); r.Close();
        var job = ReadSummaryJob(c, t, id) ?? throw new InvalidDataException("Prepared summary task missing.");
        var rebuilt = RebuildSummaryRequest(c, t, job);
        if (format != rebuilt.FormatVersion || config != JsonSerializer.Serialize(rebuilt.Request.Configuration) || hash != rebuilt.RequestHash)
            throw new KnowledgeSummaryInputException("KnowledgeSummaryPreparedMismatch");
        using var refs = ChatCommand(c, t, "SELECT ordinal,kind,transport_index,json_path,message_id,material_hash FROM knowledge_summary_material WHERE task_id=$id ORDER BY ordinal", ("$id", id));
        using var m = refs.ExecuteReader(); var material = ImmutableArray.CreateBuilder<SummaryMaterialReference>();
        while (m.Read()) material.Add(new(m.GetInt32(0), Enum.Parse<SummaryMaterialKind>(m.GetString(1)), m.GetInt32(2), m.GetString(3), SummaryGuid(m, 4), m.GetString(5)));
        if (!material.SequenceEqual(job.InputPlan.Materials)) throw new KnowledgeSummaryInputException("KnowledgeSummaryPreparedMaterialsMismatch");
        return rebuilt;
    }
}
