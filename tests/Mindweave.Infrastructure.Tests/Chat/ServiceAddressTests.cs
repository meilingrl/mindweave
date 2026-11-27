using Mindweave.Application.Models;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// A saved key goes only to the service it was saved for (tests-10 in docs/design/diagnostics-and-upkeep.md): same
/// scheme, host and port; another path on the same host is the same service.
/// </summary>
public class ServiceAddressTests
{
    [Theory]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com")]
    [InlineData("https://relay.example.com/v1", "https://relay.example.com/v2")]
    [InlineData("https://relay.example.com/v1", "https://relay.example.com")]
    [InlineData("https://api.deepseek.com/", " https://api.deepseek.com ")]
    [InlineData("https://API.DeepSeek.com", "https://api.deepseek.com")]
    [InlineData("HTTPS://api.deepseek.com", "https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com:443", "https://api.deepseek.com")]
    [InlineData("http://localhost:8080/v1", "http://localhost:8080/v1?x=1")]
    public void The_same_scheme_host_and_port_is_the_same_service(string saved, string current)
    {
        Assert.True(ServiceAddress.SameService(saved, current));
        Assert.True(ServiceAddress.SameService(current, saved));
    }

    [Theory]
    [InlineData("https://api.deepseek.com", "http://api.deepseek.com")]
    [InlineData("https://api.deepseek.com", "https://api.moonshot.cn")]
    [InlineData("https://api.deepseek.com", "https://deepseek.com")]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com.example.net")]
    [InlineData("http://localhost:8080", "http://localhost:8081")]
    [InlineData("http://localhost:8080", "http://127.0.0.1:8080")]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com:8443")]
    public void Another_scheme_host_or_port_is_another_service(string saved, string current)
    {
        Assert.False(ServiceAddress.SameService(saved, current));
        Assert.False(ServiceAddress.SameService(current, saved));
    }

    /// <summary>An address that cannot be read is never the same service, not even as itself: the key stays home.</summary>
    [Theory]
    [InlineData(null, "https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com", null)]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("api.deepseek.com", "api.deepseek.com")]
    public void An_unreadable_address_is_no_service(string? saved, string? current) =>
        Assert.False(ServiceAddress.SameService(saved, current));
}
