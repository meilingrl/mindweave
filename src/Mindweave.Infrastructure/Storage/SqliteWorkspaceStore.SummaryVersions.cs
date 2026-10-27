using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore : ISummaryVersionStore
{
    private Task<T> ReadSummaryAsync<T>(Func<SqliteConnection, SqliteTransaction, T> read) => ReadAsync(c =>
    {
        using var t = c.BeginTransaction(deferred: true);
        return read(c, t);
    });

    public Task<ImmutableArray<SummaryNodeSnapshot>> ListSummaryNodesAsync(Guid workspaceId) => ReadSummaryAsync((c, t) =>
    {
        using var q = ChatCommand(c, t, "SELECT id FROM summary_node WHERE workspace_id=$id ORDER BY created_at,id", ("$id", workspaceId));
        using var r = q.ExecuteReader(); var ids = new List<Guid>(); while (r.Read()) ids.Add(Guid.Parse(r.GetString(0))); r.Close();
        return ids.Select(id => ReadSummarySnapshot(c, t, id)!).ToImmutableArray();
    });
    public Task<SummaryNodeSnapshot?> LoadSummaryNodeAsync(Guid nodeId) => ReadSummaryAsync((c, t) => ReadSummarySnapshot(c, t, nodeId));
    public Task<SummaryNodeSnapshot?> FindConversationSummaryAsync(Guid conversationId) => ReadSummaryAsync((c, t) => ReadSummarySnapshot(c, t, conversationId, true));
    public Task<ImmutableArray<SummaryVersionHeader>> ListSummaryVersionsAsync(Guid nodeId) => ReadSummaryAsync((c, t) => ReadSummaryHeaders(c, t, nodeId));
    public Task<SummaryVersion?> LoadSummaryVersionAsync(Guid versionId) => ReadSummaryAsync((c, t) => ReadSummaryVersion(c, t, versionId));

    private static SummaryVersion? ReadSummaryVersion(SqliteConnection c, SqliteTransaction t, Guid id)
    {
        using var q = ChatCommand(c, t, """
            SELECT i.id,i.node_id,i.ordinal,i.origin,i.created_at,v.title,v.baseline_version_id,v.requested_model_id,v.actual_model_id,v.markdown,s.coverage_json
            FROM summary_version v JOIN summary_version_identity i ON i.id=v.id JOIN summary_version_source s ON s.version_id=v.id WHERE v.id=$id
            """, ("$id", id));
        using var r = q.ExecuteReader(); if (!r.Read()) return null;
        return new(new(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetInt64(2), Enum.Parse<SummaryVersionOrigin>(r.GetString(3)), ContextTime(r.GetString(4))),
            r.GetString(5), SummaryGuid(r, 6), SummaryString(r, 7), SummaryString(r, 8)), r.GetString(9), SummaryJson<KnowledgeSummaryCoverage>(r.GetString(10)));
    }
    private static SummaryNodeSnapshot? ReadSummarySnapshot(SqliteConnection c, SqliteTransaction t, Guid id, bool byConversation = false)
    {
        var state = ReadSummaryState(c, t, id, byConversation); if (state is null) return null;
        using var q = ChatCommand(c, t, """
            SELECT r.is_read, EXISTS(SELECT 1 FROM chat_message m WHERE m.conversation_id=$conversation
                AND m.sequence>json_extract(s.coverage_json,'$.OwnHighWaterSequence') AND m.status NOT IN ('Queued','Running'))
            FROM summary_version_source s JOIN summary_read_state r ON r.version_id=s.version_id WHERE s.version_id=$id
            """, ("$conversation", state.Node.ConversationId), ("$id", state.Node.CurrentVersionId));
        using var r = q.ExecuteReader(); if (!r.Read()) throw new InvalidDataException("Summary current metadata is missing.");
        return new(state, r.GetBoolean(0), r.GetBoolean(1));
    }
    public Task<SummaryOutcome<SummaryNodeSnapshot>> AdoptSummaryCandidateAsync(SummaryNodeToken expected, SummaryReplacementConfirmation? confirmation = null) =>
        WriteAsync<SummaryOutcome<SummaryNodeSnapshot>>((c, t) =>
        {
            var state = ReadSummaryState(c, t, expected.NodeId); if (state is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryNodeMissing");
            var decision = SummaryVersionPolicy.AdoptCandidate(state, expected, confirmation);
            if (decision.Kind != SummaryOutcomeKind.Applied) return new(decision.Kind, Code: decision.Code, AdoptionConflict: decision.AdoptionConflict);
            ChangeSummaryCurrent(c, t, state, decision.Value!.CurrentVersionId, decision.Value.CandidateVersionId);
            return new(SummaryOutcomeKind.Applied, ReadSummarySnapshot(c, t, expected.NodeId));
        });
    public Task<SummaryOutcome<SummaryNodeSnapshot>> SetCurrentSummaryVersionAsync(SummaryNodeToken expected, Guid versionId) =>
        WriteAsync<SummaryOutcome<SummaryNodeSnapshot>>((c, t) =>
        {
            var state = ReadSummaryState(c, t, expected.NodeId); if (state is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryNodeMissing");
            var decision = SummaryVersionPolicy.SetCurrent(state, versionId, expected);
            if (decision.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)) return new(decision.Kind, Code: decision.Code);
            if (decision.Kind == SummaryOutcomeKind.Applied) ChangeSummaryCurrent(c, t, state, versionId, null);
            return new(decision.Kind, ReadSummarySnapshot(c, t, expected.NodeId));
        });
    public Task<SummaryOutcome<SummaryNodeSnapshot>> DiscardSummaryCandidateAsync(SummaryNodeToken expected) =>
        WriteAsync<SummaryOutcome<SummaryNodeSnapshot>>((c, t) =>
        {
            var state = ReadSummaryState(c, t, expected.NodeId); if (state is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryNodeMissing");
            if (state.Token != expected) return new(SummaryOutcomeKind.Conflict, Code: "NodeChanged");
            if (state.Node.CandidateVersionId is not { } candidate) return new(SummaryOutcomeKind.NotFound, Code: "NoCandidate");
            using (var q = ChatCommand(c, t, "UPDATE summary_node SET candidate_version_id=NULL,revision=$revision WHERE id=$id", ("$revision", checked(state.Node.Revision + 1)), ("$id", expected.NodeId))) q.ExecuteNonQuery();
            RepairSummarySelection(c, t, state.Node, [candidate], state.Node.CurrentVersionId);
            DeleteSummaryBodies(c, t, [candidate]); PruneSummaryIdentities(c, t, state.Node.Id);
            return new(SummaryOutcomeKind.Applied, ReadSummarySnapshot(c, t, expected.NodeId));
        });
    // Every current change uses the new immutable title for grouping; manual display names remain independent.
    private static void ChangeSummaryCurrent(SqliteConnection c, SqliteTransaction t, SummaryVersionState state, Guid current, Guid? candidate)
    {
        var title = ReadSummaryVersion(c, t, current)?.Header.Title ?? throw new InvalidDataException("Summary current body is missing.");
        var node = state.Node; var display = node.NameIsManual ? node.DisplayName : title;
        using var q = ChatCommand(c, t, """
            UPDATE summary_node SET current_version_id=$current,candidate_version_id=$candidate,revision=$revision,base_title=$title,
                group_key=$key,display_name=$display,name_revision=$nameRevision WHERE id=$id
            """, ("$current", current), ("$candidate", candidate), ("$revision", checked(node.Revision + 1)), ("$title", title),
            ("$key", SummaryTitleNormalization.GroupKey(title)), ("$display", display), ("$nameRevision", display == node.DisplayName ? node.NameRevision : checked(node.NameRevision + 1)), ("$id", node.Id));
        q.ExecuteNonQuery();
        if (ReadSummaryDraft(c, t, node.Id) is { } draft) ClearSummaryDraft(c, t, draft, current);
    }
    public Task<SummaryOutcome<SummaryNode>> RenameSummaryNodeAsync(Guid nodeId, long expectedNameRevision, string displayName) =>
        WriteAsync<SummaryOutcome<SummaryNode>>((c, t) =>
        {
            var state = ReadSummaryState(c, t, nodeId); if (state is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryNodeMissing");
            if (state.Node.NameRevision != expectedNameRevision) return new(SummaryOutcomeKind.Conflict, Code: "NodeNameChanged");
            string name;
            try { name = KnowledgeSummaryProtocol.ParseComplete(KnowledgeSummaryProtocol.Serialize(new(displayName, "name"))).Title; }
            catch (SummaryProtocolException) { return new(SummaryOutcomeKind.Rejected, Code: "InvalidNodeName"); }
            if (state.Node.DisplayName == name && state.Node.NameIsManual) return new(SummaryOutcomeKind.NoChange, state.Node);
            using var q = ChatCommand(c, t, "UPDATE summary_node SET display_name=$name,name_is_manual=1,name_revision=$revision WHERE id=$id",
                ("$name", name), ("$revision", checked(state.Node.NameRevision + 1)), ("$id", nodeId)); q.ExecuteNonQuery();
            return new(SummaryOutcomeKind.Applied, ReadSummaryState(c, t, nodeId)!.Node);
        });
}
