namespace Mindweave.Core.Models;

/// <summary>One thing a model may or may not be able to do (a flag of <see cref="ModelCapabilities"/>).</summary>
public enum ModelCapability
{
    Streaming,

    /// <summary>Native tool (function) calling: the request may carry tools and the answer may ask for one.</summary>
    ToolCalls,

    /// <summary>A user turn may carry images.</summary>
    ImageInput,

    /// <summary>The service streams the model's reasoning apart from the answer.</summary>
    ReasoningOutput,

    /// <summary>The service reuses a repeated request prefix at a lower price without being asked.</summary>
    PromptCaching,
}

/// <summary>
/// What a configured model can do. Features decide by capability, never by vendor or model name, so a new
/// protocol or model only has to describe itself. Frozen into each task with the rest of the snapshot. Anything not
/// declared is off: a feature that needs a capability stays hidden or unused until the model is said to have it.
/// </summary>
public sealed record ModelCapabilities(
    bool Streaming,
    bool ToolCalls,
    bool ImageInput,
    bool ReasoningOutput,
    bool PromptCaching,
    int? ContextWindowTokens)
{
    /// <summary>Nothing at all: what a feature sees while no model is configured.</summary>
    public static ModelCapabilities None { get; } = new(false, false, false, false, false, null);

    /// <summary>What every service speaking <paramref name="protocol"/> is assumed to support until told otherwise.</summary>
    public static ModelCapabilities DefaultsFor(ChatProtocol protocol) => protocol switch
    {
        ChatProtocol.ChatCompletions => new(Streaming: true, ToolCalls: false, ImageInput: false, ReasoningOutput: false, PromptCaching: false, ContextWindowTokens: null),
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null),
    };

    /// <summary>Whether the model has <paramref name="capability"/>; the one question features ask.</summary>
    public bool Has(ModelCapability capability) => capability switch
    {
        ModelCapability.Streaming => Streaming,
        ModelCapability.ToolCalls => ToolCalls,
        ModelCapability.ImageInput => ImageInput,
        ModelCapability.ReasoningOutput => ReasoningOutput,
        ModelCapability.PromptCaching => PromptCaching,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null),
    };

    /// <summary>The same capabilities with <paramref name="capability"/> turned on or off.</summary>
    public ModelCapabilities With(ModelCapability capability, bool on) => capability switch
    {
        ModelCapability.Streaming => this with { Streaming = on },
        ModelCapability.ToolCalls => this with { ToolCalls = on },
        ModelCapability.ImageInput => this with { ImageInput = on },
        ModelCapability.ReasoningOutput => this with { ReasoningOutput = on },
        ModelCapability.PromptCaching => this with { PromptCaching = on },
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null),
    };
}
