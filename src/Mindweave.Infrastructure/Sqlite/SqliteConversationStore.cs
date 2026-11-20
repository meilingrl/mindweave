using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteConversationStore(SqliteDatabase database) : IConversationStore
{
    private const string MessageColumns =
        "id, conversation_id, seq, role, state, revision, supersedes_id, error_kind, error_detail, created_at, completed_at";

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<ConversationSummary>>(
            connection =>
            {
                using var command = connection.Command(null, $"""
                    SELECT c.id, c.workspace_id, c.title, c.title_source, c.created_at, c.updated_at,
                           (SELECT b.text FROM message m JOIN message_block b ON b.message_id = m.id AND b.ordinal = 0
                            WHERE m.conversation_id = c.id AND m.role = 'user' ORDER BY m.seq LIMIT 1),
                           (SELECT state FROM message WHERE conversation_id = c.id AND role = 'assistant' ORDER BY seq DESC LIMIT 1),
                           s.parent_id, s.quote,
                           EXISTS (SELECT 1 FROM conversation_archive a WHERE a.conversation_id = c.id),
                           {SqliteArchive.HiddenCondition("c.id")}
                    FROM conversation c LEFT JOIN branch_source s ON s.child_id = c.id
                    WHERE c.workspace_id = $ws
                    ORDER BY c.updated_at DESC, c.rowid DESC;
                    """, ("$ws", workspace.ToString()));
                using var reader = command.ExecuteReader();
                var list = new List<ConversationSummary>();
                while (reader.Read())
                {
                    var last = reader.NullableString(7);
                    var parent = reader.NullableString(8);
                    list.Add(new ConversationSummary(
                        ReadConversation(reader),
                        reader.NullableString(6),
                        last is null ? null : SqlNames.ParseMessageState(last),
                        parent is null ? null : ConversationId.Parse(parent),
                        reader.NullableString(9),
                        IsArchived: reader.GetInt64(10) != 0,
                        IsHidden: reader.GetInt64(11) != 0));
                }

                return list;
            },
            cancellationToken);

    public Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken) =>
        database.ReadAsync(connection => GetConversation(connection, null, id), cancellationToken);

    public Task<IReadOnlyList<Message>> GetMessagesAsync(ConversationId id, CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<Message>>(connection => GetMessages(connection, null, id), cancellationToken);

    public Task CreateAsync(Conversation conversation, Message question, Message answer, GenerationTaskRecord task, StartingRecommendation? from, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                connection.Execute(transaction, """
                    INSERT INTO conversation (id, workspace_id, title, title_source, created_at, updated_at)
                    VALUES ($id, $ws, $title, $source, $created, $updated);
                    """,
                    ("$id", conversation.Id.ToString()),
                    ("$ws", conversation.WorkspaceId.ToString()),
                    ("$title", conversation.Title),
                    ("$source", SqlNames.Of(conversation.TitleSource)),
                    ("$created", conversation.CreatedAt.Ms()),
                    ("$updated", conversation.UpdatedAt.Ms()));
                InsertMessage(connection, transaction, question with { Seq = 0 });
                InsertMessage(connection, transaction, answer with { Seq = 1 });
                InsertTask(connection, transaction, task);
                if (from is not null)
                {
                    // Requirements §10.3.5: a basis deleted since the page showed it is not remembered.
                    connection.Execute(transaction, """
                        INSERT INTO starting_recommendation (conversation_id, recommendation_id, kind, basis_node_id, applied_at, created_at)
                        VALUES ($id, $rec, $kind, (SELECT id FROM summary_node WHERE id = $basis AND workspace_id = $ws), NULL, $created);
                        """,
                        ("$id", conversation.Id.ToString()),
                        ("$ws", conversation.WorkspaceId.ToString()),
                        ("$rec", from.Recommendation.ToString()),
                        ("$kind", from.Kind == RecommendationKind.Node ? "node" : "question"),
                        ("$basis", from.Basis?.ToString()),
                        ("$created", conversation.CreatedAt.Ms()));
                }

                return 0;
            },
            cancellationToken);
    }

    public Task<(Message Question, Message Answer)> AppendQuestionAsync(Message question, Message answer, GenerationTaskRecord task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                SqliteArchive.RequireOpenForWork(connection, transaction, question.ConversationId);
                ThrowIfUnfinished(connection, transaction, question.ConversationId);
                var next = NextSeq(connection, transaction, question.ConversationId);
                var storedQuestion = question with { Seq = next };
                var storedAnswer = answer with { Seq = next + 1 };
                InsertMessage(connection, transaction, storedQuestion);
                InsertMessage(connection, transaction, storedAnswer);
                InsertTask(connection, transaction, task);
                Touch(connection, transaction, question.ConversationId, question.CreatedAt);
                return (storedQuestion, storedAnswer);
            },
            cancellationToken);
    }

    public Task<Message> AddRetryAsync(Message answer, GenerationTaskRecord task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                // Re-checked inside the transaction: only the last, finished answer of an open conversation may be superseded.
                SqliteArchive.RequireOpenForWork(connection, transaction, answer.ConversationId);
                var messages = GetMessages(connection, transaction, answer.ConversationId);
                if (answer.Supersedes is not { } replaced || !ConversationRules.CanRetry(messages, replaced))
                {
                    throw new AnswerInProgressException("Only the last finished answer can be retried.");
                }

                var stored = answer with { Seq = messages[^1].Seq + 1 };
                InsertMessage(connection, transaction, stored);
                InsertTask(connection, transaction, task);
                Touch(connection, transaction, answer.ConversationId, answer.CreatedAt);
                return stored;
            },
            cancellationToken);
    }

    public Task AddTaskAsync(GenerationTaskRecord task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                // A hidden conversation gets no answer or summary (D-021), and no title either: archiving cancelled its
                // titles, and one scheduled just after must not slip in. Naming concerns a summary node, which stays
                // usable when its source is archived. A deleted conversation is reported as such, not as a failed key.
                if (task.Kind == GenerationTaskKind.Naming)
                {
                    SqliteArchive.RequireConversation(connection, transaction, task.ConversationId);
                }
                else
                {
                    SqliteArchive.RequireOpenForWork(connection, transaction, task.ConversationId);
                }

                return InsertTask(connection, transaction, task);
            },
            cancellationToken);
    }

    public Task<bool> MarkRunningAsync(GenerationTaskId task, MessageId? answer, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                var started = connection.Execute(transaction,
                    "UPDATE generation_task SET state = 'running', started_at = $at WHERE id = $id AND state = 'queued';",
                    ("$id", task.ToString()), ("$at", at.Ms()));
                if (started == 1 && answer is { } message)
                {
                    connection.Execute(transaction,
                        "UPDATE message SET state = 'streaming' WHERE id = $id AND state = 'pending';",
                        ("$id", message.ToString()));
                }

                return started == 1;
            },
            cancellationToken);

    public Task<bool> CheckpointAsync(MessageId answer, IReadOnlyList<ContentBlock> blocks, long revision, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => CheckpointBlocks(connection, transaction, answer, blocks, revision),
            cancellationToken);

    /// <summary>
    /// A checkpoint: the answer's blocks, only while it is streaming and only when <paramref name="revision"/> is newer than
    /// what is stored. Run steps write theirs the same way, in the step's transaction (architecture §15.3).
    /// </summary>
    internal static bool CheckpointBlocks(SqliteConnection connection, SqliteTransaction transaction, MessageId answer, IReadOnlyList<ContentBlock> blocks, long revision)
    {
        var applied = connection.Execute(transaction, """
            UPDATE message SET revision = $rev
            WHERE id = $id AND state = 'streaming' AND revision < $rev;
            """, ("$id", answer.ToString()), ("$rev", revision)) == 1;
        if (applied)
        {
            ReplaceBlocks(connection, transaction, answer, blocks);
        }

        return applied;
    }

    public Task<bool> FinishAnswerAsync(
        GenerationTaskId task,
        MessageId answer,
        IReadOnlyList<ContentBlock> blocks,
        long revision,
        MessageState state,
        GenerationErrorKind? errorKind,
        string? errorDetail,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                var applied = connection.Execute(transaction, """
                    UPDATE message
                    SET state = $state, revision = max(revision + 1, $rev),
                        error_kind = $kind, error_detail = $detail, completed_at = $at
                    WHERE id = $id AND state IN ('pending', 'streaming');
                    """,
                    ("$id", answer.ToString()),
                    ("$state", SqlNames.Of(state)),
                    ("$rev", revision),
                    ("$kind", errorKind is { } k ? SqlNames.Of(k) : null),
                    ("$detail", Truncate(errorDetail)),
                    ("$at", at.Ms())) == 1;
                if (applied)
                {
                    ReplaceBlocks(connection, transaction, answer, blocks);
                }

                FinishTask(connection, transaction, task, SqlNames.TaskStateFor(state), errorKind, errorDetail, at);
                return applied;
            },
            cancellationToken);

    public Task FinishTaskAsync(GenerationTaskId task, GenerationTaskState state, GenerationErrorKind? errorKind, string? errorDetail, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync((connection, transaction) => FinishTask(connection, transaction, task, state, errorKind, errorDetail, at), cancellationToken);

    public Task<bool> SetAutoTitleAsync(ConversationId id, string title, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            // A title still running when its conversation was archived finishes into nothing: no title while hidden.
            (connection, transaction) => connection.Execute(transaction,
                $"UPDATE conversation SET title = $title, title_source = 'auto' WHERE id = $id AND title_source <> 'manual' AND NOT {SqliteArchive.HiddenCondition("$id")};",
                ("$id", id.ToString()), ("$title", title)) == 1,
            cancellationToken);

    public Task SetManualTitleAsync(ConversationId id, string title, DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) => connection.Execute(transaction,
                "UPDATE conversation SET title = $title, title_source = 'manual', updated_at = $at WHERE id = $id;",
                ("$id", id.ToString()), ("$title", title), ("$at", at.Ms())),
            cancellationToken);

    public Task<RecoveryReport> RecoverUnfinishedAsync(DateTimeOffset at, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction) =>
            {
                var answers = connection.Execute(transaction,
                    "UPDATE message SET state = 'interrupted', completed_at = $at, revision = revision + 1 WHERE state IN ('pending', 'streaming');",
                    ("$at", at.Ms()));
                // Steps of runs that were under way end with them (architecture §15.10): a read is stopped; a side effect that
                // may have happened is unknown, never repeated; an approval nobody gave is stopped. Nothing is resumed.
                connection.Execute(transaction, """
                    UPDATE run_step SET state = CASE WHEN state = 'running' AND risk = 'side_effect' THEN 'unknown' ELSE 'stopped' END,
                                        finished_at = coalesce(finished_at, $at)
                    WHERE state IN ('running', 'awaiting_approval');
                    """, ("$at", at.Ms()));
                var tasks = connection.Execute(transaction,
                    "UPDATE generation_task SET state = 'interrupted', finished_at = $at WHERE state IN ('queued', 'running');",
                    ("$at", at.Ms()));
                return new RecoveryReport(answers, tasks);
            },
            cancellationToken);

    public Task<IReadOnlySet<string>> CredentialRefsInUseAsync(CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlySet<string>>(
            connection =>
            {
                using var command = connection.Command(null, "SELECT DISTINCT credential_ref FROM generation_task WHERE state IN ('queued', 'running');");
                using var reader = command.ExecuteReader();
                var set = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    set.Add(reader.GetString(0));
                }

                return set;
            },
            cancellationToken);

    private static int FinishTask(SqliteConnection connection, SqliteTransaction transaction, GenerationTaskId task, GenerationTaskState state, GenerationErrorKind? errorKind, string? errorDetail, DateTimeOffset at) =>
        connection.Execute(transaction, """
            UPDATE generation_task SET state = $state, error_kind = $kind, error_detail = $detail, finished_at = $at
            WHERE id = $id AND state IN ('queued', 'running');
            """,
            ("$id", task.ToString()),
            ("$state", SqlNames.Of(state)),
            ("$kind", errorKind is { } k ? SqlNames.Of(k) : null),
            ("$detail", Truncate(errorDetail)),
            ("$at", at.Ms()));

    private static void ThrowIfUnfinished(SqliteConnection connection, SqliteTransaction transaction, ConversationId id)
    {
        var unfinished = connection.Scalar<long>(transaction,
            "SELECT count(*) FROM message WHERE conversation_id = $id AND state IN ('pending', 'streaming');",
            ("$id", id.ToString()));
        if (unfinished > 0)
        {
            throw new AnswerInProgressException();
        }
    }

    private static int NextSeq(SqliteConnection connection, SqliteTransaction transaction, ConversationId id) =>
        (int)(connection.Scalar<long?>(transaction, "SELECT max(seq) FROM message WHERE conversation_id = $id;", ("$id", id.ToString())) ?? -1) + 1;

    private static void Touch(SqliteConnection connection, SqliteTransaction transaction, ConversationId id, DateTimeOffset at) =>
        connection.Execute(transaction, "UPDATE conversation SET updated_at = max(updated_at, $at) WHERE id = $id;", ("$id", id.ToString()), ("$at", at.Ms()));

    private static void InsertMessage(SqliteConnection connection, SqliteTransaction transaction, Message m)
    {
        connection.Execute(transaction, $"""
            INSERT INTO message ({MessageColumns})
            VALUES ($id, $conv, $seq, $role, $state, $rev, $sup, $kind, $detail, $created, $completed);
            """,
            ("$id", m.Id.ToString()),
            ("$conv", m.ConversationId.ToString()),
            ("$seq", m.Seq),
            ("$role", SqlNames.Of(m.Role)),
            ("$state", SqlNames.Of(m.State)),
            ("$rev", m.Revision),
            ("$sup", m.Supersedes?.ToString()),
            ("$kind", m.ErrorKind is { } k ? SqlNames.Of(k) : null),
            ("$detail", m.ErrorDetail),
            ("$created", m.CreatedAt.Ms()),
            ("$completed", m.CompletedAt?.Ms()));
        ReplaceBlocks(connection, transaction, m.Id, m.Blocks);
    }

    private static void ReplaceBlocks(SqliteConnection connection, SqliteTransaction transaction, MessageId message, IReadOnlyList<ContentBlock> blocks)
    {
        connection.Execute(transaction, "DELETE FROM message_block WHERE message_id = $id;", ("$id", message.ToString()));
        for (var i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case TextBlock t:
                    connection.Execute(transaction,
                        "INSERT INTO message_block (message_id, ordinal, kind, text) VALUES ($id, $ord, $kind, $text);",
                        ("$id", message.ToString()), ("$ord", i), ("$kind", SqlNames.TextBlockKind), ("$text", t.Markdown));
                    break;
                case AttachmentBlock a:
                    // The reference is what counts; type, size and text are read from the resource (docs/design/resources.md). The
                    // block's text is the resource's display name, so a reader of block text alone (the sidebar excerpt) sees a name.
                    var conversation = connection.Scalar<string>(transaction, "SELECT conversation_id FROM message WHERE id = $id;", ("$id", message.ToString()))
                        ?? throw new InvalidOperationException($"Message {message} must be stored before its blocks.");
                    var name = SqliteResourceStore.Adopt(connection, transaction, ConversationId.Parse(conversation), a.Resource);
                    connection.Execute(transaction,
                        "INSERT INTO message_block (message_id, ordinal, kind, text, resource_id) VALUES ($id, $ord, $kind, $text, $resource);",
                        ("$id", message.ToString()), ("$ord", i), ("$kind", SqlNames.AttachmentBlockKind), ("$text", name), ("$resource", a.Resource.ToString()));
                    break;
                case ToolCallBlock call:
                    connection.Execute(transaction,
                        "INSERT INTO message_block (message_id, ordinal, kind, text, data) VALUES ($id, $ord, $kind, $text, $data);",
                        ("$id", message.ToString()), ("$ord", i), ("$kind", SqlNames.ToolCallBlockKind), ("$text", call.Summary),
                        ("$data", JsonSerializer.Serialize(new ToolCallData(call.CallId, call.Tool, call.Arguments, SqlNames.Of(call.Risk)), BlockJson)));
                    break;
                case ToolResultBlock result:
                    connection.Execute(transaction,
                        "INSERT INTO message_block (message_id, ordinal, kind, text, data, resource_id) VALUES ($id, $ord, $kind, $text, $data, $resource);",
                        ("$id", message.ToString()), ("$ord", i), ("$kind", SqlNames.ToolResultBlockKind), ("$text", result.Preview),
                        ("$data", JsonSerializer.Serialize(new ToolResultData(result.CallId, SqlNames.Of(result.Status), result.Detail, result.Findings?.Select(FindingData.Of).ToList()), BlockJson)),
                        ("$resource", result.Resource?.ToString()));
                    break;
                case CitationBlock citation:
                    connection.Execute(transaction,
                        "INSERT INTO message_block (message_id, ordinal, kind, text, data, resource_id) VALUES ($id, $ord, $kind, $text, $data, $resource);",
                        ("$id", message.ToString()), ("$ord", i), ("$kind", SqlNames.CitationBlockKind), ("$text", citation.Title),
                        ("$data", JsonSerializer.Serialize(new CitationData(citation.Number, citation.Url?.AbsoluteUri, citation.FetchedAt.ToUnixTimeMilliseconds(), citation.Start, citation.End)
                        {
                            Kind = citation.Kind == CitationKind.Web ? null : citation.Kind.ToString().ToLowerInvariant(),
                            Target = citation.Target,
                        }, BlockJson)),
                        ("$resource", citation.Resource.ToString()));
                    break;
                case var other:
                    throw new NotSupportedException($"No storage mapping for {other.GetType().Name}.");
            }
        }
    }

    /// <summary>
    /// One block row as read by <see cref="GetMessages"/>: an attachment carries its resource's columns and text, read by the
    /// same statement, so a block and what it shows are always from one snapshot.
    /// </summary>
    private static ContentBlock ReadBlock(SqliteDataReader reader)
    {
        var kind = reader.GetString(1);
        return kind switch
        {
            SqlNames.TextBlockKind => new TextBlock(reader.GetString(2)),
            SqlNames.AttachmentBlockKind when !reader.IsDBNull(3) => new AttachmentBlock(
                ResourceId.Parse(reader.GetString(3)),
                reader.NullableString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.NullableString(7)),
            SqlNames.AttachmentBlockKind => throw new InvalidDataException("An attachment block names no resource."),
            SqlNames.ToolCallBlockKind => ReadToolCall(reader.GetString(2), reader.NullableString(8)),
            SqlNames.ToolResultBlockKind => ReadToolResult(reader.GetString(2), reader.NullableString(8), reader.NullableString(3)),
            SqlNames.CitationBlockKind => ReadCitation(reader.GetString(2), reader.NullableString(8), reader.NullableString(3)),
            _ => throw new InvalidDataException($"Unknown message block kind '{kind}'."),
        };
    }

    private static readonly JsonSerializerOptions BlockJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record ToolCallData(string CallId, string Tool, string Arguments, string Risk);

    private sealed record FindingData(string Kind, string Id, string Title, int? Part)
    {
        public static FindingData Of(Core.Runs.ToolFinding f) => new(
            f.Kind switch { Core.Runs.ToolFindingKind.Summary => "summary", Core.Runs.ToolFindingKind.WebSource => "web_source", _ => "resource_part" }, f.Id, f.Title, f.Part);

        public Core.Runs.ToolFinding ToFinding() => new(
            Kind switch { "summary" => Core.Runs.ToolFindingKind.Summary, "web_source" => Core.Runs.ToolFindingKind.WebSource, _ => Core.Runs.ToolFindingKind.ResourcePart }, Id, Title, Part);
    }

    private sealed record ToolResultData(string CallId, string Status, string? Detail, List<FindingData>? Findings);

    private static ToolCallBlock ReadToolCall(string text, string? data)
    {
        var d = (data is null ? null : JsonSerializer.Deserialize<ToolCallData>(data, BlockJson)) ?? throw new InvalidDataException("A tool call block has no data.");
        return new ToolCallBlock(d.CallId, d.Tool, d.Arguments, SqlNames.ParseRisk(d.Risk), text);
    }

    private static ToolResultBlock ReadToolResult(string text, string? data, string? resource)
    {
        var d = (data is null ? null : JsonSerializer.Deserialize<ToolResultData>(data, BlockJson)) ?? throw new InvalidDataException("A tool result block has no data.");
        return new ToolResultBlock(d.CallId, SqlNames.ParseResultStatus(d.Status), text, resource is null ? null : ResourceId.Parse(resource), d.Detail, d.Findings?.Select(f => f.ToFinding()).ToList());
    }

    /// <summary>A citation's data; <see cref="Kind"/> and <see cref="Target"/> only for local sources (L11), absent for the web.</summary>
    private sealed record CitationData(int Number, string? Url, long FetchedAt, int Start, int End)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Kind { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Target { get; init; }
    }

    private static CitationBlock ReadCitation(string text, string? data, string? resource)
    {
        var d = (data is null ? null : JsonSerializer.Deserialize<CitationData>(data, BlockJson)) ?? throw new InvalidDataException("A citation block has no data.");
        var kind = d.Kind is null ? CitationKind.Web : Enum.Parse<CitationKind>(d.Kind, ignoreCase: true);
        return new CitationBlock(
            d.Number, text, d.Url is null ? null : new Uri(d.Url, UriKind.Absolute), DateTimeOffset.FromUnixTimeMilliseconds(d.FetchedAt),
            ResourceId.Parse(resource ?? throw new InvalidDataException("A citation block names no resource.")), d.Start, d.End)
        {
            Kind = kind,
            Target = d.Target,
        };
    }

    /// <summary>A run's frozen tools, as stored in <c>generation_task.tools</c>.</summary>
    private sealed record OfferedToolData(string Name, string Hash);

    private sealed record LimitsData(int ModelRequests, int ToolCalls, int Minutes, int Tokens);

    private static int InsertTask(SqliteConnection connection, SqliteTransaction transaction, GenerationTaskRecord t) =>
        connection.Execute(transaction, """
            INSERT INTO generation_task (id, kind, conversation_id, target_message_id, state, model_config_id, base_url, protocol,
                                         model, credential_ref, max_output_tokens, capabilities, input_message_ids, created_at,
                                         summary_to_seq, summary_baseline, tools, run_limits)
            VALUES ($id, $kind, $conv, $target, 'queued', $config, $url, $protocol, $model, $cred, $max, $caps, $input, $created,
                    $toSeq, $baseline, $tools, $limits);
            """,
            ("$tools", t.Tools is null ? null : JsonSerializer.Serialize(t.Tools.Select(o => new OfferedToolData(o.Name, o.DefinitionHash)), BlockJson)),
            ("$limits", t.Limits is not { } l ? null : JsonSerializer.Serialize(new LimitsData(l.ModelRequests, l.ToolCalls, (int)Math.Round(l.Duration.TotalMinutes), l.Tokens), BlockJson)),
            ("$id", t.Id.ToString()),
            ("$kind", SqlNames.Of(t.Kind)),
            ("$conv", t.ConversationId.ToString()),
            ("$target", t.TargetMessageId?.ToString()),
            ("$config", t.Model.ModelConfigId),
            ("$url", t.Model.BaseUrl),
            ("$protocol", SqlNames.Of(t.Model.Protocol)),
            ("$model", t.Model.Model),
            ("$cred", t.Model.CredentialRef),
            ("$max", t.Model.MaxOutputTokens),
            ("$caps", SqlNames.Of(t.Model.Capabilities)),
            ("$input", JsonSerializer.Serialize(t.InputMessageIds.Select(i => i.ToString()))),
            ("$created", t.CreatedAt.Ms()),
            ("$toSeq", t.SummaryToSeq),
            ("$baseline", t.SummaryBaseline?.ToString("D")));

    internal static Conversation? GetConversation(SqliteConnection connection, SqliteTransaction? transaction, ConversationId id)
    {
        using var command = connection.Command(transaction,
            "SELECT id, workspace_id, title, title_source, created_at, updated_at FROM conversation WHERE id = $id;",
            ("$id", id.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadConversation(reader) : null;
    }

    /// <summary>The conversation's messages in order, up to and including <paramref name="maxSeq"/>.</summary>
    internal static List<Message> GetMessages(SqliteConnection connection, SqliteTransaction? transaction, ConversationId id, int maxSeq = int.MaxValue)
    {
        var blocks = new Dictionary<string, List<ContentBlock>>(StringComparer.Ordinal);
        using (var command = connection.Command(transaction, """
            SELECT b.message_id, b.kind, b.text, r.id, r.name, r.media_type, r.byte_size, d.text, b.data
            FROM message_block b JOIN message m ON m.id = b.message_id
            LEFT JOIN resource r ON r.id = b.resource_id
            LEFT JOIN resource_derivative d ON d.resource_id = r.id AND d.kind = 'text'
            WHERE m.conversation_id = $id AND m.seq <= $max
            ORDER BY b.message_id, b.ordinal;
            """, ("$id", id.ToString()), ("$max", maxSeq)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var messageId = reader.GetString(0);
                if (!blocks.TryGetValue(messageId, out var list))
                {
                    blocks[messageId] = list = [];
                }

                list.Add(ReadBlock(reader));
            }
        }

        using var messages = connection.Command(transaction, $"SELECT {MessageColumns} FROM message WHERE conversation_id = $id AND seq <= $max ORDER BY seq;", ("$id", id.ToString()), ("$max", maxSeq));
        using var row = messages.ExecuteReader();
        var result = new List<Message>();
        while (row.Read())
        {
            var messageId = row.GetString(0);
            var kind = row.NullableString(7);
            var supersedes = row.NullableString(6);
            var completed = row.NullableInt64(10);
            result.Add(new Message(
                MessageId.Parse(messageId),
                ConversationId.Parse(row.GetString(1)),
                row.GetInt32(2),
                SqlNames.ParseRole(row.GetString(3)),
                blocks.TryGetValue(messageId, out var content) ? content : throw new InvalidDataException($"Message {messageId} has no content blocks."),
                SqlNames.ParseMessageState(row.GetString(4)),
                row.GetInt64(5),
                supersedes is null ? null : MessageId.Parse(supersedes),
                kind is null ? null : SqlNames.ParseErrorKind(kind),
                row.NullableString(8),
                row.Time(9),
                completed is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null));
        }

        return result;
    }

    private static Conversation ReadConversation(SqliteDataReader reader) =>
        new(
            ConversationId.Parse(reader.GetString(0)),
            WorkspaceId.Parse(reader.GetString(1)),
            reader.NullableString(2),
            SqlNames.ParseTitleSource(reader.GetString(3)),
            reader.Time(4),
            reader.Time(5));

    private static string? Truncate(string? detail) => detail is { Length: > 1000 } ? detail[..1000] : detail;
}
