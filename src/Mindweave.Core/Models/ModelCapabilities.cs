namespace Mindweave.Core.Models;

/// <summary>
/// What a configured model can do. Features decide by capability, never by vendor or model name, so a new
/// protocol or model only has to describe itself. Frozen into each task with the rest of the snapshot.
/// </summary>
public sealed record ModelCapabilities(
    bool Streaming,
    bool ToolCalls,
    bool ImageInput,
    bool ReasoningOutput,
    bool PromptCaching,
    int? ContextWindowTokens)
{
    /// <summary>What every service speaking <paramref name="protocol"/> is assumed to support until told otherwise.</summary>
    public static ModelCapabilities DefaultsFor(ChatProtocol protocol) => protocol switch
    {
        ChatProtocol.ChatCompletions => new(Streaming: true, ToolCalls: false, ImageInput: false, ReasoningOutput: false, PromptCaching: false, ContextWindowTokens: null),
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null),
    };
}
