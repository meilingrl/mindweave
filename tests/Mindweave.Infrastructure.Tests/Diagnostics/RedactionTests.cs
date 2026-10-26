using Mindweave.Application.Diagnostics;

namespace Mindweave.Infrastructure.Tests.Diagnostics;

public class RedactionTests
{
    [Theory]
    [InlineData("Authorization: Bearer abcdefgh12345678", "Authorization: Bearer [redacted]")]
    [InlineData("Incorrect API key provided: sk-proj-AbCdEf123456", "Incorrect API key provided: [redacted]")]
    [InlineData("key AIzaSyA1234567890abcdefghij rejected", "key [redacted] rejected")]
    [InlineData("{\"api_key\": \"q1w2e3r4t5y6\"}", "{\"api_key\": \"[redacted]\"")]
    [InlineData("password=hunter2hunter2", "password=[redacted]")]
    public void Credential_shaped_tokens_are_masked(string input, string expected) =>
        Assert.StartsWith(expected, Redaction.Scrub(input), StringComparison.Ordinal);

    [Fact]
    public void The_known_secret_is_masked_even_without_a_recognisable_shape()
    {
        Assert.Equal("echo: [redacted]!", Redaction.Scrub("echo: my-own-gateway-key!", "my-own-gateway-key"));
    }

    [Theory]
    [InlineData("HTTP 429: slow down, retry in 20s")]
    [InlineData("This model's maximum context length is 8192 tokens")]
    [InlineData("蒸汽机 token 计数")]
    public void Ordinary_error_text_is_left_alone(string input) =>
        Assert.Equal(input, Redaction.Scrub(input));

    [Fact]
    public void Short_known_secrets_are_not_treated_as_secrets()
    {
        Assert.Equal("a test of the api", Redaction.Scrub("a test of the api", "test"));
    }
}
