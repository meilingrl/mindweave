namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// A well-known OpenAI-compatible Chat Completions service the settings page can fill in. Only the base address
/// (the part before <c>/chat/completions</c>) is filled: model names change too often to guess, and the fetched list
/// shows them. <see cref="BaseUrl"/> is null for "custom", which keeps whatever address is typed.
/// </summary>
public sealed record ProviderPreset(string Name, string? BaseUrl, string? KeyPlaceholder = null, params string[] Aliases)
{
    public bool IsCustom => BaseUrl is null;

    public override string ToString() => Name;
}

/// <summary>
/// The preset list. Provider names live only in the desktop project: Core and Application decide by declared
/// capability, never by vendor (E4, <c>SeamTests</c>). Each address is the provider's documented OpenAI-compatible base.
/// </summary>
public static class ProviderPresets
{
    public static ProviderPreset DeepSeek { get; } = new("DeepSeek", "https://api.deepseek.com", null, "https://api.deepseek.com/v1");

    public static ProviderPreset OpenAI { get; } = new("OpenAI", "https://api.openai.com/v1");

    public static ProviderPreset DashScope { get; } = new(UiText.ProviderDashScope, "https://dashscope.aliyuncs.com/compatible-mode/v1");

    public static ProviderPreset Moonshot { get; } = new(UiText.ProviderMoonshot, "https://api.moonshot.cn/v1");

    public static ProviderPreset Zhipu { get; } = new(UiText.ProviderZhipu, "https://open.bigmodel.cn/api/paas/v4");

    public static ProviderPreset OpenRouter { get; } = new("OpenRouter", "https://openrouter.ai/api/v1");

    public static ProviderPreset SiliconFlow { get; } = new(UiText.ProviderSiliconFlow, "https://api.siliconflow.cn/v1");

    /// <summary>Ollama on this machine does not check the key, but a key is still required to save (any text will do).</summary>
    public static ProviderPreset Ollama { get; } = new(UiText.ProviderOllama, "http://localhost:11434/v1", UiText.OllamaKeyPlaceholder, "http://127.0.0.1:11434/v1");

    public static ProviderPreset Custom { get; } = new(UiText.ProviderCustom, null);

    public static IReadOnlyList<ProviderPreset> All { get; } =
        [DeepSeek, OpenAI, DashScope, Moonshot, Zhipu, OpenRouter, SiliconFlow, Ollama, Custom];

    /// <summary>The preset whose address (or an alias) is <paramref name="baseUrl"/>, ignoring case of scheme and host and a trailing slash.</summary>
    public static ProviderPreset? Match(string? baseUrl)
    {
        var key = Normalize(baseUrl);
        if (key is null)
        {
            return null;
        }

        return All.FirstOrDefault(p => p.BaseUrl is { } url && (Normalize(url) == key || p.Aliases.Any(a => Normalize(a) == key)));
    }

    private static string? Normalize(string? url)
    {
        if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        // Scheme and host come back lower-case from Uri; the path is compared as typed.
        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
    }
}
