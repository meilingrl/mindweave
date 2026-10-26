using System.Text.RegularExpressions;

namespace Mindweave.Application.Diagnostics;

/// <summary>
/// Removes credentials from text that leaves the request path: stored error details, log lines and anything
/// shown or exported as diagnostics. Applied where that text is produced, not as a later filter. The exact
/// secret is removed when known; credential-shaped tokens are removed regardless.
/// </summary>
public static partial class Redaction
{
    public const string Mask = "[redacted]";

    /// <summary>Shorter values are not treated as secrets; masking them would garble ordinary text.</summary>
    private const int MinSecretLength = 8;

    public static string Scrub(string text, string? knownSecret = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (knownSecret is { Length: >= MinSecretLength })
        {
            text = text.Replace(knownSecret, Mask, StringComparison.Ordinal);
        }

        text = BearerToken().Replace(text, "${scheme} " + Mask);
        text = PrefixedKey().Replace(text, Mask);
        return LabelledSecret().Replace(text, "${label}${sep}" + Mask);
    }

    [GeneratedRegex(@"(?<scheme>\b[Bb]earer)\s+[A-Za-z0-9._~+/=\-]{8,}")]
    private static partial Regex BearerToken();

    /// <summary>Vendor key formats such as sk-…, sk-ant-…, pk-…, rk-…, AIza….</summary>
    [GeneratedRegex(@"\b(?:(?:sk|pk|rk)-[A-Za-z0-9_\-]{8,}|AIza[A-Za-z0-9_\-]{20,})")]
    private static partial Regex PrefixedKey();

    [GeneratedRegex(@"(?<label>(?i:api[_\-]?key|access[_\-]?token|token|secret|password|authorization))(?<sep>[""']?\s*[:=]\s*[""']?)[A-Za-z0-9._~+/=\-]{8,}")]
    private static partial Regex LabelledSecret();
}
