using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>Resource rows, derivatives, chunks with their keyword index, and draft attachments (migrations v15–v16; architecture §15.2).</summary>
internal sealed class SqliteResourceStore(SqliteDatabase database) : IResourceStore
{
    private const string ResourceColumns =
        "r.id, r.workspace_id, r.conversation_id, r.sha256, r.media_type, r.byte_size, r.origin, r.name, r.source_url, r.provenance, r.created_at";

    /// <summary>The resource with its text derivative and whether that text was cut.</summary>
    private const string StoredSelect = $"""
        SELECT {ResourceColumns}, d.text, d.truncated
        FROM resource r LEFT JOIN resource_derivative d ON d.resource_id = r.id AND d.kind = 'text'
        """;

    public Task InsertAsync(Resource resource, IReadOnlyList<ResourceDerivative> derivatives, IReadOnlyList<ResourceChunk> chunks, DraftSlot? draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(derivatives);
        ArgumentNullException.ThrowIfNull(chunks);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                connection.Execute(transaction, """
                    INSERT INTO resource (id, workspace_id, conversation_id, sha256, media_type, byte_size, origin, name, source_url, provenance, created_at)
                    VALUES ($id, $ws, $conv, $hash, $type, $size, $origin, $name, $url, $provenance, $created);
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
                    ("$provenance", SqlNames.Of(resource.Provenance)),
                    ("$created", resource.CreatedAt.Ms()));
                foreach (var form in derivatives)
                {
                    InsertDerivative(connection, transaction, resource.Id, form, resource.CreatedAt);
                }

                var text = derivatives.FirstOrDefault(d => d.Kind == DerivativeKind.Text)?.Text;
                foreach (var chunk in chunks)
                {
                    if (text is null || chunk.End > text.Length)
                    {
                        throw new ArgumentException("Chunks must lie within the resource's text.", nameof(chunks));
                    }

                    connection.Execute(transaction, """
                        INSERT INTO resource_chunk (resource_id, ordinal, start_char, end_char, estimated_tokens) VALUES ($id, $ord, $start, $end, $tokens);
                        INSERT INTO resource_chunk_fts (rowid, terms) VALUES (last_insert_rowid(), $terms);
                        """,
                        ("$id", resource.Id.ToString()),
                        ("$ord", chunk.Ordinal),
                        ("$start", chunk.Start),
                        ("$end", chunk.End),
                        ("$tokens", chunk.EstimatedTokens),
                        ("$terms", SearchTerms.Of(text[chunk.Start..chunk.End])));
                }

                if (draft is not null)
                {
                    connection.Execute(transaction, """
                        INSERT INTO draft_attachment (resource_id, workspace_id, conversation_id, slot_key, ordinal, created_at)
                        VALUES ($id, $ws, $conv, $slot,
                                (SELECT ifnull(max(ordinal) + 1, 0) FROM draft_attachment
                                 WHERE workspace_id = $ws AND conversation_id IS $conv AND slot_key = $slot),
                                $created);
                        """,
                        ("$id", resource.Id.ToString()),
                        ("$ws", draft.Workspace.ToString()),
                        ("$conv", draft.Conversation?.ToString()),
                        ("$slot", draft.SlotKey),
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

    public Task<IReadOnlyList<StoredResource>> GetDraftAsync(DraftSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return database.ReadAsync<IReadOnlyList<StoredResource>>(
            connection =>
            {
                using var command = connection.Command(null, $"""
                    {StoredSelect}
                    JOIN draft_attachment a ON a.resource_id = r.id
                    WHERE a.workspace_id = $ws AND a.conversation_id IS $conv AND a.slot_key = $slot
                    ORDER BY a.ordinal;
                    """, ("$ws", slot.Workspace.ToString()), ("$conv", slot.Conversation?.ToString()), ("$slot", slot.SlotKey));
                using var reader = command.ExecuteReader();
                var result = new List<StoredResource>();
                while (reader.Read())
                {
                    result.Add(ReadStored(reader));
                }

                return result;
            },
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<ResourceId, IReadOnlyList<ResourceChunk>>> GetChunksAsync(IReadOnlyCollection<ResourceId> resources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return database.ReadAsync<IReadOnlyDictionary<ResourceId, IReadOnlyList<ResourceChunk>>>(
            connection =>
            {
                var result = new Dictionary<ResourceId, IReadOnlyList<ResourceChunk>>();
                foreach (var id in resources.Distinct())
                {
                    using var command = connection.Command(null,
                        "SELECT ordinal, start_char, end_char, estimated_tokens FROM resource_chunk WHERE resource_id = $id ORDER BY ordinal;",
                        ("$id", id.ToString()));
                    using var reader = command.ExecuteReader();
                    var chunks = new List<ResourceChunk>();
                    while (reader.Read())
                    {
                        chunks.Add(new ResourceChunk(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
                    }

                    result[id] = chunks;
                }

                return result;
            },
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<ResourceId, IReadOnlyList<int>>> SearchChunksAsync(IReadOnlyCollection<ResourceId> resources, string ftsQuery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentException.ThrowIfNullOrEmpty(ftsQuery);
        return database.ReadAsync<IReadOnlyDictionary<ResourceId, IReadOnlyList<int>>>(
            connection =>
            {
                var result = new Dictionary<ResourceId, IReadOnlyList<int>>();
                foreach (var id in resources.Distinct())
                {
                    using var command = connection.Command(null, """
                        SELECT c.ordinal
                        FROM resource_chunk_fts f JOIN resource_chunk c ON c.rowid = f.rowid
                        WHERE resource_chunk_fts MATCH $query AND c.resource_id = $id
                        ORDER BY bm25(resource_chunk_fts), c.ordinal;
                        """, ("$query", ftsQuery), ("$id", id.ToString()));
                    using var reader = command.ExecuteReader();
                    var ordinals = new List<int>();
                    while (reader.Read())
                    {
                        ordinals.Add(reader.GetInt32(0));
                    }

                    if (ordinals.Count > 0)
                    {
                        result[id] = ordinals;
                    }
                }

                return result;
            },
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<ResourceId, StoredDerivative>> GetDerivativesAsync(
        IReadOnlyCollection<ResourceId> resources, DerivativeKind kind, string? promptVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return database.ReadAsync<IReadOnlyDictionary<ResourceId, StoredDerivative>>(
            connection =>
            {
                var result = new Dictionary<ResourceId, StoredDerivative>();
                foreach (var id in resources.Distinct())
                {
                    using var command = connection.Command(null, """
                        SELECT kind, text, producer, prompt_version, blob_sha256, estimated_tokens, truncated, created_at
                        FROM resource_derivative
                        WHERE resource_id = $id AND kind = $kind AND prompt_version IS $version
                        ORDER BY created_at DESC, id DESC LIMIT 1;
                        """, ("$id", id.ToString()), ("$kind", SqlNames.Of(kind)), ("$version", promptVersion));
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        var blob = reader.NullableString(4);
                        var derivative = new ResourceDerivative(
                            SqlNames.ParseDerivativeKind(reader.GetString(0)),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.NullableString(3),
                            blob is null ? null : ContentHash.Parse(blob),
                            reader.IsDBNull(5) ? null : reader.GetInt32(5),
                            reader.GetInt64(6) != 0);
                        result[id] = new StoredDerivative(id, derivative, reader.Time(7));
                    }
                }

                return result;
            },
            cancellationToken);
    }

    public Task AddDerivativeAsync(ResourceId resource, ResourceDerivative derivative, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(derivative);
        return database.WriteAsync((connection, transaction) => InsertDerivative(connection, transaction, resource, derivative, at), cancellationToken);
    }

    public Task RecordTaskInputAsync(GenerationTaskId task, IReadOnlyList<ResourceInput> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var json = System.Text.Json.JsonSerializer.Serialize(inputs.Select(i => new InputRow(i.Resource.ToString(), i.Name, SqlNames.Of(i.Form), [.. i.Chunks], i.ChunkCount)), InputJson);
        return database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction, "UPDATE generation_task SET input_resources = $json WHERE id = $id;", ("$json", json), ("$id", task.ToString())),
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<MessageId, IReadOnlyList<ResourceInput>>> ReducedInputsAsync(ConversationId conversation, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyDictionary<MessageId, IReadOnlyList<ResourceInput>>>(
            connection =>
            {
                using var command = connection.Command(null, """
                    SELECT target_message_id, input_resources FROM generation_task
                    WHERE conversation_id = $conv AND kind = 'answer' AND input_resources <> '[]' AND target_message_id IS NOT NULL;
                    """, ("$conv", conversation.ToString()));
                using var reader = command.ExecuteReader();
                var result = new Dictionary<MessageId, IReadOnlyList<ResourceInput>>();
                while (reader.Read())
                {
                    var rows = System.Text.Json.JsonSerializer.Deserialize<List<InputRow>>(reader.GetString(1), InputJson) ?? [];
                    var inputs = rows.Select(r => new ResourceInput(ResourceId.Parse(r.Resource), r.Name, SqlNames.ParseResourceForm(r.Form), r.Chunks ?? [], r.Of)).ToList();
                    if (inputs.Any(i => i.Form is Core.Context.ResourceForm.Summary or Core.Context.ResourceForm.Parts or Core.Context.ResourceForm.ImageOmitted))
                    {
                        result[MessageId.Parse(reader.GetString(0))] = inputs;
                    }
                }

                return result;
            },
            cancellationToken);

    private static readonly System.Text.Json.JsonSerializerOptions InputJson = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };

    private sealed record InputRow(string Resource, string Name, string Form, List<int>? Chunks, int Of);

    public Task<int> DeleteAbandonedAsync(CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction, """
                DELETE FROM resource
                WHERE NOT EXISTS (SELECT 1 FROM message_block b WHERE b.resource_id = resource.id)
                  AND NOT EXISTS (SELECT 1 FROM draft_attachment a WHERE a.resource_id = resource.id);
                """),
            cancellationToken);

    private static int InsertDerivative(SqliteConnection connection, SqliteTransaction transaction, ResourceId resource, ResourceDerivative form, DateTimeOffset at) =>
        connection.Execute(transaction, """
            INSERT INTO resource_derivative (resource_id, kind, producer, prompt_version, text, blob_sha256, estimated_tokens, truncated, created_at)
            VALUES ($id, $kind, $producer, $version, $text, $blob, $tokens, $truncated, $created)
            ON CONFLICT DO NOTHING;
            """,
            ("$id", resource.ToString()),
            ("$kind", SqlNames.Of(form.Kind)),
            ("$producer", form.Producer),
            ("$version", form.PromptVersion),
            ("$text", form.Text),
            ("$blob", form.BlobHash?.Hex),
            ("$tokens", form.EstimatedTokens),
            ("$truncated", form.Truncated ? 1 : 0),
            ("$created", at.Ms()));

    internal static StoredResource? Get(SqliteConnection connection, SqliteTransaction? transaction, ResourceId id)
    {
        using var command = connection.Command(transaction, $"{StoredSelect} WHERE r.id = $id;", ("$id", id.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadStored(reader) : null;
    }

    private static StoredResource ReadStored(SqliteDataReader reader) =>
        new(Read(reader), reader.NullableString(11), !reader.IsDBNull(12) && reader.GetInt64(12) != 0);

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
            SqlNames.ParseProvenance(reader.GetString(first + 9)),
            reader.Time(first + 10));
    }

    /// <summary>
    /// Makes a message of <paramref name="conversation"/> able to refer to <paramref name="resource"/>: the resource must be in
    /// the conversation's workspace, and one not yet in any conversation joins this one. A resource of another conversation
    /// is refused (a resource belongs to one conversation; the same content elsewhere is a new resource sharing the original).
    /// The resource leaves the draft it was in: it is sent now. Returns the resource's display name.
    /// </summary>
    internal static string Adopt(SqliteConnection connection, SqliteTransaction transaction, ConversationId conversation, ResourceId resource)
    {
        string? owner;
        string name;
        using (var command = connection.Command(transaction, """
            SELECT r.conversation_id, r.workspace_id = c.workspace_id, r.name
            FROM resource r, conversation c
            WHERE r.id = $r AND c.id = $c;
            """, ("$r", resource.ToString()), ("$c", conversation.ToString())))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new InvalidOperationException($"Resource {resource} (or conversation {conversation}) does not exist.");
            }

            if (reader.GetInt64(1) == 0)
            {
                throw new InvalidOperationException($"Resource {resource} belongs to another workspace than conversation {conversation}.");
            }

            owner = reader.NullableString(0);
            name = reader.NullableString(2) ?? ContentBlocks.PastedTextName;
        }

        if (owner is null)
        {
            connection.Execute(transaction, "UPDATE resource SET conversation_id = $c WHERE id = $r;", ("$r", resource.ToString()), ("$c", conversation.ToString()));
        }
        else if (owner != conversation.ToString())
        {
            throw new InvalidOperationException($"Resource {resource} belongs to another conversation.");
        }

        connection.Execute(transaction, "DELETE FROM draft_attachment WHERE resource_id = $r;", ("$r", resource.ToString()));
        return name;
    }
}

/// <summary>What a delete takes of resources: the attachments sent in its scope, and every original file its rows name.</summary>
internal static class SqliteResourceScope
{
    /// <summary>Attachments sent in the conversations (attachment blocks), counted for the confirmation (requirements §10.4.3).</summary>
    public static int Attachments(SqliteConnection connection, SqliteTransaction? transaction, string scopeSql, params (string Name, object? Value)[] parameters) =>
        (int)connection.Scalar<long>(transaction, $"""
            SELECT count(*) FROM message_block b JOIN message m ON m.id = b.message_id
            WHERE b.resource_id IS NOT NULL AND b.kind = 'attachment' AND m.conversation_id IN ({scopeSql});
            """, parameters);

    /// <summary>Hashes of the originals and binary derivatives of resources whose conversation is in the scope.</summary>
    public static IReadOnlyList<ContentHash> Hashes(SqliteConnection connection, SqliteTransaction? transaction, string resourceFilter, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(transaction, $"""
            SELECT r.sha256 FROM resource r WHERE {resourceFilter}
            UNION
            SELECT d.blob_sha256 FROM resource_derivative d JOIN resource r ON r.id = d.resource_id WHERE d.blob_sha256 IS NOT NULL AND {resourceFilter};
            """, parameters);
        using var reader = command.ExecuteReader();
        var hashes = new List<ContentHash>();
        while (reader.Read())
        {
            hashes.Add(ContentHash.Parse(reader.GetString(0)));
        }

        return hashes;
    }
}
