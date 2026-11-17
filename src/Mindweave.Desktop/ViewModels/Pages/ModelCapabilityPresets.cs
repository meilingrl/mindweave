using System.Text.RegularExpressions;
using Mindweave.Core.Models;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// What the settings page fills in for 工具调用, 图片输入 and the flags not shown, by service and model name. Vendor and
/// model names live only here (E4): features ask <see cref="ModelCapabilities.Has"/>. Every rule comes from the
/// provider's official documentation as read on 2026-11-17 (docs/design/model-capabilities.md, with sources and what
/// still needs a real call). A model no rule knows, and any service that is not a known one, gets the protocol defaults:
/// unknown is off, and the user can turn a flag on.
/// </summary>
public static class ModelCapabilityPresets
{
    private static readonly ModelCapabilities Off = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions);

    /// <summary>One rule: a model name pattern (whole name, case-insensitive) and what such a model can do.</summary>
    private sealed record Rule(Regex Pattern, bool Tools, bool Images, bool Reasoning, bool Caching)
    {
        public ModelCapabilities Capabilities => Off with { ToolCalls = Tools, ImageInput = Images, ReasoningOutput = Reasoning, PromptCaching = Caching };
    }

    private static Rule R(string pattern, bool tools, bool images, bool reasoning, bool caching) =>
        new(new Regex($"^(?:{pattern})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), tools, images, reasoning, caching);

    // First match wins within a family.
    private static readonly Rule[] DeepSeek =
    [
        R(@"deepseek-flash.*|deepseek-v4-flash.*", tools: true, images: true, reasoning: true, caching: true),
        R(@"deepseek-v4-pro.*", tools: true, images: false, reasoning: true, caching: true),
        R(@"deepseek-chat", tools: true, images: false, reasoning: false, caching: true),
        R(@"deepseek-reasoner", tools: true, images: false, reasoning: true, caching: true),
    ];

    private static readonly Rule[] Kimi =
    [
        R(@"kimi-k3.*", tools: true, images: true, reasoning: true, caching: true),
        R(@"kimi-k2.*", tools: true, images: false, reasoning: true, caching: true),
    ];

    private static readonly Rule[] Glm =
    [
        // Vision models with tools: GLM-5.3-Flash, GLM-4.6V, AutoGLM-Phone.
        R(@"glm-5\.3-flash|glm-4\.6v|autoglm-phone", tools: true, images: true, reasoning: true, caching: true),
        // Other vision models: images, no tools (glm-4v-flash takes no base64 images, so it stays off entirely).
        R(@"glm-4v-flash", tools: false, images: false, reasoning: false, caching: true),
        R(@"glm-4\.1v-thinking.*", tools: false, images: true, reasoning: true, caching: true),
        R(@"glm-\d+(?:\.\d+)?v.*", tools: false, images: true, reasoning: true, caching: true),
        // Text models GLM-4.5 to GLM-5.x.
        R(@"glm-4\.[5-9].*|glm-5.*", tools: true, images: false, reasoning: true, caching: true),
        R(@"glm-4-flash.*", tools: true, images: false, reasoning: false, caching: true),
    ];

    private static readonly Rule[] OpenAI =
    [
        // GPT-6 on Chat Completions: images yes, function calling only through the Responses protocol.
        R(@"gpt-6.*", tools: false, images: true, reasoning: false, caching: true),
        R(@"gpt-4o.*|gpt-4\.1.*|gpt-5.*", tools: true, images: true, reasoning: false, caching: true),
    ];

    private static readonly Rule[] Claude =
    [
        // Anthropic's OpenAI-compatible layer: images and tools kept, reasoning and caching lost.
        R(@"claude-.*", tools: true, images: true, reasoning: false, caching: false),
    ];

    private static readonly Rule[] Qwen =
    [
        R(@"qwen4-max.*|qwen3\.7-plus.*", tools: true, images: true, reasoning: true, caching: true),
        R(@"qwen3-vl-.*", tools: true, images: true, reasoning: true, caching: true),
        R(@"qwen-vl-.*|qwen.*-vl.*", tools: false, images: true, reasoning: false, caching: true),
        R(@"qwen4-.*|qwen3\.[5-8]-(?:max|plus|flash).*", tools: true, images: false, reasoning: true, caching: true),
        R(@"qwen-(?:max|plus|flash|turbo).*", tools: true, images: false, reasoning: false, caching: true),
    ];

    /// <summary>
    /// The suggestion for <paramref name="model"/> on the service at <paramref name="baseUrl"/>. Relays that host other
    /// vendors' models under <c>vendor/model</c> names (OpenRouter, SiliconFlow) use that vendor's rules for the part
    /// after the slash; their caching is not automatic for every vendor, so it is left off there.
    /// </summary>
    public static ModelCapabilities Suggest(string? baseUrl, string? model)
    {
        var name = (model ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return Off;
        }

        var preset = ProviderPresets.Match(baseUrl);
        if (preset == ProviderPresets.DeepSeek)
        {
            return First(DeepSeek, name);
        }

        if (preset == ProviderPresets.Moonshot)
        {
            return First(Kimi, name);
        }

        if (preset == ProviderPresets.Zhipu)
        {
            return First(Glm, name);
        }

        if (preset == ProviderPresets.OpenAI)
        {
            return First(OpenAI, name);
        }

        if (preset == ProviderPresets.DashScope)
        {
            // DashScope also hosts other vendors' models under their own names.
            return First([.. Qwen, .. Kimi, .. Glm, .. DeepSeek], name);
        }

        if (preset == ProviderPresets.OpenRouter || preset == ProviderPresets.SiliconFlow)
        {
            return ByHostedName(name) with { PromptCaching = false };
        }

        if (IsAnthropic(baseUrl))
        {
            return First(Claude, name);
        }

        // Ollama (depends on the local model) and any other address: unknown, so off.
        return Off;
    }

    /// <summary><c>vendor/model</c> (optionally <c>Pro/vendor/model</c>) judged by the vendor's own rules.</summary>
    private static ModelCapabilities ByHostedName(string name)
    {
        var slash = name.LastIndexOf('/');
        var bare = slash < 0 ? name : name[(slash + 1)..];
        return First([.. DeepSeek, .. Kimi, .. Glm, .. OpenAI, .. Claude, .. Qwen], bare);
    }

    private static ModelCapabilities First(IEnumerable<Rule> rules, string name) =>
        rules.FirstOrDefault(r => r.Pattern.IsMatch(name))?.Capabilities ?? Off;

    private static bool IsAnthropic(string? baseUrl) =>
        Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "api.anthropic.com", StringComparison.OrdinalIgnoreCase);
}
