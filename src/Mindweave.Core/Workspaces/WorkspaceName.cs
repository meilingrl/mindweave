using System.Globalization;
using System.Text;

namespace Mindweave.Core.Workspaces;

public enum WorkspaceNameError
{
    Empty,
    TooLong,
}

/// <summary>A validated, whitespace-normalized workspace name.</summary>
public sealed record WorkspaceName
{
    public const int MaxLength = 80;

    private WorkspaceName(string value) => Value = value;

    public string Value { get; }

    /// <summary>
    /// Trims, applies Unicode NFC and collapses internal whitespace runs to one space.
    /// Length is counted in text elements so a CJK or emoji name is not penalized for UTF-16 width.
    /// </summary>
    public static bool TryCreate(string? raw, out WorkspaceName? name, out WorkspaceNameError? error)
    {
        name = null;
        error = null;
        var normalized = Normalize(raw ?? string.Empty);
        if (normalized.Length == 0)
        {
            error = WorkspaceNameError.Empty;
            return false;
        }

        if (new StringInfo(normalized).LengthInTextElements > MaxLength)
        {
            error = WorkspaceNameError.TooLong;
            return false;
        }

        name = new WorkspaceName(normalized);
        return true;
    }

    /// <summary>Rehydrates a name that was validated before it was stored.</summary>
    public static WorkspaceName FromStored(string value) => new(value);

    public override string ToString() => Value;

    private static string Normalize(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var ch in raw.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
