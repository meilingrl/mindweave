using System.Globalization;
using System.Text;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Export;

/// <summary>The optional source note of an exported version: where it came from, without any internal identity.</summary>
/// <param name="CoveredMessages">How many messages of the source conversation the version covers, counted from 1.</param>
/// <param name="CreatedAt">When the version was created, in the time zone it is to be shown in.</param>
public sealed record ExportSource(string ConversationName, int CoveredMessages, int VersionSeq, SummaryOrigin Origin, DateTimeOffset CreatedAt);

/// <summary>
/// What goes into an exported file: a format of its own, never a stored record (E5's rule for anything leaving the
/// app). It holds no model, connection, address, credential or ID, so the file cannot contain them (architecture §8.1).
/// </summary>
public sealed record SummaryExportDocument(string Title, string Body, ExportSource? Source);

/// <summary>
/// Formats an exported summary version (§8.3): <c># title</c>, the source note as a one-line quote, then the body
/// verbatim (formula delimiters such as <c>\(</c> and <c>\[</c> are not rewritten). UTF-8 without BOM, LF line ends,
/// ending in a line break.
/// </summary>
public static class SummaryMarkdown
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static byte[] Encode(SummaryExportDocument document) => Utf8NoBom.GetBytes(Format(document));

    public static string Format(SummaryExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = new StringBuilder().Append("# ").Append(OneLine(document.Title)).Append('\n');
        if (document.Source is { } source)
        {
            text.Append("\n> ").Append(SourceLine(source)).Append('\n');
        }

        var body = Lf(document.Body);
        if (body.Length > 0)
        {
            text.Append('\n').Append(body);
            if (!body.EndsWith('\n'))
            {
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    private static string SourceLine(ExportSource source) => string.Format(
        CultureInfo.InvariantCulture,
        "来源会话：{0} · 覆盖到第 {1} 条消息 · 第 {2} 版（{3}） · 创建于 {4:yyyy-MM-dd HH:mm} · 导出自 Mindweave",
        OneLine(source.ConversationName),
        source.CoveredMessages,
        source.VersionSeq,
        source.Origin == SummaryOrigin.Edited ? "手改" : "生成",
        source.CreatedAt);

    /// <summary>A heading or a quote line must stay one line, or the rest would fall out of it.</summary>
    private static string OneLine(string text) => Lf(text).Replace('\n', ' ').Trim();

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

/// <summary>The file name the save dialog suggests: the version title made safe on every platform, Windows included.</summary>
public static class ExportFileName
{
    /// <summary>Characters (text elements) kept from the title, well inside every file system's name limit.</summary>
    public const int MaxLength = 80;

    public const int MaxBytes = 240;

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Drops characters Windows refuses (<c>&lt;&gt;:"/\|?*</c>), turns control characters into spaces, cuts at
    /// <see cref="MaxLength"/>, strips leading dots (hidden files elsewhere) and trailing dots and spaces (which Windows
    /// drops), and keeps a device name (<c>CON</c>, <c>nul.txt</c>) from being one. Without an extension; empty becomes
    /// <c>summary-v{n}</c>.
    /// </summary>
    public static string Suggest(string title, int versionSeq)
    {
        ArgumentNullException.ThrowIfNull(title);
        var cleaned = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                continue;
            }

            cleaned.Append(char.IsControl(c) ? ' ' : c);
        }

        var name = string.Join(' ', cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        name = Trim(Cut(Trim(name)));
        if (name.Length == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"summary-v{versionSeq}");
        }

        // The dialog adds ".md"; a title that already ends with it would otherwise give "x.md.md".
        if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && name.Length > 3)
        {
            name = Trim(name[..^3]);
        }

        // Windows ignores spaces before the extension, so "AUX .txt" is still the device.
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase) ? stem + "_" + name[stem.Length..] : name;
    }

    private static string Trim(string name) => name.TrimStart('.', ' ').TrimEnd('.', ' ');

    private static string Cut(string name)
    {
        // At most MaxLength characters as the reader sees them, and at most MaxBytes in UTF-8 (file systems limit
        // names to 255 bytes, and the temporary name and extension need room too).
        var elements = StringInfo.GetTextElementEnumerator(name);
        var count = 0;
        var bytes = 0;
        while (elements.MoveNext())
        {
            bytes += Encoding.UTF8.GetByteCount(elements.GetTextElement());
            if (++count > MaxLength || bytes > MaxBytes)
            {
                return name[..elements.ElementIndex];
            }
        }

        return name;
    }
}
