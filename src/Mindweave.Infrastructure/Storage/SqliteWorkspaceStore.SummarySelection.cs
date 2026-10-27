using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Navigation;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    public Task<SummarySelectionState> LoadSummarySelectionAsync() => ReadSummaryAsync((c, t) =>
    {
        var saved = ReadSummarySelection(c, t);
        if (saved.Selection is not { } selection) return saved;
        var state = ReadSummaryState(c, t, selection.NodeId);
        if (state is null || state.Node.WorkspaceId != selection.WorkspaceId) return saved with { Selection = null };
        return state.Versions.Any(v => v.Identity.Id == selection.VersionId) ? saved : saved with { Selection = selection with { VersionId = state.Node.CurrentVersionId } };
    });
    private static SummarySelectionState ReadSummarySelection(SqliteConnection c, SqliteTransaction t)
    {
        using var q = ChatCommand(c, t, "SELECT workspace_id,node_id,version_id,revision FROM summary_selection WHERE singleton=1");
        using var r = q.ExecuteReader(); return r.Read() ? new(r.IsDBNull(0) ? null : new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2))), r.GetInt64(3)) : new(null, 0);
    }
    private static void WriteSummarySelection(SqliteConnection c, SqliteTransaction t, SummarySelectionState state)
    {
        using var q = ChatCommand(c, t, """
            INSERT INTO summary_selection VALUES(1,$workspace,$node,$version,$revision)
            ON CONFLICT(singleton) DO UPDATE SET workspace_id=$workspace,node_id=$node,version_id=$version,revision=$revision
            """, ("$workspace", state.Selection?.WorkspaceId), ("$node", state.Selection?.NodeId), ("$version", state.Selection?.VersionId), ("$revision", state.Revision)); q.ExecuteNonQuery();
    }
    // Also used by ordinary navigation. Table detection preserves S0 custom schema/test overrides.
    private static void InvalidateSummarySelection(SqliteConnection c, SqliteTransaction t)
    {
        using var exists = ChatCommand(c, t, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='summary_selection'");
        if (exists.ExecuteScalar() is null) return;
        var previous = ReadSummarySelection(c, t); WriteSummarySelection(c, t, new(null, checked(previous.Revision + 1)));
    }
    public Task<SummaryOutcome<SummarySelectionState>> SaveSummarySelectionAsync(SummarySelection? selection, long expectedRevision) =>
        WriteAsync<SummaryOutcome<SummarySelectionState>>((c, t) =>
        {
            var previous = ReadSummarySelection(c, t); if (previous.Revision != expectedRevision) return new(SummaryOutcomeKind.Conflict, Code: "SummarySelectionChanged");
            if (selection is not null)
            {
                using var valid = ChatCommand(c, t, "SELECT 1 FROM summary_node n JOIN summary_version v ON v.node_id=n.id WHERE n.id=$node AND n.workspace_id=$workspace AND v.id=$version",
                    ("$node", selection.NodeId), ("$workspace", selection.WorkspaceId), ("$version", selection.VersionId));
                if (valid.ExecuteScalar() is null) return new(SummaryOutcomeKind.Rejected, Code: "InvalidSummarySelection");
            }
            var result = new SummarySelectionState(selection, checked(previous.Revision + 1)); WriteSummarySelection(c, t, result);
            Guid? workspace = selection?.WorkspaceId ?? previous.Selection?.WorkspaceId;
            if (workspace is null)
            {
                using var q = ChatCommand(c, t, "SELECT workspace_id FROM app_selection WHERE singleton=1");
                if (q.ExecuteScalar() is string id) workspace = Guid.Parse(id);
            }
            if (workspace is { } target) SaveSelection(c, t, new(target, MainArea.Network), invalidateSummarySelection: false);
            return new(SummaryOutcomeKind.Applied, result);
        });
    private static void RepairSummarySelection(SqliteConnection c, SqliteTransaction t, SummaryNode node, ImmutableArray<Guid> removed, Guid? fallback)
    {
        var previous = ReadSummarySelection(c, t);
        if (previous.Selection is not { } selection || selection.NodeId != node.Id || !removed.Contains(selection.VersionId)) return;
        var next = fallback is { } id ? new SummarySelection(node.WorkspaceId, node.Id, id) : null;
        WriteSummarySelection(c, t, new(next, checked(previous.Revision + 1)));
        SaveSelection(c, t, new(node.WorkspaceId, MainArea.Network), invalidateSummarySelection: false);
    }
}
