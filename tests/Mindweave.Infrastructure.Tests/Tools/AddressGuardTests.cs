using System.Net;
using Mindweave.Application.Tools;
using Mindweave.Infrastructure.Tools;

namespace Mindweave.Infrastructure.Tests.Tools;

/// <summary>Which addresses a page may come from (architecture §15.13 "取网页的检查").</summary>
public class AddressGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.0.9")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.100")]
    [InlineData("100.127.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("192.0.0.8")]
    [InlineData("192.0.2.10")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.5")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("fec0::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("::127.0.0.1")]
    [InlineData("::ffff:0:10.0.0.1")]
    [InlineData("64:ff9b::10.0.0.1")]
    [InlineData("64:ff9b::127.0.0.1")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("2002:0a00:0001::1")]
    [InlineData("2002:7f00:0001::")]
    [InlineData("2001:0000:4136:e378:8000:63bf:3fff:fdd2")]
    [InlineData("2001:db8::1")]
    [InlineData("100::1")]
    public void Blocks_local_private_and_reserved_addresses(string address) =>
        Assert.True(AddressGuard.IsBlocked(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("198.20.0.1")]
    [InlineData("223.255.255.1")]
    [InlineData("2606:4700::1111")]
    [InlineData("2a00:1450:4001:80b::200e")]
    [InlineData("::ffff:93.184.216.34")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:5db8:d822::1")]
    public void Allows_public_addresses(string address) =>
        Assert.False(AddressGuard.IsBlocked(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/file")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("gopher://example.com/")]
    [InlineData("http://user:pw@example.com/")]
    [InlineData("https://user@example.com/")]
    public void Refuses_other_schemes_and_credentials(string address)
    {
        var failure = Assert.Throws<PageFetchException>(() => AddressGuard.CheckUrl(new Uri(address)));
        Assert.Equal(FetchFailureKind.UnsupportedScheme, failure.Kind);
    }

    [Fact]
    public void Refuses_a_relative_address() =>
        Assert.Equal(FetchFailureKind.UnsupportedScheme, Assert.Throws<PageFetchException>(() => AddressGuard.CheckUrl(new Uri("/page", UriKind.Relative))).Kind);

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST:8080/")]
    [InlineData("http://localhost./")]
    [InlineData("http://foo.localhost/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://db.internal/")]
    [InlineData("http://x.ts.net/")]
    [InlineData("http://my-mac.tail1234.ts.net/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    [InlineData("http://100.100.1.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0x7f.1/")]
    [InlineData("https://[fe80::1%25en0]/")]
    public void Refuses_local_names_and_addresses(string address)
    {
        var failure = Assert.Throws<PageFetchException>(() => AddressGuard.CheckUrl(new Uri(address)));
        Assert.Equal(FetchFailureKind.BlockedAddress, failure.Kind);
        Assert.StartsWith("拒绝读取：", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/a?b=c#d")]
    [InlineData("http://93.184.216.34/")]
    [InlineData("https://[2606:4700::1111]/")]
    [InlineData("https://zh.wikipedia.org/wiki/蒸汽机")]
    [InlineData("https://localhost.example.com/")]
    public void Lets_public_addresses_through(string address) => AddressGuard.CheckUrl(new Uri(address));
}
