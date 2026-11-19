using System.Text.Json;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Core.Summaries;

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
        GenerationTaskKind.Summary => "summary",
        GenerationTaskKind.Naming => "naming",
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

    public static GenerationTaskState ParseTaskState(string value) => value switch
    {
        "queued" => GenerationTaskState.Queued,
        "running" => GenerationTaskState.Running,
        "completed" => GenerationTaskState.Completed,
        "stopped" => GenerationTaskState.Stopped,
        "failed" => GenerationTaskState.Failed,
        "interrupted" => GenerationTaskState.Interrupted,
        "cancelled" => GenerationTaskState.Cancelled,
        _ => throw new InvalidDataException($"Unknown task state '{value}'."),
    };

    public static string Of(SummaryOrigin origin) => origin switch
    {
        SummaryOrigin.Generated => "generated",
        SummaryOrigin.Edited => "edited",
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    public static SummaryOrigin ParseOrigin(string value) => value switch
    {
        "generated" => SummaryOrigin.Generated,
        "edited" => SummaryOrigin.Edited,
        _ => throw new InvalidDataException($"Unknown summary origin '{value}'."),
    };

    public static string Of(NameSource source) => source switch
    {
        NameSource.Base => "base",
        NameSource.Auto => "auto",
        NameSource.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static NameSource ParseNameSource(string value) => value switch
    {
        "base" => NameSource.Base,
        "auto" => NameSource.Auto,
        "manual" => NameSource.Manual,
        _ => throw new InvalidDataException($"Unknown name source '{value}'."),
    };

    public static string Of(ReadSource source) => source switch
    {
        ReadSource.Progress => "progress",
        ReadSource.Manual => "manual",
        ReadSource.Edit => "edit",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static ReadSource ParseReadSource(string value) => value switch
    {
        "progress" => ReadSource.Progress,
        "manual" => ReadSource.Manual,
        "edit" => ReadSource.Edit,
        _ => throw new InvalidDataException($"Unknown read source '{value}'."),
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
        GenerationErrorKind.CompressionFailed => "compression_failed",
        GenerationErrorKind.OutputLimit => "output_limit",
        GenerationErrorKind.ContentFiltered => "content_filtered",
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
        "compression_failed" => GenerationErrorKind.CompressionFailed,
        "output_limit" => GenerationErrorKind.OutputLimit,
        "content_filtered" => GenerationErrorKind.ContentFiltered,
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

    public const string TextBlockKind = "text";

    public const string AttachmentBlockKind = "attachment";

    public const string ToolCallBlockKind = "tool_call";

    public const string ToolResultBlockKind = "tool_result";

    public const string CitationBlockKind = "citation";

    public static string Of(Core.Runs.Approval approval) => approval switch
    {
        Core.Runs.Approval.NotNeeded => "not_needed",
        Core.Runs.Approval.Pending => "pending",
        Core.Runs.Approval.Approved => "approved",
        Core.Runs.Approval.Denied => "denied",
        _ => throw new ArgumentOutOfRangeException(nameof(approval)),
    };

    public static Core.Runs.Approval? ParseApproval(string? value) => value switch
    {
        "not_needed" => Core.Runs.Approval.NotNeeded,
        "pending" => Core.Runs.Approval.Pending,
        "approved" => Core.Runs.Approval.Approved,
        "denied" => Core.Runs.Approval.Denied,
        _ => null,
    };

    public static string Of(Core.Runs.RunStepKind kind) => kind == Core.Runs.RunStepKind.Model ? "model" : "tool";

    public static Core.Runs.RunStepKind ParseStepKind(string value) => value switch
    {
        "model" => Core.Runs.RunStepKind.Model,
        "tool" => Core.Runs.RunStepKind.Tool,
        _ => throw new InvalidDataException($"Unknown run step kind '{value}'."),
    };

    public static string Of(Core.Runs.RunStepState state) => state switch
    {
        Core.Runs.RunStepState.Running => "running",
        Core.Runs.RunStepState.Completed => "completed",
        Core.Runs.RunStepState.Failed => "failed",
        Core.Runs.RunStepState.Stopped => "stopped",
        Core.Runs.RunStepState.Denied => "denied",
        Core.Runs.RunStepState.AwaitingApproval => "awaiting_approval",
        Core.Runs.RunStepState.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static Core.Runs.RunStepState ParseStepState(string value) => value switch
    {
        "running" => Core.Runs.RunStepState.Running,
        "completed" => Core.Runs.RunStepState.Completed,
        "failed" => Core.Runs.RunStepState.Failed,
        "stopped" => Core.Runs.RunStepState.Stopped,
        "denied" => Core.Runs.RunStepState.Denied,
        "awaiting_approval" => Core.Runs.RunStepState.AwaitingApproval,
        "unknown" => Core.Runs.RunStepState.Unknown,
        _ => throw new InvalidDataException($"Unknown run step state '{value}'."),
    };

    public static string Of(Core.Runs.ToolRisk risk) => risk switch
    {
        Core.Runs.ToolRisk.LocalRead => "local_read",
        Core.Runs.ToolRisk.NetworkRead => "network_read",
        Core.Runs.ToolRisk.SideEffect => "side_effect",
        _ => throw new ArgumentOutOfRangeException(nameof(risk)),
    };

    /// <summary>An unknown risk reads as a side effect: the more careful reading.</summary>
    public static Core.Runs.ToolRisk ParseRisk(string? value) => value switch
    {
        "local_read" => Core.Runs.ToolRisk.LocalRead,
        "network_read" => Core.Runs.ToolRisk.NetworkRead,
        _ => Core.Runs.ToolRisk.SideEffect,
    };

    public static string Of(Core.Runs.ToolResultStatus status) => status switch
    {
        Core.Runs.ToolResultStatus.Succeeded => "succeeded",
        Core.Runs.ToolResultStatus.Failed => "failed",
        Core.Runs.ToolResultStatus.Denied => "denied",
        Core.Runs.ToolResultStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static Core.Runs.ToolResultStatus ParseResultStatus(string? value) => value switch
    {
        "succeeded" => Core.Runs.ToolResultStatus.Succeeded,
        "denied" => Core.Runs.ToolResultStatus.Denied,
        "skipped" => Core.Runs.ToolResultStatus.Skipped,
        _ => Core.Runs.ToolResultStatus.Failed,
    };

    public static string Of(Core.Runs.RunLimit limit) => limit switch
    {
        Core.Runs.RunLimit.ModelRequests => "model_requests",
        Core.Runs.RunLimit.ToolCalls => "tool_calls",
        Core.Runs.RunLimit.Duration => "duration",
        Core.Runs.RunLimit.Tokens => "tokens",
        _ => throw new ArgumentOutOfRangeException(nameof(limit)),
    };

    public static Core.Runs.RunLimit? ParseRunLimit(string? value) => value switch
    {
        "model_requests" => Core.Runs.RunLimit.ModelRequests,
        "tool_calls" => Core.Runs.RunLimit.ToolCalls,
        "duration" => Core.Runs.RunLimit.Duration,
        "tokens" => Core.Runs.RunLimit.Tokens,
        _ => null,
    };

    public static string Of(ResourceOrigin origin) => origin switch
    {
        ResourceOrigin.Paste => "paste",
        ResourceOrigin.File => "file",
        ResourceOrigin.Image => "image",
        ResourceOrigin.Tool => "tool",
        ResourceOrigin.Web => "web",
        ResourceOrigin.Mcp => "mcp",
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    public static ResourceOrigin ParseResourceOrigin(string value) => value switch
    {
        "paste" => ResourceOrigin.Paste,
        "file" => ResourceOrigin.File,
        "image" => ResourceOrigin.Image,
        "tool" => ResourceOrigin.Tool,
        "web" => ResourceOrigin.Web,
        "mcp" => ResourceOrigin.Mcp,
        _ => throw new InvalidDataException($"Unknown resource origin '{value}'."),
    };

    public static string Of(ResourceProvenance provenance) => provenance switch
    {
        ResourceProvenance.User => "user",
        ResourceProvenance.Workspace => "workspace",
        ResourceProvenance.External => "external",
        _ => throw new ArgumentOutOfRangeException(nameof(provenance)),
    };

    public static ResourceProvenance ParseProvenance(string value) => value switch
    {
        "user" => ResourceProvenance.User,
        "workspace" => ResourceProvenance.Workspace,
        "external" => ResourceProvenance.External,
        _ => throw new InvalidDataException($"Unknown resource provenance '{value}'."),
    };

    public static string Of(Core.Context.ResourceForm form) => form switch
    {
        Core.Context.ResourceForm.Whole => "whole",
        Core.Context.ResourceForm.Summary => "summary",
        Core.Context.ResourceForm.Parts => "parts",
        Core.Context.ResourceForm.Image => "image",
        Core.Context.ResourceForm.ImageOmitted => "image_omitted",
        Core.Context.ResourceForm.ImageUnsupported => "image_unsupported",
        Core.Context.ResourceForm.ImageMissing => "image_missing",
        _ => throw new ArgumentOutOfRangeException(nameof(form)),
    };

    public static Core.Context.ResourceForm ParseResourceForm(string value) => value switch
    {
        "whole" => Core.Context.ResourceForm.Whole,
        "summary" => Core.Context.ResourceForm.Summary,
        "parts" => Core.Context.ResourceForm.Parts,
        "image" => Core.Context.ResourceForm.Image,
        "image_omitted" => Core.Context.ResourceForm.ImageOmitted,
        "image_unsupported" => Core.Context.ResourceForm.ImageUnsupported,
        "image_missing" => Core.Context.ResourceForm.ImageMissing,
        _ => throw new InvalidDataException($"Unknown resource form '{value}'."),
    };

    public static string Of(DerivativeKind kind) => kind switch
    {
        DerivativeKind.Text => "text",
        DerivativeKind.Summary => "summary",
        DerivativeKind.ImageScaled => "image_scaled",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static DerivativeKind ParseDerivativeKind(string value) => value switch
    {
        "text" => DerivativeKind.Text,
        "summary" => DerivativeKind.Summary,
        "image_scaled" => DerivativeKind.ImageScaled,
        _ => throw new InvalidDataException($"Unknown derivative kind '{value}'."),
    };

    public static string Of(AiRequestOutcome outcome) => outcome switch
    {
        AiRequestOutcome.Completed => "completed",
        AiRequestOutcome.Truncated => "truncated",
        AiRequestOutcome.Stopped => "stopped",
        AiRequestOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public static AiRequestOutcome ParseOutcome(string value) => value switch
    {
        "completed" => AiRequestOutcome.Completed,
        "truncated" => AiRequestOutcome.Truncated,
        "stopped" => AiRequestOutcome.Stopped,
        "failed" => AiRequestOutcome.Failed,
        _ => throw new InvalidDataException($"Unknown request outcome '{value}'."),
    };

    private static readonly JsonSerializerOptions CapabilityJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static string Of(ModelCapabilities capabilities) => JsonSerializer.Serialize(capabilities, CapabilityJson);

    /// <summary>NULL (rows from before capabilities existed) reads as the protocol defaults.</summary>
    public static ModelCapabilities ParseCapabilities(string? json, ChatProtocol protocol) =>
        json is null
            ? ModelCapabilities.DefaultsFor(protocol)
            : JsonSerializer.Deserialize<ModelCapabilities>(json, CapabilityJson) ?? throw new InvalidDataException("Empty capabilities.");
}
