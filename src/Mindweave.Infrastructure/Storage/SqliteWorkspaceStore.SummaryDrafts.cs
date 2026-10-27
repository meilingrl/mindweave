using System.Globalization;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    public Task<SummaryEditDraft?> LoadSummaryDraftAsync(Guid nodeId) => ReadSummaryAsync((c, t) => ReadSummaryDraft(c, t, nodeId));
    private static SummaryEditDraft? ReadSummaryDraft(SqliteConnection c, SqliteTransaction t, Guid nodeId)
    {
        using var q = ChatCommand(c, t, "SELECT base_version_id,epoch,revision,title,markdown,is_dirty FROM summary_edit_draft WHERE node_id=$id", ("$id", nodeId));
        using var r = q.ExecuteReader(); return r.Read() ? new(new(nodeId, Guid.Parse(r.GetString(0)), r.GetInt64(1), r.GetInt64(2)), SummaryString(r, 3), SummaryString(r, 4), r.GetBoolean(5)) : null;
    }
    private static void WriteSummaryDraft(SqliteConnection c, SqliteTransaction t, SummaryEditDraft draft)
    {
        using var q = ChatCommand(c, t, """
            INSERT INTO summary_edit_draft VALUES($id,$base,$epoch,$revision,$title,$body,$dirty)
            ON CONFLICT(node_id) DO UPDATE SET base_version_id=$base,epoch=$epoch,revision=$revision,title=$title,markdown=$body,is_dirty=$dirty
            """, ("$id", draft.Token.NodeId), ("$base", draft.Token.BaseVersionId), ("$epoch", draft.Token.Epoch), ("$revision", draft.Token.Revision),
            ("$title", draft.Title), ("$body", draft.Markdown), ("$dirty", draft.IsDirty)); q.ExecuteNonQuery();
    }
    private static SummaryEditDraft ClearSummaryDraft(SqliteConnection c, SqliteTransaction t, SummaryEditDraft draft, Guid baseline)
    {
        var cleared = new SummaryEditDraft(new(draft.Token.NodeId, baseline, checked(draft.Token.Epoch + 1), checked(draft.Token.Revision + 1)), null, null, false);
        WriteSummaryDraft(c, t, cleared); return cleared;
    }
    public Task<SummaryOutcome<SummaryEditDraft>> BeginSummaryEditAsync(SummaryNodeToken expected) => WriteAsync<SummaryOutcome<SummaryEditDraft>>((c, t) =>
    {
        var state = ReadSummaryState(c, t, expected.NodeId); if (state is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryNodeMissing");
        var decision = SummaryVersionPolicy.BeginEdit(state, expected.CurrentVersionId, expected);
        if (decision.Kind != SummaryOutcomeKind.Applied) return new(decision.Kind, Code: decision.Code);
        var old = ReadSummaryDraft(c, t, state.Node.Id); var current = ReadSummaryVersion(c, t, state.Node.CurrentVersionId)!;
        var draft = old is { IsDirty: true } ? old with { Token = decision.Value! } : new SummaryEditDraft(decision.Value!, current.Header.Title, current.Markdown, false);
        WriteSummaryDraft(c, t, draft); return new(SummaryOutcomeKind.Applied, draft);
    });
    public Task<SummaryOutcome<SummaryEditDraft>> SaveSummaryDraftAsync(SummaryDraftToken expected, SummaryContent content) => WriteAsync<SummaryOutcome<SummaryEditDraft>>((c, t) =>
    {
        var state = ReadSummaryState(c, t, expected.NodeId); if (state is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryNodeMissing");
        var draft = ReadSummaryDraft(c, t, expected.NodeId);
        if (draft is null || draft.Token != expected || draft.Title is null) return new(SummaryOutcomeKind.Obsolete, Code: "StaleDraftEpochOrRevision");
        if (state.Node.CurrentVersionId != expected.BaseVersionId) return new(SummaryOutcomeKind.Conflict, Code: "DraftBaselineChanged");
        if (content?.Title is null || content.Markdown is null) return new(SummaryOutcomeKind.Rejected, Code: "InvalidDraftContent");
        // Autosave is lossless, including temporarily empty/incomplete titles and Markdown. Only explicit commit validates the protocol.
        var current = ReadSummaryVersion(c, t, expected.BaseVersionId)!;
        var saved = new SummaryEditDraft(expected with { Revision = checked(expected.Revision + 1) }, content.Title, content.Markdown,
            content.Title != current.Header.Title || content.Markdown != current.Markdown);
        WriteSummaryDraft(c, t, saved); return new(SummaryOutcomeKind.Applied, saved);
    });
    public Task<SummaryOutcome<SummaryEditDraft>> DiscardSummaryDraftAsync(SummaryDraftToken expected) => WriteAsync<SummaryOutcome<SummaryEditDraft>>((c, t) =>
    {
        var state = ReadSummaryState(c, t, expected.NodeId); if (state is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryNodeMissing");
        var draft = ReadSummaryDraft(c, t, expected.NodeId);
        if (draft is null || draft.Token != expected || draft.Title is null) return new(SummaryOutcomeKind.Obsolete, Code: "StaleDraftEpochOrRevision");
        return new(SummaryOutcomeKind.Applied, ClearSummaryDraft(c, t, draft, state.Node.CurrentVersionId));
    });
    public Task<SummaryOutcome<SummaryVersion>> CommitSummaryDraftAsync(SummaryNodeToken expectedNode, SummaryDraftToken expectedDraft) => WriteAsync<SummaryOutcome<SummaryVersion>>((c, t) =>
    {
        var state = ReadSummaryState(c, t, expectedNode.NodeId); if (state is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryNodeMissing");
        var draft = ReadSummaryDraft(c, t, state.Node.Id);
        if (draft is null || draft.Token != expectedDraft) return new(SummaryOutcomeKind.Obsolete, Code: "StaleDraftEpochOrRevision");
        var current = ReadSummaryVersion(c, t, state.Node.CurrentVersionId)!;
        var decision = SummaryVersionPolicy.CommitDraft(state, current, draft, expectedNode);
        if (decision.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)) return new(decision.Kind, Code: decision.Code);
        if (decision.Kind == SummaryOutcomeKind.NoChange)
        {
            ClearSummaryDraft(c, t, draft, state.Node.CurrentVersionId); return new(SummaryOutcomeKind.NoChange, current);
        }
        var id = Guid.NewGuid(); var ordinal = NextSummaryOrdinal(c, t, state.Node.Id); var content = decision.Value!.Content;
        using (var q = ChatCommand(c, t, "INSERT INTO summary_version_identity VALUES($id,$node,$ordinal,'Manual',$now)", ("$id", id), ("$node", state.Node.Id), ("$ordinal", ordinal), ("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)))) q.ExecuteNonQuery();
        using (var q = ChatCommand(c, t, "INSERT INTO summary_version VALUES($id,$node,$title,$body,$base,NULL,NULL,$hash)", ("$id", id), ("$node", state.Node.Id), ("$title", content.Title), ("$body", content.Markdown), ("$base", current.Header.Identity.Id), ("$hash", SummaryContentHash(content)))) q.ExecuteNonQuery();
        using (var q = ChatCommand(c, t, "INSERT INTO summary_version_source SELECT $id,coverage_json FROM summary_version_source WHERE version_id=$base", ("$id", id), ("$base", current.Header.Identity.Id))) q.ExecuteNonQuery();
        using (var q = ChatCommand(c, t, "INSERT INTO summary_version_message SELECT $id,scope,ordinal,message_id,conversation_id FROM summary_version_message WHERE version_id=$base", ("$id", id), ("$base", current.Header.Identity.Id))) q.ExecuteNonQuery();
        using (var q = ChatCommand(c, t, """
            INSERT INTO summary_read_state(version_id,epoch,revision,is_read,suppress_automatic,text_hash,text_length,seen_json,visible_dwell_ticks,observation_sequence,was_active_visible)
            VALUES($id,0,0,1,0,'',0,'[]',0,0,0)
            """, ("$id", id))) q.ExecuteNonQuery();
        ChangeSummaryCurrent(c, t, state, id, state.Node.CandidateVersionId);
        return new(SummaryOutcomeKind.Applied, ReadSummaryVersion(c, t, id));
    });
}
