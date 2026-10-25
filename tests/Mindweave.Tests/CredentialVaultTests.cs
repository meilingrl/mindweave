using System.Security.Cryptography;
using Mindweave.Infrastructure.Credentials;
using Xunit;

namespace Mindweave.Tests;

public sealed class CredentialVaultTests
{
    [Theory]
    [InlineData("")]
    [InlineData("other-app")]
    [InlineData("Mindweave.codex-steady/../other")]
    public void RejectsInvalidNamespace(string value) => Assert.Throws<ArgumentException>(() => new SystemCredentialVault(value));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task RejectsEmptySecretWithoutNativeAccess(string secret) =>
        await Assert.ThrowsAsync<ArgumentException>(() => new SystemCredentialVault("Mindweave.codex-steady.tests.validation").WriteNewAsync(secret));

    [Theory]
    [InlineData("other:v1:0123456789abcdef0123456789abcdef")]
    [InlineData("Mindweave.codex-steady.tests.validation:v2:0123456789abcdef0123456789abcdef")]
    [InlineData("Mindweave.codex-steady.tests.validation:v1:not-random-id")]
    [InlineData("Mindweave.codex-steady.tests.other:v1:0123456789abcdef0123456789abcdef")]
    public async Task RejectsOutsideOrMalformedReferencesBeforeNativeAccess(string reference)
    {
        var vault = new SystemCredentialVault("Mindweave.codex-steady.tests.validation");
        await Assert.ThrowsAsync<ArgumentException>(() => vault.ReadAsync(reference));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.DeleteAsync(reference));
    }

    [UnsupportedCredentialFact]
    public async Task UnknownPlatformFailsExplicitly()
    {
        var vault = new SystemCredentialVault("Mindweave.codex-steady.tests.validation");
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => vault.WriteNewAsync("synthetic"));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => vault.ReadAsync("Mindweave.codex-steady.tests.validation:v1:0123456789abcdef0123456789abcdef"));
    }

    [NativeCredentialFact]
    public async Task NativeSyntheticRoundtripOnlyOwnFreshItem()
    {
        var ns = "Mindweave.codex-steady.tests." + Guid.NewGuid().ToString("N");
        var vault = new SystemCredentialVault(ns);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string? reference = null;
        try
        {
            reference = await vault.WriteNewAsync(secret);
            Assert.StartsWith(ns + ":v1:", reference);
            var actual = await vault.ReadAsync(reference);
            // Compare without putting secret-bearing values into assertion diagnostics.
            Assert.True(CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(actual)), "Synthetic native value did not roundtrip.");
        }
        finally
        {
            if (reference is not null) await vault.DeleteAsync(reference);
        }
        var exception = await Assert.ThrowsAsync<CredentialVaultException>(() => vault.ReadAsync(reference!));
        Assert.True(exception.NotFound);
    }
}

public sealed class NativeCredentialFactAttribute : FactAttribute
{
    public NativeCredentialFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MINDWEAVE_TEST_NATIVE_CREDENTIALS") != "1")
            Skip = "Native credential API not invoked; explicit synthetic-only opt-in required.";
        else if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) Skip = "Native credential platform unavailable.";
    }
}

public sealed class UnsupportedCredentialFactAttribute : FactAttribute
{
    public UnsupportedCredentialFactAttribute()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) Skip = "Unknown-platform behavior can only execute on unsupported platform.";
    }
}
