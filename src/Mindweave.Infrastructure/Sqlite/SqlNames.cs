using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>Explicit enum ↔ column value mapping, so renaming a C# member can never silently change stored data.</summary>
internal static class SqlNames
{
    public static string Of(MessageRole role) => role switch
    {
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static MessageRole ParseRole(string value) => value switch
    {
        "user" => MessageRole.User,
        "assistant" => MessageRole.Assistant,
        _ => throw new InvalidDataException($"Unknown role '{value}'."),
    };

    public static string Of(MessageState state) => state switch
    {
        MessageState.Complete => "complete",
        MessageState.Pending => "pending",
        MessageState.Streaming => "streaming",
        MessageState.Stopped => "stopped",
        MessageState.Failed => "failed",
        MessageState.Interrupted => "interrupted",
        MessageState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static MessageState ParseMessageState(string value) => value switch
    {
        "complete" => MessageState.Complete,
        "pending" => MessageState.Pending,
        "streaming" => MessageState.Streaming,
        "stopped" => MessageState.Stopped,
        "failed" => MessageState.Failed,
        "interrupted" => MessageState.Interrupted,
        "cancelled" => MessageState.Cancelled,
        _ => throw new InvalidDataException($"Unknown message state '{value}'."),
    };

    public static string Of(TitleSource source) => source switch
    {
        TitleSource.None => "none",
        TitleSource.Auto => "auto",
        TitleSource.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static TitleSource ParseTitleSource(string value) => value switch
    {
        "none" => TitleSource.None,
        "auto" => TitleSource.Auto,
        "manual" => TitleSource.Manual,
        _ => throw new InvalidDataException($"Unknown title source '{value}'."),
    };

    public static string Of(GenerationTaskKind kind) => kind switch
    {
        GenerationTaskKind.Answer => "answer",
        GenerationTaskKind.Title => "title",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Of(GenerationTaskState state) => state switch
    {
        GenerationTaskState.Queued => "queued",
        GenerationTaskState.Running => "running",
        GenerationTaskState.Completed => "completed",
        GenerationTaskState.Stopped => "stopped",
        GenerationTaskState.Failed => "failed",
        GenerationTaskState.Interrupted => "interrupted",
        GenerationTaskState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static GenerationTaskState TaskStateFor(MessageState answerState) => answerState switch
    {
        MessageState.Complete => GenerationTaskState.Completed,
        MessageState.Stopped => GenerationTaskState.Stopped,
        MessageState.Failed => GenerationTaskState.Failed,
        MessageState.Interrupted => GenerationTaskState.Interrupted,
        MessageState.Cancelled => GenerationTaskState.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(answerState), answerState, "Not a terminal answer state."),
    };

    public static string Of(GenerationErrorKind kind) => kind switch
    {
        GenerationErrorKind.Unauthorized => "unauthorized",
        GenerationErrorKind.QuotaOrRateLimited => "quota_or_rate_limited",
        GenerationErrorKind.ContextTooLong => "context_too_long",
        GenerationErrorKind.ServerError => "server_error",
        GenerationErrorKind.Network => "network",
        GenerationErrorKind.Timeout => "timeout",
        GenerationErrorKind.InvalidResponse => "invalid_response",
        GenerationErrorKind.CredentialUnavailable => "credential_unavailable",
        GenerationErrorKind.NotConfigured => "not_configured",
        GenerationErrorKind.StorageUnavailable => "storage_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Unknown values (written by a newer build) read as a generic invalid response rather than failing.</summary>
    public static GenerationErrorKind ParseErrorKind(string value) => value switch
    {
        "unauthorized" => GenerationErrorKind.Unauthorized,
        "quota_or_rate_limited" => GenerationErrorKind.QuotaOrRateLimited,
        "context_too_long" => GenerationErrorKind.ContextTooLong,
        "server_error" => GenerationErrorKind.ServerError,
        "network" => GenerationErrorKind.Network,
        "timeout" => GenerationErrorKind.Timeout,
        "credential_unavailable" => GenerationErrorKind.CredentialUnavailable,
        "not_configured" => GenerationErrorKind.NotConfigured,
        "storage_unavailable" => GenerationErrorKind.StorageUnavailable,
        _ => GenerationErrorKind.InvalidResponse,
    };

    public static string Of(ChatProtocol protocol) => protocol switch
    {
        ChatProtocol.ChatCompletions => "chat_completions",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    public static ChatProtocol ParseProtocol(string value) => value switch
    {
        "chat_completions" => ChatProtocol.ChatCompletions,
        _ => throw new InvalidDataException($"Unknown protocol '{value}'."),
    };
}
