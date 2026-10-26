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
        using var find = ChatCommand(connection, transaction, "SELECT id FROM context_compression_attempt WHERE task_id=$task AND cache_key=$key AND status IN ('Queued','Running','Completed') ORDER BY rowid DESC LIMIT 1",
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
        ReplyContextPlanner.CreateSummaryRequest(material.Task.ConversationId, chunk, material.Originals, material.Task.Configuration);
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var insert = ChatCommand(connection, transaction, """
            INSERT INTO context_compression_attempt(id,task_id,cache_key,status,revision,payload,created_at,updated_at)
            VALUES($id,$task,$key,'Queued',0,'',$now,$now)
            """, ("$id", id), ("$task", replyTaskId), ("$key", cacheKey), ("$now", now));
        insert.ExecuteNonQuery();
        return ReadCompressionAttempt(connection, transaction, id)!;
    });

    public Task<ContextCompressionAttempt?> LoadCompressionAttemptAsync(Guid attemptId) => ReadAsync(connection =>
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        return ReadCompressionAttempt(connection, transaction, attemptId);
    });

    public Task<bool> WriteCompressionAsync(Guid attemptId, long revision, string payload, GenerationStatus status,
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
            var material = ReadContextMaterial(connection, transaction, attempt.ReplyTaskId);
            if (!IsActive(material.Task.Status)) return false;
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
            if (status == GenerationStatus.Completed)
            {
                using var insert = ChatCommand(connection, transaction, """
                    INSERT INTO context_summary_cache VALUES($id,$key,$conversation,$attempt,$config,$chunk,$payload,$hash,$model,$now)
                    """, ("$id", Guid.NewGuid()), ("$key", attempt.Chunk.CacheKey), ("$conversation", material.Task.ConversationId),
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
        using var command = ChatCommand(c, t, "SELECT task_id,cache_key,status,revision,payload,finish_reason,error_code,actual_model_id,created_at,updated_at FROM context_compression_attempt WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var taskId = Guid.Parse(reader.GetString(0)); var key = reader.GetString(1);
        var status = Enum.Parse<GenerationStatus>(reader.GetString(2)); var revision = reader.GetInt64(3); var payload = reader.GetString(4);
        var finish = reader.IsDBNull(5) ? null : reader.GetString(5); var error = reader.IsDBNull(6) ? null : reader.GetString(6);
        var model = reader.IsDBNull(7) ? null : reader.GetString(7); var created = ContextTime(reader.GetString(8)); var updated = ContextTime(reader.GetString(9));
        reader.Close();
        var material = ReadContextMaterial(c, t, taskId);
        return new(id, taskId, RequireChunk(material, key), material.Task.Configuration, status, revision, payload, finish, error, model, created, updated);
    }

    private static CompletedContextSummary? ReadCompletedSummary(SqliteConnection c, SqliteTransaction t, ContextMaterial material, ContextChunk chunk)
    {
        using var command = ChatCommand(c, t, "SELECT id,conversation_id,attempt_id,configuration_json,chunk_json,payload,payload_hash,actual_model_id,completed_at FROM context_summary_cache WHERE cache_key=$key", ("$key", chunk.CacheKey));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var id = Guid.Parse(reader.GetString(0)); var conversationId = Guid.Parse(reader.GetString(1)); var attemptId = Guid.Parse(reader.GetString(2));
        var config = reader.GetString(3); var storedChunk = reader.GetString(4); var payload = reader.GetString(5); var hash = reader.GetString(6);
        var model = reader.IsDBNull(7) ? null : reader.GetString(7); var time = ContextTime(reader.GetString(8)); reader.Close();
        var attempt = ReadCompressionAttempt(c, t, attemptId);
        if (conversationId != material.Task.ConversationId || config != JsonSerializer.Serialize(material.Task.Configuration) ||
            storedChunk != JsonSerializer.Serialize(chunk) || hash != PayloadHash(payload) || attempt is null ||
            attempt.Status != GenerationStatus.Completed || attempt.FinishReason != "stop" || attempt.Payload != payload ||
            attempt.ActualModelId != model || attempt.UpdatedAt != time || attempt.Configuration != material.Task.Configuration ||
            JsonSerializer.Serialize(attempt.Chunk) != storedChunk)
            throw new InvalidOperationException("完成摘要的内容或来源身份已损坏。");
        var producingTask = ReadGeneration(c, t, attempt.ReplyTaskId)!;
        if (producingTask.ConversationId != conversationId) throw new InvalidOperationException("摘要属于其他会话。");
        return new(id, conversationId, attemptId, chunk, material.Task.Configuration, ContextSummaryProtocol.Parse(payload), hash, model, time);
    }
}
