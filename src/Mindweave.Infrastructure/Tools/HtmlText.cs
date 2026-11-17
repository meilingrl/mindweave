using System.Net;
using System.Text;

namespace Mindweave.Infrastructure.Tools;

/// <summary>
/// The readable text of an HTML page (decision W13: a small extractor of our own, no HTML library). A single forward scanner
/// turns the markup into tokens (no regular expressions, so no backtracking on hostile input); scripts, styles, navigation,
/// page headers and footers, forms and the like are dropped; when an <c>article</c> or <c>main</c> holds enough text only it
/// is kept. Block elements become line breaks, headings Markdown headings and list items "- " lines. Links are collected from
/// the whole document before anything is dropped.
/// </summary>
internal static class HtmlText
{
    /// <summary>An <c>article</c> or <c>main</c> with less text than this is probably not the page's content.</summary>
    internal const int MinMainText = 200;

    internal const int MaxLinks = 500;

    internal const int MaxText = 2_000_000;

    private const int MaxTitle = 300;

    private static readonly HashSet<string> Removed = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "template", "svg", "iframe", "object", "head", "nav", "header", "footer", "aside",
        "form", "button", "select", "title", "math", "canvas", "dialog", "noembed", "noframes",
    };

    /// <summary>Elements whose content is not markup: read up to their end tag as text.</summary>
    private static readonly HashSet<string> RawText = new(StringComparer.Ordinal)
    {
        "script", "style", "title", "textarea", "xmp", "noscript", "noembed", "noframes", "iframe", "plaintext",
    };

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>Elements that start and end a paragraph (a blank line).</summary>
    private static readonly HashSet<string> Paragraphs = new(StringComparer.Ordinal)
    {
        "p", "blockquote", "pre", "table", "section", "article", "main", "ul", "ol", "dl", "figure", "hr", "h1", "h2", "h3",
        "h4", "h5", "h6", "address", "fieldset", "details",
    };

    /// <summary>Elements that start and end a line.</summary>
    private static readonly HashSet<string> Lines = new(StringComparer.Ordinal)
    {
        "div", "br", "li", "tr", "dd", "dt", "figcaption", "caption", "summary", "legend", "center", "thead", "tbody", "tfoot",
        "body", "html", "option", "label",
    };

    /// <summary>Attributes kept by the scanner; the rest are skipped.</summary>
    private static readonly HashSet<string> KeptAttributes = new(StringComparer.Ordinal) { "a", "base", "meta" };

    public static (string Title, string Text, IReadOnlyList<Uri> Links) Extract(string html, Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(baseUrl);
        var tokens = Tokenize(html);
        var match = MatchEnds(tokens);
        var links = LinksOf(tokens, baseUrl);
        var text = MainText(tokens, match);
        var title = TitleOf(tokens, match);
        if (title.Length == 0)
        {
            title = AddressGuard.HostOf(baseUrl);
        }

        return (title, text, links);
    }

    private enum Kind
    {
        Text,
        Start,
        End,
    }

    /// <summary>A text run (raw, entities not yet decoded), or a start or end tag with its lowercase name.</summary>
    private sealed record Token(Kind Kind, string Name, string Text, bool SelfClosing, Dictionary<string, string>? Attributes)
    {
        public string? Attribute(string name) => Attributes is not null && Attributes.TryGetValue(name, out var v) ? v : null;
    }

    private static List<Token> Tokenize(string html)
    {
        var tokens = new List<Token>();
        var n = html.Length;
        var i = 0;
        while (i < n)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                AddText(tokens, html, i, n);
                break;
            }

            AddText(tokens, html, i, lt);
            if (lt + 1 >= n)
            {
                AddText(tokens, html, lt, n);
                break;
            }

            var next = html[lt + 1];
            if (next == '!')
            {
                if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
                {
                    var close = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    i = close < 0 ? n : close + 3;
                }
                else
                {
                    i = SkipPast(html, lt, '>');
                }
            }
            else if (next == '?')
            {
                i = SkipPast(html, lt, '>');
            }
            else if (next == '/')
            {
                var (name, after) = ReadName(html, lt + 2);
                i = SkipPast(html, after, '>');
                if (name.Length > 0)
                {
                    tokens.Add(new Token(Kind.End, name, "", false, null));
                }
            }
            else if (char.IsAsciiLetter(next))
            {
                i = ReadStartTag(html, lt, tokens);
            }
            else
            {
                AddText(tokens, html, lt, lt + 1);
                i = lt + 1;
            }
        }

        return tokens;
    }

    private static void AddText(List<Token> tokens, string html, int start, int end)
    {
        if (end > start)
        {
            tokens.Add(new Token(Kind.Text, "", html[start..end], false, null));
        }
    }

    private static int SkipPast(string html, int from, char c)
    {
        var at = html.IndexOf(c, from);
        return at < 0 ? html.Length : at + 1;
    }

    private static (string Name, int After) ReadName(string html, int start)
    {
        var i = start;
        while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('/' or '>' or '<'))
        {
            i++;
        }

        return (html[start..i].ToLowerInvariant(), i);
    }

    /// <summary>A start tag at <paramref name="lt"/>, its attributes (quoted values may contain '&gt;'), and raw-text content.</summary>
    private static int ReadStartTag(string html, int lt, List<Token> tokens)
    {
        var n = html.Length;
        var (name, i) = ReadName(html, lt + 1);
        var keep = KeptAttributes.Contains(name);
        Dictionary<string, string>? attributes = keep ? new(StringComparer.Ordinal) : null;
        var selfClosing = false;
        while (i < n)
        {
            var c = html[i];
            if (c == '>')
            {
                i++;
                break;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/')
            {
                selfClosing = i + 1 < n && html[i + 1] == '>';
                i++;
                continue;
            }

            selfClosing = false;
            var nameStart = i;
            while (i < n && !char.IsWhiteSpace(html[i]) && html[i] is not ('=' or '>' or '/'))
            {
                i++;
            }

            var attribute = html[nameStart..i];
            while (i < n && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            var value = "";
            if (i < n && html[i] == '=')
            {
                i++;
                while (i < n && char.IsWhiteSpace(html[i]))
                {
                    i++;
                }

                if (i < n && html[i] is '"' or '\'')
                {
                    var quote = html[i];
                    var close = html.IndexOf(quote, i + 1);
                    var end = close < 0 ? n : close;
                    value = html[(i + 1)..end];
                    i = close < 0 ? n : close + 1;
                }
                else
                {
                    var start = i;
                    while (i < n && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                    {
                        i++;
                    }

                    value = html[start..i];
                }
            }

            if (attributes is not null)
            {
                attributes.TryAdd(attribute.ToLowerInvariant(), WebUtility.HtmlDecode(value));
            }
        }

        tokens.Add(new Token(Kind.Start, name, "", selfClosing, attributes));
        if (RawText.Contains(name) && !selfClosing)
        {
            var close = name == "plaintext" ? -1 : html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
            var end = close < 0 ? n : close;
            AddText(tokens, html, i, end);
            tokens.Add(new Token(Kind.End, name, "", false, null));
            i = close < 0 ? n : SkipPast(html, close, '>');
        }

        return i;
    }

    /// <summary>For each start tag, the index of its end tag (innermost unmatched start of the same name), or -1.</summary>
    private static int[] MatchEnds(List<Token> tokens)
    {
        var match = new int[tokens.Count];
        Array.Fill(match, -1);
        var open = new Dictionary<string, Stack<int>>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == Kind.Start && !token.SelfClosing && !Void.Contains(token.Name))
            {
                if (!open.TryGetValue(token.Name, out var stack))
                {
                    open[token.Name] = stack = new Stack<int>();
                }

                stack.Push(i);
            }
            else if (token.Kind == Kind.End && open.TryGetValue(token.Name, out var stack) && stack.Count > 0)
            {
                match[stack.Pop()] = i;
            }
        }

        return match;
    }

    private static List<Uri> LinksOf(List<Token> tokens, Uri pageUrl)
    {
        var baseUrl = pageUrl;
        var found = tokens.FirstOrDefault(t => t.Kind == Kind.Start && t.Name == "base" && t.Attribute("href") is { Length: > 0 });
        if (found is not null && Uri.TryCreate(pageUrl, found.Attribute("href")!.Trim(), out var declared) && IsWeb(declared))
        {
            baseUrl = declared;
        }

        var links = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (token.Kind != Kind.Start || token.Name != "a" || token.Attribute("href") is not { } href)
            {
                continue;
            }

            href = href.Trim();
            if (href.Length == 0 || href[0] == '#' || !Uri.TryCreate(baseUrl, href, out var link) || !IsWeb(link))
            {
                continue;
            }

            var bare = link.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped);
            if (seen.Add(bare) && Uri.TryCreate(bare, UriKind.Absolute, out var clean))
            {
                links.Add(clean);
                if (links.Count >= MaxLinks)
                {
                    break;
                }
            }
        }

        return links;
    }

    private static bool IsWeb(Uri url) => url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

    private static string TitleOf(List<Token> tokens, int[] match)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == Kind.Start && tokens[i].Name == "title")
            {
                var raw = i + 1 < tokens.Count && tokens[i + 1].Kind == Kind.Text ? tokens[i + 1].Text : "";
                var title = Collapse(WebUtility.HtmlDecode(raw));
                if (title.Length > 0)
                {
                    return Short(title);
                }

                break;
            }
        }

        foreach (var token in tokens)
        {
            if (token.Kind == Kind.Start && token.Name == "meta"
                && (token.Attribute("property") ?? token.Attribute("name")) is { } property
                && property.Trim().Equals("og:title", StringComparison.OrdinalIgnoreCase)
                && Collapse(token.Attribute("content") ?? "") is { Length: > 0 } og)
            {
                return Short(og);
            }
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == Kind.Start && tokens[i].Name == "h1")
            {
                var end = match[i] < 0 ? Math.Min(tokens.Count, i + 50) : match[i];
                var heading = Collapse(Render(tokens, match, i + 1, end, skipRemoved: false));
                if (heading.Length > 0)
                {
                    return Short(heading);
                }
            }
        }

        return "";
    }

    private static string Short(string title) => title.Length > MaxTitle ? title[..MaxTitle] + "…" : title;

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>The longest <c>article</c> with enough text, else the longest such <c>main</c>, else the whole page.</summary>
    private static string MainText(List<Token> tokens, int[] match)
    {
        string? best = null;
        foreach (var name in (string[])["article", "main"])
        {
            foreach (var start in VisibleStarts(tokens, match, name))
            {
                var text = Render(tokens, match, start + 1, match[start], skipRemoved: true);
                if (text.Length >= MinMainText && (best is null || text.Length > best.Length))
                {
                    best = text;
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return Render(tokens, match, 0, tokens.Count, skipRemoved: true);
    }

    /// <summary>Matched start tags of <paramref name="name"/> that are not inside a dropped element.</summary>
    private static List<int> VisibleStarts(List<Token> tokens, int[] match, string name)
    {
        var starts = new List<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == Kind.Start && match[i] >= 0)
            {
                if (Removed.Contains(token.Name))
                {
                    i = match[i];
                    continue;
                }

                if (token.Name == name)
                {
                    starts.Add(i);
                }
            }
        }

        return starts;
    }

    private static string Render(List<Token> tokens, int[] match, int start, int end, bool skipRemoved)
    {
        var writer = new Writer();
        var pre = 0;
        for (var i = start; i < end && !writer.Full; i++)
        {
            var token = tokens[i];
            switch (token.Kind)
            {
                case Kind.Text:
                    writer.Text(WebUtility.HtmlDecode(token.Text), pre > 0);
                    break;
                case Kind.Start when Removed.Contains(token.Name):
                    if (skipRemoved && match[i] >= 0)
                    {
                        i = match[i];
                    }
                    else if (skipRemoved && RawText.Contains(token.Name))
                    {
                        i++; // Its raw content, whose end tag the scanner added.
                    }

                    break;
                case Kind.Start:
                    if (token.Name == "pre")
                    {
                        pre++;
                    }

                    Open(writer, token.Name);
                    break;
                case Kind.End:
                    if (token.Name == "pre" && pre > 0)
                    {
                        pre--;
                    }

                    Close(writer, token.Name);
                    break;
            }
        }

        return writer.ToString();
    }

    private static void Open(Writer writer, string name)
    {
        if (name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6')
        {
            writer.Break(2);
            writer.Prefix(new string('#', name[1] - '0') + " ");
        }
        else if (name == "li")
        {
            writer.Break(1);
            writer.Prefix("- ");
        }
        else if (name is "td" or "th")
        {
            writer.Space();
        }
        else
        {
            Close(writer, name);
        }
    }

    private static void Close(Writer writer, string name)
    {
        if (Paragraphs.Contains(name))
        {
            writer.Break(2);
        }
        else if (Lines.Contains(name))
        {
            writer.Break(1);
        }
        else if (name is "td" or "th")
        {
            writer.Space();
        }
    }

    /// <summary>Text with collapsed whitespace, at most one blank line between blocks, and line prefixes written only before text.</summary>
    private sealed class Writer
    {
        private readonly StringBuilder _text = new();
        private int _breaks;
        private string? _prefix;
        private bool _space;
        private bool _lineStart = true;

        public bool Full => _text.Length >= MaxText;

        public void Break(int lines)
        {
            if (_text.Length > 0)
            {
                _breaks = Math.Max(_breaks, lines);
            }

            _prefix = null;
            _space = false;
        }

        public void Prefix(string prefix) => _prefix = prefix;

        public void Space()
        {
            if (!_lineStart && _breaks == 0)
            {
                _space = true;
            }
        }

        public void Text(string text, bool pre)
        {
            foreach (var c in text)
            {
                if (pre)
                {
                    if (c == '\r')
                    {
                        continue;
                    }

                    if (c == '\n')
                    {
                        if (_text.Length > 0)
                        {
                            _breaks = Math.Min(_breaks + 1, 2);
                        }

                        continue;
                    }

                    Flush();
                    _text.Append(c == ' ' ? ' ' : c);
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    Space();
                    continue;
                }

                Flush();
                _text.Append(c);
            }
        }

        private void Flush()
        {
            if (_breaks > 0)
            {
                _text.Append('\n', _breaks);
                _breaks = 0;
                _lineStart = true;
            }

            if (_lineStart)
            {
                if (_prefix is not null)
                {
                    _text.Append(_prefix);
                    _prefix = null;
                }

                _lineStart = false;
                _space = false;
            }
            else if (_space)
            {
                _text.Append(' ');
                _space = false;
            }
        }

        public override string ToString() => _text.Length > MaxText ? _text.ToString(0, MaxText) : _text.ToString();
    }
}
