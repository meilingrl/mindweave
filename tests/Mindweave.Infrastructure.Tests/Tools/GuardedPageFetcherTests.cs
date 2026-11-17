using System.Net;
using System.Text;
using Mindweave.Application.Resources;
using Mindweave.Application.Tools;
using Mindweave.Core.Resources;
using Mindweave.Infrastructure.Resources;
using Mindweave.Infrastructure.Tools;
using Mindweave.Testing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Mindweave.Infrastructure.Tests.Tools;

/// <summary>
/// Reading a page (architecture §15.8, §15.13): every hop checked, limits kept, bodies turned into text. A fake handler stands
/// in for the network and an injected resolver for DNS.
/// </summary>
public class GuardedPageFetcherTests
{
    private static readonly IPAddress Public = IPAddress.Parse("93.184.216.34");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<IPAddress[]> PublicResolver(string host, CancellationToken ct) => Task.FromResult(new[] { Public });

    private static GuardedPageFetcher Fetcher(FakeHandler handler, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, PageFetchLimits? limits = null) =>
        new(new HttpClient(handler), resolve ?? PublicResolver, [new PdfTextExtractor(), new DocxTextExtractor()], limits: limits);

    private static FakeHandler NeverCalled() => new(_ => throw new InvalidOperationException("No request may be sent."));

    private static async Task<PageFetchException> Fails(GuardedPageFetcher fetcher, string url) =>
        await Assert.ThrowsAsync<PageFetchException>(() => fetcher.FetchAsync(new Uri(url), Ct));

    [Fact]
    public async Task Reads_an_html_page_with_only_the_reader_headers()
    {
        var handler = new FakeHandler(_ => Responses.Text("<html><head><title>蒸汽机</title></head><body><p>第一段。</p><a href='/next'>下一页</a></body></html>"));

        var page = await Fetcher(handler).FetchAsync(new Uri("https://example.com/steam"), Ct);

        Assert.Equal(new Uri("https://example.com/steam"), page.Url);
        Assert.Equal("蒸汽机", page.Title);
        Assert.Contains("第一段。", page.Text, StringComparison.Ordinal);
        Assert.Equal("text/html", page.MediaType);
        Assert.Equal([new Uri("https://example.com/next")], page.Links);
        Assert.True(page.Bytes > 0);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(GuardedPageFetcher.UserAgent, request.Headers["User-Agent"]);
        Assert.Equal(["Accept", "Accept-Language", "User-Agent"], request.Headers.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_name_that_resolves_to_a_private_address_is_refused_before_any_request()
    {
        var handler = NeverCalled();
        var resolved = new List<string>();
        var fetcher = Fetcher(handler, (host, _) =>
        {
            resolved.Add(host);
            return Task.FromResult(new[] { Public, IPAddress.Parse("10.0.0.5") });
        });

        var failure = await Fails(fetcher, "https://intranet.example.com/");

        Assert.Equal(FetchFailureKind.BlockedAddress, failure.Kind);
        Assert.Equal("拒绝读取：intranet.example.com 指向本机或局域网地址。", failure.Message);
        Assert.Equal(["intranet.example.com"], resolved);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    [InlineData("http://100.100.1.1/")]
    [InlineData("http://localhost:5000/")]
    [InlineData("http://nas.local/")]
    public async Task Local_addresses_are_refused_without_resolving_or_sending(string url)
    {
        var handler = NeverCalled();
        var fetcher = Fetcher(handler, (_, _) => throw new InvalidOperationException("No lookup for a literal or a local name."));

        Assert.Equal(FetchFailureKind.BlockedAddress, (await Fails(fetcher, url)).Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/")]
    [InlineData("http://user:pw@example.com/")]
    public async Task Other_schemes_and_credentials_are_refused(string url) =>
        Assert.Equal(FetchFailureKind.UnsupportedScheme, (await Fails(Fetcher(NeverCalled()), url)).Kind);

    [Fact]
    public async Task A_redirect_to_a_private_address_is_refused_and_never_sent()
    {
        var handler = new FakeHandler(_ => Responses.Redirect("http://192.168.1.1/admin"));

        var failure = await Fails(Fetcher(handler), "https://example.com/");

        Assert.Equal(FetchFailureKind.BlockedAddress, failure.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_redirect_to_a_name_that_resolves_privately_is_refused()
    {
        var handler = new FakeHandler(_ => Responses.Redirect("https://evil.example.net/"));
        var fetcher = Fetcher(handler, (host, _) => Task.FromResult(new[] { host == "evil.example.net" ? IPAddress.Parse("169.254.169.254") : Public }));

        Assert.Equal(FetchFailureKind.BlockedAddress, (await Fails(fetcher, "https://example.com/")).Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_redirect_to_a_file_address_is_refused()
    {
        var handler = new FakeHandler(_ => Responses.Redirect("file:///etc/passwd", HttpStatusCode.MovedPermanently));

        Assert.Equal(FetchFailureKind.UnsupportedScheme, (await Fails(Fetcher(handler), "https://example.com/")).Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Relative_redirects_are_followed_and_the_final_address_is_reported()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/a" => Responses.Redirect("b?x=1", HttpStatusCode.MovedPermanently),
            "/b" => Responses.Redirect("/c", HttpStatusCode.TemporaryRedirect),
            _ => Responses.Text("<p>arrived</p>"),
        });

        var page = await Fetcher(handler).FetchAsync(new Uri("https://example.com/a"), Ct);

        Assert.Equal(new Uri("https://example.com/c"), page.Url);
        Assert.Equal(["https://example.com/a", "https://example.com/b?x=1", "https://example.com/c"], handler.Requests.Select(r => r.Uri.ToString()));
        Assert.Equal("arrived", page.Text);
    }

    [Fact]
    public async Task Five_redirects_are_followed_and_a_sixth_is_refused()
    {
        static FakeHandler Chain(int redirects) => new(request =>
        {
            var n = int.Parse(request.RequestUri!.AbsolutePath.Trim('/'), System.Globalization.CultureInfo.InvariantCulture);
            return n < redirects ? Responses.Redirect($"/{n + 1}") : Responses.Text("<p>end</p>");
        });

        Assert.Equal("end", (await Fetcher(Chain(5)).FetchAsync(new Uri("https://example.com/0"), Ct)).Text);

        var six = Chain(6);
        Assert.Equal(FetchFailureKind.TooManyRedirects, (await Fails(Fetcher(six), "https://example.com/0")).Kind);
        Assert.Equal(6, six.Requests.Count);
    }

    [Fact]
    public async Task No_cookie_or_authorization_is_sent_even_after_a_redirect_sets_a_cookie()
    {
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/login")
            {
                var redirect = Responses.Redirect("/home");
                redirect.Headers.Add("Set-Cookie", "session=abc; Path=/");
                return redirect;
            }

            return Responses.Text("<p>home</p>");
        });

        await Fetcher(handler).FetchAsync(new Uri("https://example.com/login"), Ct);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.False(r.Headers.ContainsKey("Cookie"));
            Assert.False(r.Headers.ContainsKey("Authorization"));
        });
    }

    [Fact]
    public void The_production_handler_sends_no_cookies_follows_no_redirects_and_skips_the_proxy()
    {
        using var handler = GuardedPageFetcher.CreateHandler(PublicResolver);

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Credentials);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task The_connection_check_refuses_local_names_and_private_answers_without_a_socket()
    {
        Task<IPAddress[]> Unused(string host, CancellationToken ct) => throw new InvalidOperationException("No lookup expected.");

        Assert.Equal(FetchFailureKind.BlockedAddress, (await Assert.ThrowsAsync<PageFetchException>(() => GuardedPageFetcher.CheckedAddressesAsync("localhost", Unused, Ct))).Kind);
        Assert.Equal(FetchFailureKind.BlockedAddress, (await Assert.ThrowsAsync<PageFetchException>(() => GuardedPageFetcher.CheckedAddressesAsync("::1", Unused, Ct))).Kind);
        Assert.Equal(FetchFailureKind.BlockedAddress, (await Assert.ThrowsAsync<PageFetchException>(() =>
            GuardedPageFetcher.CheckedAddressesAsync("rebind.example.com", (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }), Ct))).Kind);
        Assert.Equal(FetchFailureKind.Network, (await Assert.ThrowsAsync<PageFetchException>(() =>
            GuardedPageFetcher.CheckedAddressesAsync("nowhere.example.com", (_, _) => Task.FromResult(Array.Empty<IPAddress>()), Ct))).Kind);

        Assert.Equal([Public], await GuardedPageFetcher.CheckedAddressesAsync("example.com", PublicResolver, Ct));
        Assert.Equal([Public], await GuardedPageFetcher.CheckedAddressesAsync("93.184.216.34", Unused, Ct));
    }

    [Fact]
    public async Task A_name_that_resolves_differently_at_connection_time_is_refused_by_the_handler()
    {
        // The first lookup (the per-hop check) answers a public address; the connection's own lookup answers a private one,
        // as a rebinding DNS server would. The guarded handler refuses before opening a socket.
        var lookups = 0;
        Task<IPAddress[]> Rebinding(string host, CancellationToken ct) =>
            Task.FromResult(new[] { Interlocked.Increment(ref lookups) == 1 ? Public : IPAddress.Parse("127.0.0.1") });
        using var http = new HttpClient(GuardedPageFetcher.CreateHandler(Rebinding));
        var fetcher = new GuardedPageFetcher(http, Rebinding, []);

        var failure = await Fails(fetcher, "http://rebind.example.com/");

        Assert.Equal(FetchFailureKind.BlockedAddress, failure.Kind);
        Assert.Equal(2, lookups);
    }

    [Fact]
    public async Task A_declared_length_over_the_limit_is_refused_without_reading()
    {
        var stream = new SlowStream(6 * 1024 * 1024, 65536, TimeSpan.Zero);
        var handler = new FakeHandler(_ =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new("text/html");
            content.Headers.ContentLength = 6 * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var failure = await Fails(Fetcher(handler), "https://example.com/big");

        Assert.Equal(FetchFailureKind.TooLarge, failure.Kind);
        Assert.Equal("网页超过 5 MB，没有读取。", failure.Message);
    }

    [Fact]
    public async Task A_body_without_length_that_grows_past_the_limit_is_stopped()
    {
        var handler = new FakeHandler(_ =>
        {
            var content = new StreamContent(new SlowStream(5 * 1024 * 1024 + 1, 65536, TimeSpan.Zero));
            content.Headers.ContentType = new("text/plain");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        Assert.Equal(FetchFailureKind.TooLarge, (await Fails(Fetcher(handler), "https://example.com/stream")).Kind);
    }

    [Fact]
    public async Task A_slow_body_runs_into_the_time_limit()
    {
        var handler = new FakeHandler(_ =>
        {
            var content = new StreamContent(new SlowStream(1024 * 1024, 16, TimeSpan.FromSeconds(5)));
            content.Headers.ContentType = new("text/html");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var limits = PageFetchLimits.Default with { TotalTime = TimeSpan.FromMilliseconds(200) };

        var failure = await Fails(Fetcher(handler, limits: limits), "https://example.com/slow");

        Assert.Equal(FetchFailureKind.Timeout, failure.Kind);
        Assert.Contains("秒内没有读完网页", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_time_limit_is_twenty_seconds() =>
        Assert.Equal("20 秒内没有读完网页。", (await Fails(SlowWithStandingClock(), "https://example.com/")).Message);

    /// <summary>A handler that waits forever; a standing clock jumps 20 s once the request is waiting.</summary>
    private static GuardedPageFetcher SlowWithStandingClock()
    {
        var clock = new ManualTime();
        var handler = new FakeHandler(async (_, ct) =>
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        return new GuardedPageFetcher(new HttpClient(handler), PublicResolver, [], clock);
    }

    [Fact]
    public async Task Cancelling_is_not_a_failure()
    {
        using var cancel = new CancellationTokenSource();
        var handler = new FakeHandler(async (_, ct) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetcher(handler).FetchAsync(new Uri("https://example.com/"), cancel.Token));
    }

    [Theory]
    [InlineData("image/png", "这个网址是 image/png，不是网页、文字或 PDF，没有读取。")]
    [InlineData("application/zip", "这个网址是 application/zip，不是网页、文字或 PDF，没有读取。")]
    [InlineData("application/octet-stream", "这个网址是 application/octet-stream，不是网页、文字或 PDF，没有读取。")]
    public async Task Other_types_are_refused(string type, string message)
    {
        var failure = await Fails(Fetcher(new FakeHandler(_ => Responses.Bytes([1, 2, 3], type))), "https://example.com/file");

        Assert.Equal(FetchFailureKind.UnsupportedType, failure.Kind);
        Assert.Equal(message, failure.Message);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    public async Task An_error_status_is_reported(int status)
    {
        var failure = await Fails(Fetcher(new FakeHandler(_ => Responses.Text("<p>nope</p>", status: (HttpStatusCode)status))), "https://example.com/");

        Assert.Equal(FetchFailureKind.HttpStatus, failure.Kind);
        Assert.Equal($"网站返回了 {status}。", failure.Message);
    }

    [Fact]
    public async Task A_network_failure_is_reported()
    {
        var failure = await Fails(Fetcher(new FakeHandler(_ => throw new HttpRequestException("Connection refused"))), "https://example.com/");

        Assert.Equal(FetchFailureKind.Network, failure.Kind);
    }

    [Fact]
    public async Task Plain_text_is_read_as_it_is()
    {
        var page = await Fetcher(new FakeHandler(_ => Responses.Text("line one\r\nline two\n", "text/plain"))).FetchAsync(new Uri("https://example.com/notes/readme.txt"), Ct);

        Assert.Equal("line one\nline two", page.Text);
        Assert.Equal("readme.txt", page.Title);
        Assert.Equal("text/plain", page.MediaType);
        Assert.Empty(page.Links);
    }

    [Fact]
    public async Task Gb18030_html_is_decoded_by_its_meta_charset()
    {
        var gb = CodePagesEncodingProvider.Instance.GetEncoding(54936)!;
        var html = "<html><head><meta charset=\"gbk\"><title>中文网页</title></head><body><p>瓦特改良了蒸汽机。</p></body></html>";

        var page = await Fetcher(new FakeHandler(_ => Responses.Bytes(gb.GetBytes(html), "text/html"))).FetchAsync(new Uri("https://example.cn/"), Ct);

        Assert.Equal("中文网页", page.Title);
        Assert.Equal("瓦特改良了蒸汽机。", page.Text);
    }

    [Fact]
    public async Task The_declared_charset_wins_and_http_equiv_is_understood()
    {
        var big5 = CodePagesEncodingProvider.Instance.GetEncoding("big5")!;
        var declared = await Fetcher(new FakeHandler(_ => Responses.Bytes(big5.GetBytes("<p>蒸汽機</p>"), "text/html; charset=Big5"))).FetchAsync(new Uri("https://example.tw/"), Ct);
        Assert.Equal("蒸汽機", declared.Text);

        var gb = CodePagesEncodingProvider.Instance.GetEncoding(54936)!;
        var equiv = "<head><META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=gb2312\"></head><p>火车</p>";
        var page = await Fetcher(new FakeHandler(_ => Responses.Bytes(gb.GetBytes(equiv), "text/html"))).FetchAsync(new Uri("https://example.cn/"), Ct);
        Assert.Equal("火车", page.Text);
    }

    [Fact]
    public async Task Undeclared_gb18030_text_is_still_read()
    {
        var gb = CodePagesEncodingProvider.Instance.GetEncoding(54936)!;

        var page = await Fetcher(new FakeHandler(_ => Responses.Bytes(gb.GetBytes("没有声明编码的文字"), "text/plain"))).FetchAsync(new Uri("https://example.cn/a.txt"), Ct);

        Assert.Equal("没有声明编码的文字", page.Text);
    }

    [Fact]
    public async Task A_body_without_a_type_is_sniffed()
    {
        var html = await Fetcher(new FakeHandler(_ => Responses.Bytes(Encoding.UTF8.GetBytes("<!DOCTYPE html><html><p>sniffed</p></html>"), null))).FetchAsync(new Uri("https://example.com/"), Ct);
        Assert.Equal(("text/html", "sniffed"), (html.MediaType, html.Text));

        var text = await Fetcher(new FakeHandler(_ => Responses.Bytes(Encoding.UTF8.GetBytes("just words"), null))).FetchAsync(new Uri("https://example.com/"), Ct);
        Assert.Equal(("text/plain", "just words"), (text.MediaType, text.Text));

        var pdf = await Fetcher(new FakeHandler(_ => Responses.Bytes(BuildPdf("Sniffed PDF text"), null))).FetchAsync(new Uri("https://example.com/doc"), Ct);
        Assert.Equal(MediaTypes.Pdf, pdf.MediaType);

        var binary = await Fails(Fetcher(new FakeHandler(_ => Responses.Bytes([0x89, 0x50, 0x4E, 0x47, 0, 0, 1, 2], null))), "https://example.com/x");
        Assert.Equal(FetchFailureKind.UnsupportedType, binary.Kind);
    }

    [Fact]
    public async Task A_pdf_is_read_by_the_pdf_extractor()
    {
        var page = await Fetcher(new FakeHandler(_ => Responses.Bytes(BuildPdf("The steam engine paper"), "application/pdf"))).FetchAsync(new Uri("https://example.com/papers/steam.pdf"), Ct);

        Assert.Contains("The steam engine paper", page.Text, StringComparison.Ordinal);
        Assert.Equal("steam.pdf", page.Title);
        Assert.Equal(MediaTypes.Pdf, page.MediaType);
    }

    [Fact]
    public async Task A_pdf_without_text_has_no_text()
    {
        var failure = await Fails(Fetcher(new FakeHandler(_ => Responses.Bytes(BuildPdf(), "application/pdf"))), "https://example.com/scan.pdf");

        Assert.Equal(FetchFailureKind.NoText, failure.Kind);
    }

    [Fact]
    public async Task A_page_drawn_only_by_scripts_has_no_text()
    {
        var html = "<html><head><title>App</title><script>render()</script></head><body><div id=root></div><noscript>Enable JS</noscript></body></html>";

        var failure = await Fails(Fetcher(new FakeHandler(_ => Responses.Text(html))), "https://app.example.com/");

        Assert.Equal(FetchFailureKind.NoText, failure.Kind);
        Assert.Equal("网页里没有可读的文字（可能需要脚本才能显示）。", failure.Message);
    }

    [Fact]
    public async Task Text_is_capped()
    {
        var limits = PageFetchLimits.Default with { MaxTextCharacters = 10 };

        var page = await Fetcher(new FakeHandler(_ => Responses.Text(new string('x', 100), "text/plain")), limits: limits).FetchAsync(new Uri("https://example.com/"), Ct);

        Assert.Equal(10, page.Text.Length);
    }

    private static byte[] BuildPdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        for (var i = 0; i < lines.Length; i++)
        {
            page.AddText(lines[i], 12, new PdfPoint(50, 750 - (20 * i)), font);
        }

        return builder.Build();
    }
}
