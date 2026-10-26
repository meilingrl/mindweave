using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Branches;
using Mindweave.Core.Branches;
using Mindweave.Core.Chat;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore : IBranchStore
{
    private const string SupportedRendererProfile = "livemarkdown/2.5.1;mindweave/1";

    public Task<Conversation> CreateBranchAsync(Guid parentConversationId, SourceSelection selection, Guid operationId, ConversationReadState? parentReadState = null)
    {
        ValidateSelection(selection);
        if (operationId == Guid.Empty) throw new ArgumentException("分支操作身份不能为空。", nameof(operationId));
        if (parentReadState is not null && parentReadState.ConversationId != parentConversationId)
            throw new ArgumentException("阅读位置必须属于父会话。", nameof(parentReadState));
        return WriteAsync((connection, transaction) =>
        {
            // Operation replay is bound to the complete immutable selection, not just the child.
            using (var replay = ChatCommand(connection, transaction, """
                SELECT child_conversation_id,parent_conversation_id,message_id,message_revision,source_body_hash,quote,
                    anchor_version,renderer_profile,anchor_payload FROM branch_source WHERE operation_id=$operation
                """, ("$operation", operationId)))
            using (var reader = replay.ExecuteReader())
                if (reader.Read())
                {
                    if (Guid.Parse(reader.GetString(1)) != parentConversationId || Guid.Parse(reader.GetString(2)) != selection.MessageId ||
                        reader.GetInt64(3) != selection.MessageRevision || reader.GetString(4) != selection.SourceBodyHash || reader.GetString(5) != selection.Quote ||
                        reader.GetInt32(6) != selection.Anchor.FormatVersion || reader.GetString(7) != selection.Anchor.RendererProfile || reader.GetString(8) != selection.Anchor.Payload)
                        throw new InvalidOperationException("分支操作身份已用于不同来源。");
                    return RequireConversation(connection, transaction, Guid.Parse(reader.GetString(0)));
                }
            var parent = RequireConversation(connection, transaction, parentConversationId);
            var messages = ReadMessages(connection, transaction, parentConversationId);
            var source = messages.SingleOrDefault(message => message.Id == selection.MessageId);
            if (source is null || source.Role != MessageRole.Assistant || IsActive(source.Status) || string.IsNullOrWhiteSpace(source.Body) ||
                source.Revision != selection.MessageRevision || BodyHash(source.Body) != selection.SourceBodyHash)
                throw new ArgumentException("来源必须是此父会话已保存且版本一致的非空终态回答。", nameof(selection));
            // Validate the inherited chain before copying identities. Ordinary foreign keys cannot prove ancestry.
            var inherited = ReadValidatedRanges(connection, transaction, parentConversationId);
            var own = ChatHistory.LatestInput(messages.Where(message => message.Sequence <= source.Sequence).ToArray()).ToArray();
            if (own.Any(message => IsActive(message.Status))) throw new ArgumentException("来源上下文尚未保存为终态。", nameof(selection));
            var ranges = inherited.Append(new FrozenContextRange(parentConversationId, own.Select(message => message.Id).ToArray())).ToArray();
            var child = new Conversation(Guid.NewGuid(), parent.WorkspaceId, "新分支", false, 0, DateTimeOffset.UtcNow);
            using (var insert = ChatCommand(connection, transaction,
                "INSERT INTO conversation(id,workspace_id,title,created_at) VALUES($id,$workspace,$title,$created)",
                ("$id", child.Id), ("$workspace", child.WorkspaceId), ("$title", child.Title), ("$created", child.CreatedAt.ToString("O", CultureInfo.InvariantCulture))))
                insert.ExecuteNonQuery();
            using (var insert = ChatCommand(connection, transaction, """
                INSERT INTO branch_source VALUES($child,$operation,$parent,$message,$revision,$hash,$quote,$version,$profile,$payload)
                """, ("$child", child.Id), ("$operation", operationId), ("$parent", parent.Id), ("$message", source.Id),
                ("$revision", selection.MessageRevision), ("$hash", selection.SourceBodyHash), ("$quote", selection.Quote),
                ("$version", selection.Anchor.FormatVersion), ("$profile", selection.Anchor.RendererProfile), ("$payload", selection.Anchor.Payload)))
                insert.ExecuteNonQuery();
            for (var rangeIndex = 0; rangeIndex < ranges.Length; rangeIndex++)
            {
                var range = ranges[rangeIndex];
                using var insert = ChatCommand(connection, transaction, "INSERT INTO frozen_context_range VALUES($child,$order,$ancestor)",
                    ("$child", child.Id), ("$order", rangeIndex), ("$ancestor", range.ConversationId));
                insert.ExecuteNonQuery();
                for (var messageIndex = 0; messageIndex < range.MessageIds.Count; messageIndex++)
                {
                    using var message = ChatCommand(connection, transaction, "INSERT INTO frozen_context_message VALUES($child,$range,$ancestor,$order,$message)",
                        ("$child", child.Id), ("$range", rangeIndex), ("$ancestor", range.ConversationId), ("$order", messageIndex), ("$message", range.MessageIds[messageIndex]));
                    message.ExecuteNonQuery();
                }
            }
            if (parentReadState is not null) SaveReadState(connection, transaction, parentReadState);
            return child;
        });
    }

    public Task<BranchSource?> LoadBranchSourceAsync(Guid childConversationId) => ReadAsync(connection => ReadBranchSource(connection, null, childConversationId));

    public Task<IReadOnlyList<BranchSource>> ListChildBranchesAsync(Guid parentConversationId) => ReadAsync<IReadOnlyList<BranchSource>>(connection =>
    {
        using var command = ChatCommand(connection, null, $"SELECT {BranchColumns} FROM branch_source WHERE parent_conversation_id=$parent ORDER BY rowid", ("$parent", parentConversationId));
        using var reader = command.ExecuteReader(); var sources = new List<BranchSource>();
        while (reader.Read()) sources.Add(ReadBranchSource(reader));
        return sources;
    });

    public Task<IReadOnlyList<FrozenContextRange>> ReadFrozenContextRangesAsync(Guid conversationId) => ReadAsync<IReadOnlyList<FrozenContextRange>>(connection =>
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        return ReadValidatedRanges(connection, transaction, conversationId);
    });

    private const string BranchColumns = "child_conversation_id,parent_conversation_id,message_id,quote,anchor_version,renderer_profile,anchor_payload";
    private static BranchSource? ReadBranchSource(SqliteConnection connection, SqliteTransaction? transaction, Guid child)
    {
        using var command = ChatCommand(connection, transaction, $"SELECT {BranchColumns} FROM branch_source WHERE child_conversation_id=$child", ("$child", child));
        using var reader = command.ExecuteReader(); return reader.Read() ? ReadBranchSource(reader) : null;
    }
    private static BranchSource ReadBranchSource(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetString(3), new(reader.GetInt32(4), reader.GetString(5), reader.GetString(6)));

    private static List<FrozenContextRange> ReadValidatedRanges(SqliteConnection connection, SqliteTransaction transaction, Guid child)
    {
        var conversation = RequireConversation(connection, transaction, child);
        var chain = new List<Guid>(); var seen = new HashSet<Guid> { child }; var cursor = child;
        while (ReadBranchSource(connection, transaction, cursor) is { } source)
        {
            if (!seen.Add(source.ParentConversationId) || RequireConversation(connection, transaction, source.ParentConversationId).WorkspaceId != conversation.WorkspaceId)
                throw new InvalidDataException("分支祖先关系无效。");
            chain.Add(source.ParentConversationId); cursor = source.ParentConversationId;
        }
        chain.Reverse();
        var ranges = new List<FrozenContextRange>();
        using (var command = ChatCommand(connection, transaction, "SELECT range_order,ancestor_conversation_id FROM frozen_context_range WHERE child_conversation_id=$child ORDER BY range_order", ("$child", child)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (reader.GetInt32(0) != ranges.Count) throw new InvalidDataException("冻结范围顺序无效。");
                ranges.Add(new(Guid.Parse(reader.GetString(1)), []));
            }
        if (!chain.SequenceEqual(ranges.Select(range => range.ConversationId))) throw new InvalidDataException("冻结范围必须且只能来自完整祖先链。");
        for (var index = 0; index < ranges.Count; index++)
        {
            var ids = new List<Guid>();
            using var command = ChatCommand(connection, transaction, "SELECT message_order,message_id FROM frozen_context_message WHERE child_conversation_id=$child AND range_order=$range ORDER BY message_order", ("$child", child), ("$range", index));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetInt32(0) != ids.Count) throw new InvalidDataException("冻结消息顺序无效。");
                ids.Add(Guid.Parse(reader.GetString(1)));
            }
            if (ids.Count == 0) throw new InvalidDataException("冻结范围不能为空。");
            ranges[index] = ranges[index] with { MessageIds = ids.ToArray() };
        }
        return ranges;
    }

    private static string BodyHash(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
    private static void ValidateSelection(SourceSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.MessageId == Guid.Empty || selection.MessageRevision < 0 || selection.SourceBodyHash is null || selection.SourceBodyHash.Length != 64 ||
            selection.SourceBodyHash.Any(character => !char.IsAsciiDigit(character) && character is not (>= 'A' and <= 'F')) ||
            string.IsNullOrWhiteSpace(selection.Quote) || selection.Anchor is null || selection.Anchor.FormatVersion != 1 ||
            selection.Anchor.RendererProfile != SupportedRendererProfile || string.IsNullOrWhiteSpace(selection.Anchor.Payload) ||
            Encoding.UTF8.GetByteCount(selection.Anchor.Payload) > SourceAnchorProtocol.MaximumPayloadBytes)
            throw new ArgumentException("来源选区信封无效。", nameof(selection));
        try
        {
            using var payload = JsonDocument.Parse(selection.Anchor.Payload, new JsonDocumentOptions { MaxDepth = 64 });
            if (payload.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("来源锚点必须是 JSON 对象。", nameof(selection));
        }
        catch (JsonException error) { throw new ArgumentException("来源锚点 JSON 无效。", nameof(selection), error); }
    }
}
