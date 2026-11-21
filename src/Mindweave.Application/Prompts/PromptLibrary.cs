using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Mindweave.Application.Languages;

namespace Mindweave.Application.Prompts;

/// <summary>
/// The long prompts, one embedded file per language (docs/design/bilingual-ui.md §5): <c>Prompts/en/&lt;name&gt;.md</c> and
/// <c>Prompts/zh/&lt;name&gt;.md</c>. A prompt is read in the interface language at the moment its request is built. Each one
/// tells the model to write in the language of the conversation or material, so the interface language never decides the
/// language of a title or summary. Placeholders are written <c>{name}</c> and filled by <see cref="Fill"/>.
/// </summary>
public static partial class PromptLibrary
{
    /// <summary>The prompts, by file name.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "summary",
        "summary-branch-background",
        "summary-naming",
        "compression",
        "title",
        "attachment-digest",
        "attachment-digest-merge",
        "run",
        "recommendations",
        "link-suggestions",
        "workspace-names",
    ];

    private static readonly ConcurrentDictionary<(string Name, UiLanguage Language), string> Loaded = new();

    /// <summary>The prompt <paramref name="name"/> in the current interface language, placeholders unfilled.</summary>
    public static string Get(string name) => Get(name, CurrentLanguage.Value);

    /// <summary>The prompt <paramref name="name"/> in <paramref name="language"/>, placeholders unfilled.</summary>
    public static string Get(string name, UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Loaded.GetOrAdd((name, language), static key => Load(key.Name, key.Language));
    }

    /// <summary>The prompt <paramref name="name"/> in the current interface language with its placeholders filled.</summary>
    public static string Get(string name, params (string Name, string Value)[] values) => Fill(Get(name), values);

    /// <summary>
    /// Fills every <c>{name}</c> in <paramref name="template"/> in one pass, so a value that itself contains braces is left as
    /// it is. Throws when a placeholder has no value or a value has no placeholder: both are mistakes in the code.
    /// </summary>
    public static string Fill(string template, params (string Name, string Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        var byName = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var filled = Placeholder().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!byName.TryGetValue(key, out var value))
            {
                throw new InvalidOperationException($"No value for the prompt placeholder {{{key}}}.");
            }

            used.Add(key);
            return value;
        });
        if (byName.Keys.FirstOrDefault(k => !used.Contains(k)) is { } unused)
        {
            throw new InvalidOperationException($"The prompt has no placeholder {{{unused}}}.");
        }

        return filled;
    }

    /// <summary>The placeholder names in <paramref name="template"/>, in order of first appearance.</summary>
    public static IReadOnlyList<string> PlaceholdersOf(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return [.. Placeholder().Matches(template).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The prompt files embedded for <paramref name="language"/>, by name.</summary>
    public static IReadOnlyList<string> Embedded(UiLanguage language)
    {
        var prefix = ResourcePrefix(language);
        return [.. typeof(PromptLibrary).Assembly.GetManifestResourceNames()
            .Where(r => r.StartsWith(prefix, StringComparison.Ordinal) && r.EndsWith(".md", StringComparison.Ordinal))
            .Select(r => r[prefix.Length..^".md".Length])
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The file's text with line breaks as <c>\n</c> (whatever the checkout did) and without the one line break that ends
    /// the file: a prompt never ends with a line break of its own.
    /// </summary>
    private static string Load(string name, UiLanguage language)
    {
        var resource = ResourcePrefix(language) + name + ".md";
        using var stream = typeof(PromptLibrary).Assembly.GetManifestResourceStream(resource)
            ?? throw new ArgumentException($"There is no prompt named '{name}' for {language}.", nameof(name));
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        return text.EndsWith('\n') ? text[..^1] : text;
    }

    private static string ResourcePrefix(UiLanguage language) => language == UiLanguage.English ? "Prompts.en." : "Prompts.zh.";

    /// <summary>"{name}": lower-case letters, digits and hyphens. JSON in a prompt ({"a":…}) never matches.</summary>
    [GeneratedRegex(@"\{([a-z][a-z0-9-]*)\}")]
    private static partial Regex Placeholder();
}
