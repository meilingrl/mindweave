using Mindweave.Core.Conversations;
using Mindweave.Core.Models;

namespace Mindweave.Application.Ai;

public enum ChatRole
{
    System,
    User,
    Assistant,
}

/// <summary>An image sent as a part of a user turn (architecture §15.4): its media type and bytes, sent as a data URL.</summary>
public sealed record ChatImage(string MediaType, byte[] Data);

/// <summary>
/// One turn. <paramref name="Images"/> go with a user turn only, and only to a model that declares
/// <see cref="ModelCapability.ImageInput"/>; an adapter sends them as image parts after <paramref name="Content"/>.
/// </summary>
public sealed record ChatTurn(ChatRole Role, string Content, IReadOnlyList<ChatImage>? Images = null)
{
    public bool HasImages => Images is { Count: > 0 };
}

public static class ChatTurns
{
    /// <summary>The estimate of a request as sent: its texts plus a fixed cost per image (architecture §15.4).</summary>
    public static int Estimate(IReadOnlyList<ChatTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        return Core.Context.TokenEstimator.Request(turns.Select(t => t.Content)) + (turns.Sum(t => t.Images?.Count ?? 0) * Core.Context.ResourceMaterial.ImageTokens);
    }
}

/// <summary>Provider-neutral request. Adapters translate it; no SDK types cross this boundary.</summary>
public sealed record ChatRequest(ModelSnapshot Model, IReadOnlyList<ChatTurn> Turns);

public abstract record ChatStreamEvent
{
    private ChatStreamEvent()
    {
    }

    /// <summary>The service accepted the request; first event of every stream. Diagnostic only.</summary>
    public sealed record ResponseStarted(int HttpStatus, string? ProviderRequestId) : ChatStreamEvent;

    public sealed record TextDelta(string Text) : ChatStreamEvent;

    /// <summary>
    /// A reasoning model sent part of its reasoning (<c>reasoning_content</c>) instead of answer text. Only its length is
    /// passed on: the reasoning is neither shown nor stored, it only says the service is working (2026-11-06).
    /// </summary>
    public sealed record ReasoningDelta(int Characters) : ChatStreamEvent;

    /// <summary>
    /// The provider declared the answer finished. <paramref name="Truncated"/> when it stopped before the answer was whole:
    /// at the output limit, or (<paramref name="Filtered"/>, health check 2026-11-12) because its content filter stopped it.
    /// </summary>
    public sealed record Completed(bool Truncated, bool Filtered = false) : ChatStreamEvent;

    /// <summary>Token counts the service reported, if it did (usually with the last chunk). Diagnostic only.</summary>
    public sealed record Usage(int? PromptTokens, int? CompletionTokens) : ChatStreamEvent;
}

public sealed class ChatClientException : Exception
{
    public ChatClientException(GenerationErrorKind kind, string detail, Exception? inner = null, int? httpStatus = null)
        : base(detail, inner)
    {
        Kind = kind;
        HttpStatus = httpStatus;
    }

    public ChatClientException()
    {
    }

    public ChatClientException(string message)
        : base(message)
    {
    }

    public ChatClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GenerationErrorKind Kind { get; } = GenerationErrorKind.InvalidResponse;

    /// <summary>The service's HTTP status when it answered with an error; null for transport failures.</summary>
    public int? HttpStatus { get; }
}

/// <summary>
/// Streams one completion. Throws <see cref="ChatClientException"/> for provider and transport failures; its
/// message is already redacted and safe to store or show.
/// </summary>
public interface IChatClient
{
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, CancellationToken cancellationToken);

    /// <summary>Lists model identifiers the service reports. Does not generate and should not be billed.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken);
}
