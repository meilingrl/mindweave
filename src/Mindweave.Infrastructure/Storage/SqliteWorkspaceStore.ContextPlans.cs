using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Chat;
using Mindweave.Core.Chat;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    // Storage format is explicit; Core's abstract ContextPart never enters the JSON serializer.
    private sealed record StoredPart(string Kind, Guid? MessageId, ContextChunk? Chunk);
    private sealed record StoredPlan(int FormatVersion, string EstimatorProfile, IReadOnlyList<Guid> OriginalMessageIds,
        IReadOnlyList<StoredPart> Parts, Guid? DirectSourceMessageId, string? SourceQuote);
    private static string PlanJson(ReplyContextPlan plan) => JsonSerializer.Serialize(new StoredPlan(plan.FormatVersion,
        plan.EstimatorProfile, plan.OriginalMessageIds, plan.Parts.Select<ContextPart, StoredPart>(part => part switch
        {
            VerbatimContextPart raw => new("Verbatim", raw.MessageId, null),
            SummaryContextPart summary => new("Summary", null, summary.Chunk),
            _ => throw new InvalidOperationException("未知上下文材料。")
        }).ToArray(), plan.DirectSourceMessageId, plan.SourceQuote));

    private static void InsertContextPlan(SqliteConnection connection, SqliteTransaction transaction, Guid taskId, ReplyContextPlan plan)
    {
        using var insert = ChatCommand(connection, transaction, "INSERT INTO reply_context_plan VALUES($id,$json)", ("$id", taskId), ("$json", PlanJson(plan)));
        insert.ExecuteNonQuery();
        foreach (var summary in plan.Parts.OfType<SummaryContextPart>())
        {
            using var chunk = ChatCommand(connection, transaction, "INSERT INTO context_plan_chunk VALUES($id,$key,$json)",
                ("$id", taskId), ("$key", summary.Chunk.CacheKey), ("$json", JsonSerializer.Serialize(summary.Chunk)));
            chunk.ExecuteNonQuery();
        }
    }

    private sealed record ContextMaterial(GenerationRecord Task, ChatMessage[] Originals, ReplyContextPlan Plan);
    private static ContextMaterial ReadContextMaterial(SqliteConnection connection, SqliteTransaction transaction, Guid taskId, bool requireActive = false)
    {
        var task = ReadGeneration(connection, transaction, taskId);
        if (task is null || task.Kind != GenerationKind.Reply || (requireActive && !IsActive(task.Status)))
            throw new InvalidOperationException("回答任务不存在或已结束。");
        var own = ReadMessages(connection, transaction, task.ConversationId);
        var answer = own.SingleOrDefault(m => m.Id == task.AnswerMessageId) ?? throw new InvalidOperationException("回答身份失效。");
        var allowed = new List<ChatMessage>();
        foreach (var range in ReadValidatedRanges(connection, transaction, task.ConversationId))
        {
            var ancestor = ReadMessages(connection, transaction, range.ConversationId).ToDictionary(m => m.Id);
            foreach (var id in range.MessageIds) allowed.Add(ancestor[id]);
        }
        var frozenIds = allowed.Select(m => m.Id).ToArray();
        allowed.AddRange(own.Where(m => m.Sequence < answer.Sequence));
        var byId = allowed.ToDictionary(m => m.Id);
        if (task.InputMessageIds.Count == 0 || !task.InputMessageIds.Take(frozenIds.Length).SequenceEqual(frozenIds) ||
            task.InputMessageIds.Distinct().Count() != task.InputMessageIds.Count ||
            task.InputMessageIds.Any(id => !byId.ContainsKey(id) || IsActive(byId[id].Status)) ||
            task.InputMessageIds[^1] != answer.ReplyToUserId)
            throw new InvalidOperationException("已接受的原消息身份或来源范围失效。");
        var originals = task.InputMessageIds.Select(id => byId[id]).ToArray();
        var source = ReadBranchSource(connection, transaction, task.ConversationId);
        var expected = ReplyContextPlanner.Plan(task.ConversationId, new(originals, source?.Quote, source?.MessageId), task.Configuration);
        using var read = ChatCommand(connection, transaction, "SELECT plan_json FROM reply_context_plan WHERE task_id=$id", ("$id", taskId));
        // Compare the entire explicit format with authoritative reconstruction. This also rejects altered source/current-question protection.
        if (read.ExecuteScalar() is not string stored || stored != PlanJson(expected))
            throw new InvalidOperationException("上下文计划已损坏或与接受身份不符。");
        using var chunks = ChatCommand(connection, transaction, "SELECT cache_key,chunk_json FROM context_plan_chunk WHERE task_id=$id", ("$id", taskId));
        using var reader = chunks.ExecuteReader();
        var expectedChunks = expected.Parts.OfType<SummaryContextPart>().ToDictionary(p => p.Chunk.CacheKey, p => JsonSerializer.Serialize(p.Chunk));
        while (reader.Read())
            if (!expectedChunks.Remove(reader.GetString(0), out var json) || json != reader.GetString(1))
                throw new InvalidOperationException("计划片段已损坏。");
        if (expectedChunks.Count != 0) throw new InvalidOperationException("计划片段缺失。");
        return new(task, originals, expected);
    }
}
