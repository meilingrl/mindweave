using Microsoft.Data.Sqlite;
using Mindweave.Application.Exchange;
using Mindweave.Application.Import;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>Writes what a shared file adds, in one transaction (docs/design/sharing-import.md §2).</summary>
internal sealed class SqliteImportStore(SqliteDatabase database) : IImportStore
{
    public Task<IReadOnlyDictionary<ConversationId, WorkspaceId>> HoldingsAsync(IReadOnlyCollection<ConversationId> conversations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        return database.ReadAsync<IReadOnlyDictionary<ConversationId, WorkspaceId>>(connection => Holdings(connection, null, conversations), cancellationToken);
    }

    public Task<ImportResult> ImportAsync(ExchangeDocument document, ImportTarget target, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(target);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                if (target.Create is null && connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM workspace WHERE id = $ws);", ("$ws", target.Id.ToString())) == 0)
                {
                    return ImportResult.Nothing(ImportStatus.WorkspaceMissing);
                }

                // Compared again here: what was shown may have changed since.
                var holdings = Holdings(connection, transaction, ImportRules.Compared(document));
                if (ImportRules.Plan(document, holdings, target.Id) is not { } plan)
                {
                    return ImportResult.Nothing(ImportStatus.DoesNotFit);
                }

                if (plan.New.Count == 0)
                {
                    return ImportResult.Nothing(ImportStatus.NothingNew);
                }

                if (target.Create is { } workspace)
                {
                    SqliteWorkspaceStore.Insert(connection, transaction, workspace);
                }

                var import = Guid.CreateVersion7().ToString("D");
                connection.Execute(transaction,
                    "INSERT INTO import (id, title, kind, imported_at) VALUES ($id, $title, $kind, $at);",
                    ("$id", import), ("$title", ImportRules.OneLine(document.Title)), ("$kind", document.Kind), ("$at", at.Ms()));
                foreach (var conversation in plan.New)
                {
                    Insert(connection, transaction, conversation, target.Id, import);
                }

                var created = plan.New.Select(c => c.Id).ToHashSet();
                return new ImportResult(
                    ImportStatus.Imported,
                    [.. plan.New.Select(c => c.Id)],
                    [.. plan.New.Select(c => c.Parent).OfType<ConversationId>().Where(p => !created.Contains(p)).Distinct()],
                    (plan.New.FirstOrDefault(c => c.Parent is null) ?? plan.New[0]).Id);
            },
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<ConversationId, ImportMark>> MarksAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyDictionary<ConversationId, ImportMark>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT c.id, i.title, i.imported_at
                    FROM import_conversation r JOIN conversation c ON c.id = r.conversation_id JOIN import i ON i.id = r.import_id
                    WHERE c.workspace_id = $ws;
                    """, ("$ws", workspace.ToString()));
                using var reader = command.ExecuteReader();
                var marks = new Dictionary<ConversationId, ImportMark>();
                while (reader.Read())
                {
                    marks[ConversationId.Parse(reader.GetString(0))] = new ImportMark(reader.GetString(1), reader.Time(2));
                }

                return marks;
            },
            cancellationToken);

    /// <summary>A conversation as a structure-only share carries it: its identity, its name, its start and its parent.</summary>
    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, ExchangeConversation conversation, WorkspaceId workspace, string import)
    {
        // A name from a file is one the user did not write, so a title of the user's own replaces it like any automatic one.
        var title = ImportRules.OneLine(conversation.Name ?? string.Empty) is { Length: > 0 } name ? name : null;
        SqliteConversationStore.InsertConversation(connection, transaction, new Conversation(
            conversation.Id, workspace, title, title is null ? TitleSource.None : TitleSource.Auto, conversation.StartedAt, conversation.StartedAt));
        if (conversation.Parent is { } parent)
        {
            SqliteBranchStore.InsertWithoutSource(connection, transaction, conversation.Id, parent, conversation.StartedAt);
        }

        connection.Execute(transaction,
            "INSERT INTO import_conversation (conversation_id, import_id) VALUES ($id, $import);",
            ("$id", conversation.Id.ToString()), ("$import", import));
    }

    private static Dictionary<ConversationId, WorkspaceId> Holdings(SqliteConnection connection, SqliteTransaction? transaction, IEnumerable<ConversationId> conversations)
    {
        var holdings = new Dictionary<ConversationId, WorkspaceId>();
        foreach (var conversation in conversations)
        {
            if (connection.Scalar<string>(transaction, "SELECT workspace_id FROM conversation WHERE id = $id;", ("$id", conversation.ToString())) is { } workspace)
            {
                holdings[conversation] = WorkspaceId.Parse(workspace);
            }
        }

        return holdings;
    }
}
