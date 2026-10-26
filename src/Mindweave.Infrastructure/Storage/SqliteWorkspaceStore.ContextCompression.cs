using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Core.Chat;
using Mindweave.Application.Chat;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    public Task<ReplyContextPlan?> LoadContextPlanAsync(Guid replyTaskId) => ReadAsync(connection =>
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        if (!HasContextPlan(connection, transaction, replyTaskId)) return null;
        return ReadContextMaterial(connection, transaction, replyTaskId).Plan;
    });

    public Task<CompletedContextSummary?> LoadCompletedSummaryAsync(Guid replyTaskId, string cacheKey) => ReadAsync(connection =>
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        var material = ReadContextMaterial(connection, transaction, replyTaskId);
        return ReadCompletedSummary(connection, transaction, material, RequireChunk(material, cacheKey));
    });

    public Task<ContextCompressionAttempt> StartCompressionAsync(Guid replyTaskId, string cacheKey) => WriteAsync((connection, transaction) =>
    {
        var material = ReadContextMaterial(connection, transaction, replyTaskId, requireActive: true);
        var chunk = RequireChunk(material, cacheKey);
        using var find = ChatCommand(connection, transaction, "SELECT a.id FROM context_compression_dependency d JOIN context_compression_attempt a ON a.id=d.attempt_id WHERE d.task_id=$task AND d.cache_key=$key AND a.status IN ('Queued','Running','Completed') ORDER BY a.rowid DESC LIMIT 1",
            ("$task", replyTaskId), ("$key", cacheKey));
        if (find.ExecuteScalar() is string existing)
        {
            var attempt = ReadCompressionAttempt(connection, transaction, Guid.Parse(existing))!;
            if (attempt.Status == GenerationStatus.Completed && ReadCompletedSummary(connection, transaction, material, chunk) is null)
                throw new InvalidOperationException("完成压缩缺少有效缓存。");
            return attempt;
        }
        if (ReadCompletedSummary(connection, transaction, material, chunk) is not null)
            throw new InvalidOperationException("此片段已有完成缓存，请直接使用缓存。");
        // This performs the exact compression-request preflight against the accepted configuration.
        ReplyContextPlanner.CreateSummaryRequest(chunk.SourceConversationId, chunk, material.Originals, material.Task.Configuration);
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var insert = ChatCommand(connection, transaction, """
            INSERT INTO context_compression_attempt(id,source_conversation_id,cache_key,configuration_json,chunk_json,status,revision,payload,created_at,updated_at)
            VALUES($id,$owner,$key,$config,$chunk,'Queued',0,'',$now,$now)
            """, ("$id", id), ("$owner", chunk.SourceConversationId), ("$key", cacheKey), ("$config", JsonSerializer.Serialize(material.Task.Configuration)),
            ("$chunk", JsonSerializer.Serialize(chunk)), ("$now", now));
        insert.ExecuteNonQuery();
        for (var index = 0; index < chunk.Slices.Count; index++)
        {
            var slice = chunk.Slices[index];
            using var source = ChatCommand(connection, transaction, "INSERT INTO context_compression_source VALUES($attempt,$owner,$index,$message,$revision,$hash,$start,$length)",
                ("$attempt", id), ("$owner", chunk.SourceConversationId), ("$index", index), ("$message", slice.MessageId),
                ("$revision", slice.Revision), ("$hash", slice.BodyHash), ("$start", slice.Start), ("$length", slice.Length));
            source.ExecuteNonQuery();
        }
        using var dependency = ChatCommand(connection, transaction, "INSERT INTO context_compression_dependency VALUES($task,$key,$attempt)",
            ("$task", replyTaskId), ("$key", cacheKey), ("$attempt", id));
        dependency.ExecuteNonQuery();
        return ReadCompressionAttempt(connection, transaction, id)!;
    });

    public Task<ContextCompressionAttempt?> LoadCompressionAttemptAsync(Guid attemptId) => ReadAsync(connection =>
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        return ReadCompressionAttempt(connection, transaction, attemptId);
    });

    public Task<bool> WriteCompressionAsync(Guid replyTaskId, Guid attemptId, long revision, string payload, GenerationStatus status,
        string? finishReason = null, string? errorCode = null, string? actualModelId = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!Enum.IsDefined(status)) throw new ArgumentException("未知生成状态。", nameof(status));
        ValidateGenerationMetadata(errorCode, actualModelId);
        return WriteAsync((connection, transaction) =>
        {
            var attempt = ReadCompressionAttempt(connection, transaction, attemptId);
            if (attempt is null || !IsActive(attempt.Status) || revision <= attempt.Revision ||
                (attempt.Status == GenerationStatus.Running && status == GenerationStatus.Queued)) return false;
            var parent = ReadGeneration(connection, transaction, replyTaskId);
            if (parent is null || !IsActive(parent.Status)) return false;
            using var dependency = ChatCommand(connection, transaction, "SELECT 1 FROM context_compression_dependency WHERE task_id=$task AND attempt_id=$attempt AND cache_key=$key",
                ("$task", replyTaskId), ("$attempt", attemptId), ("$key", attempt.Chunk.CacheKey));
            if (dependency.ExecuteScalar() is null) return false;
            var material = ReadContextMaterial(connection, transaction, replyTaskId);
            var authorizedChunk = RequireChunk(material, attempt.Chunk.CacheKey);
            if (attempt.Configuration != material.Task.Configuration || JsonSerializer.Serialize(authorizedChunk) != JsonSerializer.Serialize(attempt.Chunk))
                throw new InvalidOperationException("压缩尝试与请求依赖不符。");
            if (Encoding.UTF8.GetByteCount(payload) > ContextSummaryProtocol.MaximumPayloadBytes)
                throw new InvalidOperationException("压缩输出超过保存上限。");
            if (status == GenerationStatus.Completed)
            {
                if (finishReason != "stop") throw new InvalidOperationException("压缩未正常完整结束。");
                ContextSummaryProtocol.Parse(payload);
            }
            var model = actualModelId ?? attempt.ActualModelId;
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            using var update = ChatCommand(connection, transaction, """
                UPDATE context_compression_attempt SET status=$status,revision=$revision,payload=$payload,
                    finish_reason=$finish,error_code=$error,actual_model_id=$model,updated_at=$now WHERE id=$id
                """, ("$id", attemptId), ("$status", status.ToString()), ("$revision", revision), ("$payload", payload),
                ("$finish", finishReason), ("$error", errorCode), ("$model", model), ("$now", now));
            update.ExecuteNonQuery();
            if (status == GenerationStatus.Completed && ReadCompletedSummary(connection, transaction, material, authorizedChunk) is null)
            {
                // Separate siblings may have started the same source chunk. First durable cache wins;
                // a later valid producer still completes, without replacing that immutable result.
                using var insert = ChatCommand(connection, transaction, """
                    INSERT INTO context_summary_cache VALUES($id,$key,$conversation,$attempt,$config,$chunk,$payload,$hash,$model,$now)
                    """, ("$id", Guid.NewGuid()), ("$key", attempt.Chunk.CacheKey), ("$conversation", attempt.SourceConversationId),
                    ("$attempt", attemptId), ("$config", JsonSerializer.Serialize(material.Task.Configuration)),
                    ("$chunk", JsonSerializer.Serialize(attempt.Chunk)), ("$payload", payload), ("$hash", PayloadHash(payload)), ("$model", model), ("$now", now));
                insert.ExecuteNonQuery();
            }
            return true;
        });
    }

    private static bool HasContextPlan(SqliteConnection c, SqliteTransaction t, Guid id)
    {
        using var command = ChatCommand(c, t, "SELECT 1 FROM reply_context_plan WHERE task_id=$id", ("$id", id));
        return command.ExecuteScalar() is not null;
    }
    private static ContextChunk RequireChunk(ContextMaterial material, string key) =>
        material.Plan.Parts.OfType<SummaryContextPart>().SingleOrDefault(p => p.Chunk.CacheKey == key)?.Chunk
        ?? throw new InvalidOperationException("压缩片段不属于此回答计划。");
    private static string PayloadHash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    private static DateTimeOffset ContextTime(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    private static ContextCompressionAttempt? ReadCompressionAttempt(SqliteConnection c, SqliteTransaction t, Guid id)
    {
        using var command = ChatCommand(c, t, "SELECT source_conversation_id,cache_key,status,revision,payload,finish_reason,error_code,actual_model_id,created_at,updated_at,configuration_json,chunk_json FROM context_compression_attempt WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var owner = Guid.Parse(reader.GetString(0)); var key = reader.GetString(1);
        var status = Enum.Parse<GenerationStatus>(reader.GetString(2)); var revision = reader.GetInt64(3); var payload = reader.GetString(4);
        var finish = reader.IsDBNull(5) ? null : reader.GetString(5); var error = reader.IsDBNull(6) ? null : reader.GetString(6);
        var model = reader.IsDBNull(7) ? null : reader.GetString(7); var created = ContextTime(reader.GetString(8)); var updated = ContextTime(reader.GetString(9));
        var configJson = reader.GetString(10); var chunkJson = reader.GetString(11); reader.Close();
        var config = JsonSerializer.Deserialize<AiConfiguration>(configJson) ?? throw new InvalidOperationException("压缩配置无效。");
        var chunk = JsonSerializer.Deserialize<ContextChunk>(chunkJson) ?? throw new InvalidOperationException("压缩片段无效。");
        if (chunk.SourceConversationId != owner || chunk.CacheKey != key || chunk.Slices is null ||
            chunkJson != JsonSerializer.Serialize(chunk) || configJson != JsonSerializer.Serialize(config))
            throw new InvalidOperationException("压缩来源身份已损坏。");
        using var sources = ChatCommand(c, t, "SELECT slice_order,message_id,revision,body_hash,start,length FROM context_compression_source WHERE attempt_id=$id ORDER BY slice_order", ("$id", id));
        using var sourceReader = sources.ExecuteReader(); var slices = new List<ContextSourceSlice>();
        while (sourceReader.Read())
        {
            if (sourceReader.GetInt32(0) != slices.Count) throw new InvalidOperationException("压缩来源顺序已损坏。");
            slices.Add(new(Guid.Parse(sourceReader.GetString(1)), sourceReader.GetInt64(2), sourceReader.GetString(3), sourceReader.GetInt32(4), sourceReader.GetInt32(5)));
        }
        sourceReader.Close();
        if (!slices.SequenceEqual(chunk.Slices)) throw new InvalidOperationException("压缩来源清单已损坏。");
        var originals = ReadMessages(c, t, owner);
        if (originals.Any(m => slices.Any(s => s.MessageId == m.Id) && IsActive(m.Status)))
            throw new InvalidOperationException("压缩来源不是终态。");
        // Independent source-owned evidence. No requester, requester plan, or LatestInput is needed.
        ReplyContextPlanner.CreateSummaryRequest(owner, chunk, originals, config);
        return new(id, owner, chunk, config, status, revision, payload, finish, error, model, created, updated);
    }

    private static CompletedContextSummary? ReadCompletedSummary(SqliteConnection c, SqliteTransaction t, ContextMaterial material, ContextChunk chunk)
    {
        using var command = ChatCommand(c, t, "SELECT id,source_conversation_id,attempt_id,configuration_json,chunk_json,payload,payload_hash,actual_model_id,completed_at FROM context_summary_cache WHERE cache_key=$key", ("$key", chunk.CacheKey));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var id = Guid.Parse(reader.GetString(0)); var conversationId = Guid.Parse(reader.GetString(1)); var attemptId = Guid.Parse(reader.GetString(2));
        var config = reader.GetString(3); var storedChunk = reader.GetString(4); var payload = reader.GetString(5); var hash = reader.GetString(6);
        var model = reader.IsDBNull(7) ? null : reader.GetString(7); var time = ContextTime(reader.GetString(8)); reader.Close();
        var attempt = ReadCompressionAttempt(c, t, attemptId);
        if (conversationId != chunk.SourceConversationId || config != JsonSerializer.Serialize(material.Task.Configuration) ||
            storedChunk != JsonSerializer.Serialize(chunk) || hash != PayloadHash(payload) || attempt is null ||
            attempt.Status != GenerationStatus.Completed || attempt.FinishReason != "stop" || attempt.Payload != payload ||
            attempt.ActualModelId != model || attempt.UpdatedAt != time || attempt.Configuration != material.Task.Configuration ||
            JsonSerializer.Serialize(attempt.Chunk) != storedChunk)
            throw new InvalidOperationException("完成摘要的内容或来源身份已损坏。");
        if (attempt.SourceConversationId != conversationId) throw new InvalidOperationException("摘要属于其他会话。");
        return new(id, conversationId, attemptId, chunk, material.Task.Configuration, ContextSummaryProtocol.Parse(payload), hash, model, time);
    }
}
