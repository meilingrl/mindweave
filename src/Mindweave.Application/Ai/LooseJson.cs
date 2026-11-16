using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mindweave.Application.Ai;

/// <summary>
/// Reads the JSON array of small objects a prompt asks a model for, as forgivingly as a model's output needs (pure; shared
/// by the recommendation and link-suggestion prompts, review 2026-11-13). Each object becomes its known fields by their
/// canonical names; a field whose value is a number keeps its digits (a label such as "2" for S2). Unknown fields are
/// ignored, and the first value of a field wins.
/// </summary>
public static partial class LooseJson
{
    /// <summary>The key a bare string element of the array is read as.</summary>
    public const string BareString = "";

    /// <summary>
    /// The objects of the first JSON array in <paramref name="output"/> (possibly in a code fence or after a lead-in); a
    /// string element is one object with <see cref="BareString"/> as its only field. Null when there is no array or it does
    /// not parse.
    /// </summary>
    public static List<Dictionary<string, string?>>? FromArray(string output, Func<string, string?> canonical)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(canonical);
        var start = output.IndexOf('[', StringComparison.Ordinal);
        var end = output.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(output[start..(end + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var list = new List<Dictionary<string, string?>>();
            foreach (var item in json.RootElement.EnumerateArray())
            {
                switch (item.ValueKind)
                {
                    case JsonValueKind.String:
                        list.Add(new Dictionary<string, string?>(StringComparer.Ordinal) { [BareString] = item.GetString() });
                        break;
                    case JsonValueKind.Object:
                        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
                        foreach (var property in item.EnumerateObject())
                        {
                            if (canonical(property.Name) is { } key && !fields.ContainsKey(key))
                            {
                                fields[key] = property.Value.ValueKind switch
                                {
                                    JsonValueKind.String => property.Value.GetString(),
                                    JsonValueKind.Number => property.Value.GetRawText(),
                                    _ => null,
                                };
                            }
                        }

                        list.Add(fields);
                        break;
                }
            }

            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// JSON that does not parse (most often ASCII quotes left unescaped inside Chinese text) read by its fields: in each
    /// object, a known key's value runs up to the next known key or the object's end, its enclosing quotes and trailing
    /// comma removed. <paramref name="key"/> matches a quoted key with its colon and captures the name in group 1. Null when
    /// the output has no such key.
    /// </summary>
    public static List<Dictionary<string, string?>>? FromFields(string output, Regex key, Func<string, string?> canonical)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(canonical);
        if (!key.IsMatch(output))
        {
            return null;
        }

        var list = new List<Dictionary<string, string?>>();
        foreach (Match block in JsonObject().Matches(output))
        {
            var body = block.Groups[1].Value;
            var keys = key.Matches(body);
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < keys.Count; i++)
            {
                var start = keys[i].Index + keys[i].Length;
                var end = i + 1 < keys.Count ? keys[i + 1].Index : body.Length;
                var value = body[start..end].Trim().TrimEnd(',').Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                {
                    value = value[1..^1];
                }

                if (canonical(keys[i].Groups[1].Value) is { } name)
                {
                    fields.TryAdd(name, value);
                }
            }

            if (fields.Count > 0)
            {
                list.Add(fields);
            }
        }

        return list;
    }

    /// <summary>One JSON object's body; the last one may lack its closing brace (cut-off output).</summary>
    [GeneratedRegex(@"\{([^{}]*)(?:\}|$)")]
    private static partial Regex JsonObject();
}
