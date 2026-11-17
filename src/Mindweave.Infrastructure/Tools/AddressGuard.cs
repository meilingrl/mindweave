using System.Net;
using System.Net.Sockets;
using Mindweave.Application.Tools;

namespace Mindweave.Infrastructure.Tools;

/// <summary>
/// Which addresses a page may be read from (architecture §15.8, §15.13 "取网页的检查"): http(s) only, no credentials in the
/// address, and never this computer, a private or link-local network, the tailnet, multicast, or a reserved or documentation
/// range, whether given directly, embedded in an IPv6 address, or reached through a name.
/// </summary>
internal static class AddressGuard
{
    /// <summary>Names that mean this computer or a private network whatever they resolve to.</summary>
    private static readonly string[] BlockedSuffixes = [".localhost", ".local", ".internal", ".ts.net"];

    /// <summary>True when <paramref name="address"/> is on this computer, a private network, or a range no public site uses.</summary>
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            return IsBlockedV4(address.MapToIPv4().GetAddressBytes());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsBlockedV4(address.GetAddressBytes());
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return true;
        }

        var b = address.GetAddressBytes();

        // ::/96: the unspecified address, loopback and IPv4-compatible addresses (::a.b.c.d); ::, ::1 fall in 0.0.0.0/8.
        if (AllZero(b, 0, 12))
        {
            return IsBlockedV4(b.AsSpan(12));
        }

        // ::ffff:0:a.b.c.d (IPv4-translated, RFC 2765).
        if (AllZero(b, 0, 8) && b[8] == 0xff && b[9] == 0xff && b[10] == 0 && b[11] == 0)
        {
            return IsBlockedV4(b.AsSpan(12));
        }

        // 64:ff9b::/96, the well-known NAT64 prefix, carries an IPv4 address; 64:ff9b:1::/48 is for local use only.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
        {
            return !AllZero(b, 4, 8) || IsBlockedV4(b.AsSpan(12));
        }

        // 2002::/16 (6to4) carries an IPv4 address in its next 32 bits.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsBlockedV4(b.AsSpan(2, 4));
        }

        // 2001::/32 (Teredo) tunnels to an address that cannot be checked; 2001:db8::/32 is for documentation.
        if (b[0] == 0x20 && b[1] == 0x01 && ((b[2] == 0 && b[3] == 0) || (b[2] == 0x0d && b[3] == 0xb8)))
        {
            return true;
        }

        // 100::/64, the discard prefix.
        if (b[0] == 0x01 && b[1] == 0x00 && AllZero(b, 2, 6))
        {
            return true;
        }

        return b[0] == 0xff // ff00::/8 multicast
            || (b[0] & 0xfe) == 0xfc // fc00::/7 unique local
            || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) // fe80::/10 link-local
            || (b[0] == 0xfe && (b[1] & 0xc0) == 0xc0); // fec0::/10 site-local (deprecated)
    }

    /// <summary>
    /// Throws <see cref="PageFetchException"/> unless <paramref name="url"/> is an absolute http(s) address without user
    /// name or password whose host is not blocked by name or, for an IP literal, by address. Names are resolved by the caller.
    /// </summary>
    public static void CheckUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            var scheme = url.IsAbsoluteUri ? url.Scheme : "相对";
            throw new PageFetchException(FetchFailureKind.UnsupportedScheme, $"只能读取 http 或 https 网址，没有读取这个 {scheme} 网址。");
        }

        if (url.UserInfo.Length > 0)
        {
            throw new PageFetchException(FetchFailureKind.UnsupportedScheme, "网址里带有用户名或密码，没有读取。");
        }

        var host = HostOf(url);
        if (host.Length == 0)
        {
            throw new PageFetchException(FetchFailureKind.UnsupportedScheme, "网址里没有主机名，没有读取。");
        }

        if (LiteralAddress(host) is { } literal ? IsBlocked(literal) : IsBlockedName(host))
        {
            throw Blocked(host);
        }
    }

    /// <summary>The host as it is resolved: IDN in ASCII form, without brackets or a trailing dot.</summary>
    public static string HostOf(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.DnsSafeHost.TrimEnd('.');
    }

    /// <summary>The address when <paramref name="host"/> is an IP literal (an IPv6 zone is dropped), otherwise null.</summary>
    public static IPAddress? LiteralAddress(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var bare = host.Trim('[', ']');
        var zone = bare.IndexOf('%', StringComparison.Ordinal);
        if (zone >= 0)
        {
            bare = bare[..zone];
        }

        // Numeric forms the resolver would also read as an address (2130706433, 0x7f.1) count as literals too.
        return IPAddress.TryParse(bare, out var address) ? address : null;
    }

    /// <summary><c>localhost</c> and the suffixes that only ever name local or private machines.</summary>
    public static bool IsBlockedName(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.TrimEnd('.');
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var suffix in BlockedSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static PageFetchException Blocked(string host) =>
        new(FetchFailureKind.BlockedAddress, $"拒绝读取：{host} 指向本机或局域网地址。");

    private static bool IsBlockedV4(ReadOnlySpan<byte> a) => a[0] switch
    {
        0 or 10 or 127 => true, // this network, RFC 1918, loopback
        100 => (a[1] & 0xc0) == 64, // 100.64.0.0/10: carrier-grade NAT and Tailscale
        169 => a[1] == 254, // link-local
        172 => (a[1] & 0xf0) == 16, // 172.16.0.0/12
        192 => (a[1] == 168) || (a[1] == 0 && (a[2] == 0 || a[2] == 2)), // 192.168/16, 192.0.0/24, 192.0.2/24
        198 => (a[1] & 0xfe) == 18 || (a[1] == 51 && a[2] == 100), // 198.18.0.0/15 benchmarking, 198.51.100.0/24
        203 => a[1] == 0 && a[2] == 113, // 203.0.113.0/24
        >= 224 => true, // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved and the broadcast address
        _ => false,
    };

    private static bool AllZero(byte[] bytes, int start, int count)
    {
        for (var i = start; i < start + count; i++)
        {
            if (bytes[i] != 0)
            {
                return false;
            }
        }

        return true;
    }
}
