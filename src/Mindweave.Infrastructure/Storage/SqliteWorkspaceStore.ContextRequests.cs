using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    private const int PreparedRequestFormatVersion = 2;

    // Versioned compact JSON with fixed envelope/property order and ordered Role/Content entries.
    // Hash the complete frozen configuration and physical material without retaining another body copy.
    private static string PreparedRequestHash(ChatRequest request) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            FormatVersion = PreparedRequestFormatVersion,
            Configuration = request.Configuration,
            Messages = request.Messages
        })));

    public Task<PreparedReplyRequest> PrepareReplyRequestAsync(Guid replyTaskId) => WriteAsync((connection, transaction) =>
    {
        var material = ReadContextMaterial(connection, transaction, replyTaskId, requireActive: true);
        var existing = ReadPreparedReplyRequest(connection, transaction, replyTaskId);
        if (existing is not null) return existing;
        var prepared = ComposePreparedReply(connection, transaction, material, DateTimeOffset.UtcNow);
        using var insert = ChatCommand(connection, transaction, """
            INSERT INTO prepared_reply_request VALUES($id,$version,$config,$hash,$now)
            """, ("$id", replyTaskId), ("$version", PreparedRequestFormatVersion), ("$config", JsonSerializer.Serialize(prepared.Request.Configuration)),
            ("$hash", PreparedRequestHash(prepared.Request)), ("$now", prepared.PreparedAt.ToString("O", CultureInfo.InvariantCulture)));
        insert.ExecuteNonQuery();
        foreach (var use in prepared.Uses)
        {
            using var add = ChatCommand(connection, transaction, "INSERT INTO prepared_context_use VALUES($id,$index,$kind,$message,$summary)",
                ("$id", replyTaskId), ("$index", use.InputIndex), ("$kind", use.Kind.ToString()), ("$message", use.MessageId), ("$summary", use.SummaryId));
            add.ExecuteNonQuery();
        }
        return prepared;
    });

    public Task<PreparedReplyRequest?> LoadPreparedReplyRequestAsync(Guid replyTaskId) => ReadAsync(connection =>
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        return ReadPreparedReplyRequest(connection, transaction, replyTaskId);
    });

    private static PreparedReplyRequest ComposePreparedReply(SqliteConnection c, SqliteTransaction t, ContextMaterial material, DateTimeOffset time)
    {
        var summaries = new Dictionary<string, ContextSummaryContent>(); var uses = new List<PreparedContextUse>();
        if (material.Plan.SourceQuote is not null)
            uses.Add(new(uses.Count, ContextUseKind.SourceQuote, material.Plan.DirectSourceMessageId ?? throw new InvalidOperationException("引用缺少来源身份。"), null));
        foreach (var part in material.Plan.Parts)
        {
            if (part is VerbatimContextPart raw) uses.Add(new(uses.Count, ContextUseKind.Verbatim, raw.MessageId, null));
            else if (part is SummaryContextPart summary)
            {
                var cached = ReadCompletedSummary(c, t, material, summary.Chunk) ?? throw new InvalidOperationException("必要压缩尚未完成。");
                summaries.Add(summary.Chunk.CacheKey, cached.Content);
                uses.Add(new(uses.Count, ContextUseKind.Summary, null, cached.Id));
            }
        }
        var input = ReplyContextPlanner.ComposeInputs(material.Plan, material.Originals, summaries, material.Task.Configuration);
        return new(material.Task.Id, new(material.Task.Configuration, Array.AsReadOnly(input)), uses.AsReadOnly(), time);
    }

    private static PreparedReplyRequest? ReadPreparedReplyRequest(SqliteConnection c, SqliteTransaction t, Guid id)
    {
        using var read = ChatCommand(c, t, "SELECT format_version,configuration_json,request_hash,prepared_at FROM prepared_reply_request WHERE task_id=$id", ("$id", id));
        using var reader = read.ExecuteReader();
        if (!reader.Read()) return null;
        var version = reader.GetInt32(0); var config = reader.GetString(1); var hash = reader.GetString(2); var time = ContextTime(reader.GetString(3)); reader.Close();
        if (version != PreparedRequestFormatVersion) throw new InvalidOperationException("准备的请求格式版本不受支持。");
        var expected = ComposePreparedReply(c, t, ReadContextMaterial(c, t, id), time);
        if (config != JsonSerializer.Serialize(expected.Request.Configuration) || hash != PreparedRequestHash(expected.Request))
            throw new InvalidOperationException("准备的实际请求内容已损坏。");
        using var readUses = ChatCommand(c, t, "SELECT input_index,kind,message_id,summary_id FROM prepared_context_use WHERE task_id=$id ORDER BY input_index", ("$id", id));
        using var usesReader = readUses.ExecuteReader(); var uses = new List<PreparedContextUse>();
        while (usesReader.Read()) uses.Add(new(usesReader.GetInt32(0), Enum.Parse<ContextUseKind>(usesReader.GetString(1)),
            usesReader.IsDBNull(2) ? null : Guid.Parse(usesReader.GetString(2)), usesReader.IsDBNull(3) ? null : Guid.Parse(usesReader.GetString(3))));
        if (!uses.SequenceEqual(expected.Uses)) throw new InvalidOperationException("实际请求使用清单已损坏。");
        return expected;
    }
}
