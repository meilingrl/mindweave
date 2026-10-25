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

    public sealed record TextDelta(string Text) : ChatStreamEvent;

    /// <summary>The provider declared the answer finished. <paramref name="Truncated"/> when it stopped at the length limit.</summary>
    public sealed record Completed(bool Truncated) : ChatStreamEvent;
}

public sealed class ChatClientException : Exception
{
    public ChatClientException(GenerationErrorKind kind, string detail, Exception? inner = null)
        : base(detail, inner) => Kind = kind;

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
}

/// <summary>Streams one completion. Throws <see cref="ChatClientException"/> for provider and transport failures.</summary>
public interface IChatClient
{
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, CancellationToken cancellationToken);

    /// <summary>Lists model identifiers the service reports. Does not generate and should not be billed.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken);
}
