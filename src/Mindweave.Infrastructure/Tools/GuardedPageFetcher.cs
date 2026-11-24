using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Mindweave.Application.Resources;
using Mindweave.Application.Tools;
using Mindweave.Core.Resources;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Infrastructure.Tools;

/// <summary>The limits of one page read (architecture §15.13); tests shorten them.</summary>
public sealed record PageFetchLimits
{
    public static PageFetchLimits Default { get; } = new();

    public long MaxBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>The whole read, redirects included.</summary>
    public TimeSpan TotalTime { get; init; } = TimeSpan.FromSeconds(20);

    public int MaxRedirects { get; init; } = 5;

    public int MaxTextCharacters { get; init; } = HtmlText.MaxText;
}

/// <summary>
/// Reads one web page (architecture §15.8, §15.13 "取网页的检查"). Before every hop (redirects are followed by hand, at most
/// five) the address is checked: http(s) only, no user name or password, and the host, and every address it resolves to,
/// outside this computer and private networks. The production handler connects only to addresses it has resolved and checked
/// itself (<see cref="CheckedAddressesAsync"/>), so a name that resolves differently the second time cannot reach the
/// network. When the system proxy applies to an address (decision W8, revised 2026-11-17 after the owner's Clash Verge setup
/// refused every page), the request goes through the proxy instead: the URL, the name and redirects are still checked, a
/// resolved private address is still refused, and a proxy's fake-IP answer (198.18.0.0/15) or a name only the proxy can
/// resolve is let through. It sends no cookies and no credentials. The body is read as a
/// stream within a size and a time limit, and becomes text: HTML through <see cref="HtmlText"/>, PDF through the PDF
/// <see cref="ITextExtractor"/>, other text as it is.
/// </summary>
public sealed partial class GuardedPageFetcher : IWebPageFetcher
{
    internal const string UserAgent = "Mindweave/0.1 (+web page reader)";

    /// <summary>A check says a few lines at most; a page with more text is read whatever its title.</summary>
    private const int MaxRobotCheckText = 500;

    private static readonly HashSet<string> RobotCheckTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "百度安全验证", "安全验证", "人机验证", "滑动验证页面", "Just a moment...", "Attention Required! | Cloudflare",
        "Verify you are human", "Are you a robot?", "Robot Check",
    };

    internal const string AcceptTypes = "text/html,application/xhtml+xml,text/plain;q=0.9,application/pdf;q=0.8,*/*;q=0.1";

    /// <summary>The languages a page is asked for: the interface language first (docs/design/bilingual-ui.md §5), read per request.</summary>
    internal static string AcceptLanguages => T("en-US,en;q=0.9,zh-CN;q=0.8", "zh-CN,zh;q=0.9,en;q=0.8");

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpClient _http;
    private readonly HttpClient? _proxied;
    private readonly IWebProxy? _proxy;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly ITextExtractor? _pdf;
    private readonly TimeProvider _time;
    private readonly PageFetchLimits _limits;

    public GuardedPageFetcher(
        HttpClient http,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        IEnumerable<ITextExtractor> extractors,
        TimeProvider? time = null,
        PageFetchLimits? limits = null,
        IWebProxy? proxy = null,
        HttpClient? proxied = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(extractors);
        _http = http;
        _resolve = resolve;
        _pdf = extractors.FirstOrDefault(e => e.CanExtract(MediaTypes.Pdf));
        _time = time ?? TimeProvider.System;
        _limits = limits ?? PageFetchLimits.Default;
        _proxy = proxied is null ? null : proxy;
        _proxied = proxy is null ? null : proxied;
    }

    /// <summary>The production reader: its own guarded handler and the system resolver.</summary>
    public static GuardedPageFetcher Create(IEnumerable<ITextExtractor> extractors, TimeProvider? time = null)
    {
        var http = new HttpClient(CreateHandler(ResolveAsync)) { Timeout = Timeout.InfiniteTimeSpan };
        // The same proxy model requests and Tavily use: HTTP(S)_PROXY and NO_PROXY when set, else the system's settings.
        var proxy = HttpClient.DefaultProxy;
        var proxied = new HttpClient(CreateProxiedHandler(proxy)) { Timeout = Timeout.InfiniteTimeSpan };
        return new GuardedPageFetcher(http, ResolveAsync, extractors, time, proxy: proxy, proxied: proxied);
    }

    /// <summary>Through the proxy: no redirects (each hop is checked by the reader), no cookies, no credentials.</summary>
    public static SocketsHttpHandler CreateProxiedHandler(IWebProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            Proxy = proxy,
            PreAuthenticate = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
    }

    /// <summary>True when the system proxy applies to <paramref name="url"/> (set, and not bypassed by NO_PROXY or the system's list).</summary>
    internal bool GoesThroughProxy(Uri url)
    {
        if (_proxy is null || _proxied is null)
        {
            return false;
        }

        try
        {
            if (_proxy.IsBypassed(url))
            {
                return false;
            }

            var through = _proxy.GetProxy(url);
            return through is not null && through != url;
        }
#pragma warning disable CA1031 // A proxy setting that cannot be read means a direct, fully checked connection.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>
    /// No redirects (each hop is checked here), no cookies, no proxy (decision W8: through a proxy the real connection is the
    /// proxy's and cannot be checked), and a connection only to addresses resolved and checked by <see cref="CheckedAddressesAsync"/>.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            PreAuthenticate = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = await CheckedAddressesAsync(context.DnsEndPoint.Host, resolve, cancellationToken).ConfigureAwait(false);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    /// <summary>
    /// The addresses a connection to <paramref name="host"/> may use: an IP literal as it is, a name resolved once. Throws
    /// <see cref="PageFetchException"/> when the name or any resolved address is blocked, or nothing resolves.
    /// </summary>
    public static async Task<IPAddress[]> CheckedAddressesAsync(string host, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken cancellationToken, bool viaProxy = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(resolve);
        host = host.Trim('[', ']').TrimEnd('.');
        if (AddressGuard.LiteralAddress(host) is { } literal)
        {
            return AddressGuard.IsBlocked(literal) ? throw AddressGuard.Blocked(host) : [literal];
        }

        if (AddressGuard.IsBlockedName(host))
        {
            throw AddressGuard.Blocked(host);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await resolve(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            // Through a proxy the proxy resolves the name; a name this computer cannot resolve is not a reason to stop.
            return viaProxy ? [] : throw new PageFetchException(FetchFailureKind.Network, NotFound(host), ex);
        }

        if (addresses is not { Length: > 0 })
        {
            return viaProxy ? [] : throw new PageFetchException(FetchFailureKind.Network, NotFound(host));
        }

        // A proxy's fake-IP answer is not where the page is (AddressGuard.IsProxyFakeAddress): with a TUN-mode proxy a direct
        // connection to it goes into the proxy, without one it simply fails. Real private addresses are refused either way.
        return addresses.Any(AddressGuard.IsBlockedAnswer) ? throw AddressGuard.Blocked(host) : addresses;
    }

    private static Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    public async Task<FetchedPage> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var limit = new CancellationTokenSource(_limits.TotalTime, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            return await FetchCoreAsync(url, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PageFetchException(FetchFailureKind.Timeout, T(
                string.Create(CultureInfo.InvariantCulture, $"The page was not read within {_limits.TotalTime.TotalSeconds:0.#} seconds."),
                string.Create(CultureInfo.InvariantCulture, $"{_limits.TotalTime.TotalSeconds:0.#} 秒内没有读完网页。")));
        }
        catch (HttpRequestException ex)
        {
            // The guarded connection refuses by throwing inside the handler, which wraps it.
            for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
            {
                if (inner is PageFetchException refused)
                {
                    throw new PageFetchException(refused.Kind, refused.Message, ex);
                }
            }

            throw new PageFetchException(FetchFailureKind.Network, T($"Could not connect to {AddressGuard.HostOf(url)}: {ex.Message}", $"连不上 {AddressGuard.HostOf(url)}：{ex.Message}"), ex);
        }
        catch (IOException ex)
        {
            throw new PageFetchException(FetchFailureKind.Network, T($"The connection broke while reading {AddressGuard.HostOf(url)}: {ex.Message}", $"读取 {AddressGuard.HostOf(url)} 时连接中断：{ex.Message}"), ex);
        }
    }

    private async Task<FetchedPage> FetchCoreAsync(Uri url, CancellationToken cancellationToken)
    {
        var current = url;
        for (var redirects = 0; ; redirects++)
        {
            AddressGuard.CheckUrl(current);
            var viaProxy = GoesThroughProxy(current);
            await CheckedAddressesAsync(AddressGuard.HostOf(current), _resolve, cancellationToken, viaProxy).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", AcceptTypes);
            request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguages);
            using var response = await (viaProxy ? _proxied! : _http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                if (redirects >= _limits.MaxRedirects)
                {
                    throw new PageFetchException(FetchFailureKind.TooManyRedirects, T(
                        string.Create(CultureInfo.InvariantCulture, $"The URL redirected more than {_limits.MaxRedirects} times, so it was not read."),
                        string.Create(CultureInfo.InvariantCulture, $"网址重定向超过 {_limits.MaxRedirects} 次，没有读取。")));
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (status is < 200 or >= 300)
            {
                throw new PageFetchException(FetchFailureKind.HttpStatus, T(
                    string.Create(CultureInfo.InvariantCulture, $"The website returned {status}."),
                    string.Create(CultureInfo.InvariantCulture, $"网站返回了 {status}。")));
            }

            return await ReadPageAsync(current, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<FetchedPage> ReadPageAsync(Uri url, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var declared = response.Content.Headers.ContentType;
        var mediaType = declared?.MediaType?.Trim().ToLowerInvariant();
        if (mediaType is { Length: > 0 } && KindOf(mediaType) is null)
        {
            throw Unsupported(mediaType);
        }

        if (response.Content.Headers.ContentLength > _limits.MaxBytes)
        {
            throw TooLarge();
        }

        var bytes = await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var kind = mediaType is { Length: > 0 } ? KindOf(mediaType)!.Value : Sniff(bytes);
        mediaType = mediaType is { Length: > 0 } ? mediaType : kind switch
        {
            PageKind.Html => "text/html",
            PageKind.Pdf => MediaTypes.Pdf,
            _ => MediaTypes.PlainText,
        };

        string title;
        string text;
        IReadOnlyList<Uri> links = [];
        switch (kind)
        {
            case PageKind.Pdf:
                title = FileTitle(url);
                text = PdfText(bytes);
                break;
            case PageKind.Html:
                (title, text, links) = HtmlText.Extract(Decode(bytes, declared?.CharSet, html: true), url);
                if (IsRobotCheck(url, title, text))
                {
                    throw new PageFetchException(FetchFailureKind.RobotCheck, T(
                        "The website asks visitors to pass a security check (that they are not a robot), so the page was not read.",
                        "网站要求先通过安全验证（确认不是机器人），没有读取。"));
                }

                break;
            default:
                title = FileTitle(url);
                text = PlainTextExtractor.NormalizeLineEndings(Decode(bytes, declared?.CharSet, html: false)).Trim();
                break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new PageFetchException(FetchFailureKind.NoText, T("The page has no readable text (it may need scripts to show).", "网页里没有可读的文字（可能需要脚本才能显示）。"));
        }

        if (text.Length > _limits.MaxTextCharacters)
        {
            text = text[.._limits.MaxTextCharacters];
        }

        return new FetchedPage(url, title, text, mediaType, links, bytes.Length);
    }

    /// <summary>
    /// A check that the visitor is no robot, sent in place of the page with a success status: at an address known to hold
    /// only such checks, or under a title such checks use with little or no text. An article about them has more.
    /// </summary>
    private static bool IsRobotCheck(Uri url, string title, string text) =>
        string.Equals(AddressGuard.HostOf(url), "wappass.baidu.com", StringComparison.OrdinalIgnoreCase)
        || (text.Length <= MaxRobotCheckText && RobotCheckTitles.Contains(title.Trim()));

    private enum PageKind
    {
        Html,
        Text,
        Pdf,
    }

    private static PageKind? KindOf(string mediaType) => mediaType switch
    {
        "text/html" or "application/xhtml+xml" => PageKind.Html,
        "application/pdf" => PageKind.Pdf,
        _ when mediaType.StartsWith("text/", StringComparison.Ordinal) => PageKind.Text,
        _ => null,
    };

    /// <summary>A body without a type: a PDF by its header, HTML by its first tags, text when it reads as text.</summary>
    private static PageKind Sniff(byte[] bytes)
    {
        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        if (head.TrimStart(" \t\r\n"u8).StartsWith("%PDF"u8))
        {
            return PageKind.Pdf;
        }

        var start = Encoding.Latin1.GetString(head);
        if (start.Contains("<html", StringComparison.OrdinalIgnoreCase) || start.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase))
        {
            return PageKind.Html;
        }

        return new PlainTextExtractor().Extract(bytes, MediaTypes.PlainText).Text is not null ? PageKind.Text : throw Unsupported(null);
    }

    private string PdfText(byte[] bytes)
    {
        var extraction = _pdf?.Extract(bytes, MediaTypes.Pdf);
        return extraction switch
        {
            { Text: { } text } => text,
            { Failure: ExtractionFailure.Encrypted } => throw new PageFetchException(FetchFailureKind.NoText, T("This PDF needs a password, so it was not read.", "这个 PDF 需要密码，没有读取。")),
            { Failure: ExtractionFailure.NoText } => throw new PageFetchException(FetchFailureKind.NoText, T("This PDF has no readable text (it may be a scan).", "这个 PDF 里没有可读的文字（可能是扫描件）。")),
            null => throw new PageFetchException(FetchFailureKind.UnsupportedType, T("PDFs cannot be read.", "无法读取 PDF。")),
            _ => throw new PageFetchException(FetchFailureKind.NoText, T("This PDF cannot be read.", "这个 PDF 无法读取。")),
        };
    }

    /// <summary>The last part of the path, or the host.</summary>
    private static string FileTitle(Uri url)
    {
        var last = url.Segments.Length > 0 ? Uri.UnescapeDataString(url.Segments[^1]).Trim('/') : "";
        return last.Length > 0 ? last : AddressGuard.HostOf(url);
    }

    /// <summary>The body, counted as it streams; more than the limit stops reading.</summary>
    private async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > _limits.MaxBytes)
                {
                    throw TooLarge();
                }

                body.Write(buffer, 0, read);
            }

            return body.ToArray();
        }
    }

    private PageFetchException TooLarge() =>
        new(FetchFailureKind.TooLarge, T(
            string.Create(CultureInfo.InvariantCulture, $"The page is larger than {_limits.MaxBytes / (1024.0 * 1024):0.#} MB, so it was not read."),
            string.Create(CultureInfo.InvariantCulture, $"网页超过 {_limits.MaxBytes / (1024.0 * 1024):0.#} MB，没有读取。")));

    private static PageFetchException Unsupported(string? mediaType) => new(
        FetchFailureKind.UnsupportedType,
        mediaType is null
            ? T("This URL is not a web page, text or a PDF, so it was not read.", "这个网址不是网页、文字或 PDF，没有读取。")
            : T($"This URL is {mediaType}, not a web page, text or a PDF, so it was not read.", $"这个网址是 {mediaType}，不是网页、文字或 PDF，没有读取。"));

    private static string NotFound(string host) => T($"Could not find the website {host}.", $"找不到网站 {host}。");

    /// <summary>
    /// The text of the body: a byte order mark first, then the declared charset, then (HTML) a <c>meta</c> charset in the
    /// first 4 KB, then UTF-8 if it is valid, then GB18030 (older Chinese pages), then UTF-8 with replacement characters.
    /// </summary>
    internal static string Decode(byte[] bytes, string? charset, bool html)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var span = bytes.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(span[3..]);
        }

        if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(span[2..]);
        }

        if (span.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(span[2..]);
        }

        var encoding = EncodingOf(charset);
        if (encoding is null && html)
        {
            var head = Encoding.Latin1.GetString(span[..Math.Min(span.Length, 4096)]);
            var declared = MetaCharset().Match(head);
            encoding = declared.Success ? EncodingOf(declared.Groups["charset"].Value) : null;

            // A page cannot be in UTF-16 if its meta was readable as ASCII (WHATWG); it means UTF-8.
            if (encoding is UnicodeEncoding)
            {
                encoding = Encoding.UTF8;
            }
        }

        if (encoding is not null)
        {
            return encoding.GetString(bytes);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
        }

        try
        {
            return Gb18030(strict: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// The encoding a label names (WHATWG labels: gb2312 and gbk read as GB18030, a superset; latin1 and ascii as windows-1252),
    /// or null when the label is unknown.
    /// </summary>
    internal static Encoding? EncodingOf(string? label)
    {
        var name = label?.Trim().Trim('"', '\'').ToLowerInvariant();
        switch (name)
        {
            case null or "":
                return null;
            case "gb2312" or "gbk" or "x-gbk" or "gb18030" or "chinese" or "csgb2312" or "gb_2312-80":
                return Gb18030(strict: false);
            case "iso-8859-1" or "latin1" or "l1" or "us-ascii" or "ascii" or "windows-1252" or "cp1252":
                return CodePagesEncodingProvider.Instance.GetEncoding(1252);
        }

        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(name);
        }
    }

    private static Encoding Gb18030(bool strict) =>
        (strict
            ? CodePagesEncodingProvider.Instance.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            : CodePagesEncodingProvider.Instance.GetEncoding(54936))
        ?? throw new InvalidOperationException("GB18030 is not available.");

    /// <summary><c>&lt;meta charset=…&gt;</c> or <c>&lt;meta http-equiv=… content="…; charset=…"&gt;</c>.</summary>
    [GeneratedRegex(@"<meta\b[^>]{0,512}?charset\s*=\s*[""']?\s*(?<charset>[A-Za-z0-9_\-:.]{1,40})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex MetaCharset();
}
