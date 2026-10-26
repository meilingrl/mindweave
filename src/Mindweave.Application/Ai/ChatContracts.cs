using Mindweave.Core.Conversations;
using Mindweave.Core.Models;

namespace Mindweave.Application.Ai;

public enum ChatRole
{
    System,
    User,
    Assistant,
}

public sealed record ChatTurn(ChatRole Role, string Content);

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

    /// <summary>The provider declared the answer finished. <paramref name="Truncated"/> when it stopped at the length limit.</summary>
    public sealed record Completed(bool Truncated) : ChatStreamEvent;

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
