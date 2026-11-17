using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>Resource rows and derivatives (migration v14; docs/design/resources.md).</summary>
internal sealed class SqliteResourceStore(SqliteDatabase database) : IResourceStore
{
    private const string ResourceColumns =
        "r.id, r.workspace_id, r.conversation_id, r.sha256, r.media_type, r.byte_size, r.origin, r.name, r.source_url, r.trusted, r.created_at";

    public Task InsertAsync(Resource resource, IReadOnlyList<ResourceDerivative> derivatives, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(derivatives);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                connection.Execute(transaction, """
                    INSERT INTO resource (id, workspace_id, conversation_id, sha256, media_type, byte_size, origin, name, source_url, trusted, created_at)
                    VALUES ($id, $ws, $conv, $hash, $type, $size, $origin, $name, $url, $trusted, $created);
                    """,
                    ("$id", resource.Id.ToString()),
                    ("$ws", resource.WorkspaceId.ToString()),
                    ("$conv", resource.ConversationId?.ToString()),
                    ("$hash", resource.Hash.Hex),
                    ("$type", resource.MediaType),
                    ("$size", resource.Size),
                    ("$origin", SqlNames.Of(resource.Origin)),
                    ("$name", resource.Name),
                    ("$url", resource.SourceUrl?.ToString()),
                    ("$trusted", resource.Trusted ? 1 : 0),
                    ("$created", resource.CreatedAt.Ms()));
                foreach (var form in derivatives)
                {
                    connection.Execute(transaction, """
                        INSERT INTO resource_derivative (resource_id, kind, text, producer, created_at)
                        VALUES ($id, $kind, $text, $producer, $created);
                        """,
                        ("$id", resource.Id.ToString()),
                        ("$kind", SqlNames.Of(form.Kind)),
                        ("$text", form.Text),
                        ("$producer", form.Producer),
                        ("$created", resource.CreatedAt.Ms()));
                }

                return 0;
            },
            cancellationToken);
    }

    public Task<StoredResource?> GetAsync(ResourceId id, CancellationToken cancellationToken) =>
        database.ReadAsync(connection => Get(connection, null, id), cancellationToken);

    public Task<ResourceDeleteStatus> DeleteUnattachedAsync(ResourceId id, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                var rid = ("$id", (object?)id.ToString());
                if (connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM resource WHERE id = $id);", rid) == 0)
                {
                    return ResourceDeleteStatus.Missing;
                }

                if (connection.Scalar<long>(transaction, "SELECT EXISTS (SELECT 1 FROM message_block WHERE resource_id = $id);", rid) != 0)
                {
                    return ResourceDeleteStatus.Attached;
                }

                connection.Execute(transaction, "DELETE FROM resource WHERE id = $id;", rid);
                return ResourceDeleteStatus.Deleted;
            },
            cancellationToken);

    internal static StoredResource? Get(SqliteConnection connection, SqliteTransaction? transaction, ResourceId id)
    {
        using var command = connection.Command(transaction, $"""
            SELECT {ResourceColumns}, d.text
            FROM resource r LEFT JOIN resource_derivative d ON d.resource_id = r.id AND d.kind = 'text'
            WHERE r.id = $id;
            """, ("$id", id.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new StoredResource(Read(reader), reader.NullableString(11)) : null;
    }

    /// <summary>Reads the eleven <see cref="ResourceColumns"/> starting at <paramref name="first"/>.</summary>
    internal static Resource Read(SqliteDataReader reader, int first = 0)
    {
        var conversation = reader.NullableString(first + 2);
        var url = reader.NullableString(first + 8);
        return new Resource(
            ResourceId.Parse(reader.GetString(first)),
            WorkspaceId.Parse(reader.GetString(first + 1)),
            conversation is null ? null : ConversationId.Parse(conversation),
            ContentHash.Parse(reader.GetString(first + 3)),
            reader.GetString(first + 4),
            reader.GetInt64(first + 5),
            SqlNames.ParseResourceOrigin(reader.GetString(first + 6)),
            reader.NullableString(first + 7),
            url is null ? null : new Uri(url, UriKind.Absolute),
            reader.GetInt64(first + 9) != 0,
            reader.Time(first + 10));
    }

    /// <summary>
    /// Makes a message of <paramref name="conversation"/> able to refer to <paramref name="resource"/>: the resource must be in
    /// the conversation's workspace, and one not yet in any conversation joins this one. A resource of another conversation
    /// is refused (a resource belongs to one conversation; the same content elsewhere is a new resource sharing the original).
    /// Returns the resource's display name.
    /// </summary>
    internal static string Adopt(SqliteConnection connection, SqliteTransaction transaction, ConversationId conversation, ResourceId resource)
    {
        using var command = connection.Command(transaction, """
            SELECT r.conversation_id, r.workspace_id = c.workspace_id, r.name
            FROM resource r, conversation c
            WHERE r.id = $r AND c.id = $c;
            """, ("$r", resource.ToString()), ("$c", conversation.ToString()));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Resource {resource} (or conversation {conversation}) does not exist.");
        }

        if (reader.GetInt64(1) == 0)
        {
            throw new InvalidOperationException($"Resource {resource} belongs to another workspace than conversation {conversation}.");
        }

        var owner = reader.NullableString(0);
        var name = reader.NullableString(2) ?? ContentBlocks.PastedTextName;
        if (owner is null)
        {
            connection.Execute(transaction, "UPDATE resource SET conversation_id = $c WHERE id = $r;", ("$r", resource.ToString()), ("$c", conversation.ToString()));
        }
        else if (owner != conversation.ToString())
        {
            throw new InvalidOperationException($"Resource {resource} belongs to another conversation.");
        }

        return name;
    }
}
