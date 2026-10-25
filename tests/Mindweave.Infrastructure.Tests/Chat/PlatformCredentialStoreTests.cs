using Mindweave.Infrastructure.Credentials;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Exercises the real Keychain / Credential Manager. Opt-in (MINDWEAVE_CREDENTIAL_TESTS=1) because it writes to
/// the signed-in user's secret store; it uses a unique throwaway namespace and deletes what it creates.
/// </summary>
public class PlatformCredentialStoreTests
{
    [Fact]
    public async Task Write_read_update_and_delete_round_trip()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MINDWEAVE_CREDENTIAL_TESTS") == "1", "Set MINDWEAVE_CREDENTIAL_TESTS=1 to touch the real credential store.");
        Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "No platform credential store.");
        var ct = TestContext.Current.CancellationToken;
        var store = CredentialStores.ForPlatform($"test-{Guid.NewGuid():N}");
        var reference = $"{Guid.NewGuid():D}/{Guid.NewGuid():D}";
        try
        {
            Assert.Null(await store.ReadAsync(reference, ct));
            await store.WriteAsync(reference, "sk-测试-1", ct);
            Assert.Equal("sk-测试-1", await store.ReadAsync(reference, ct));
            await store.WriteAsync(reference, "sk-2", ct);
            Assert.Equal("sk-2", await store.ReadAsync(reference, ct));
        }
        finally
        {
            await store.DeleteAsync(reference, ct);
        }

        Assert.Null(await store.ReadAsync(reference, ct));
        await store.DeleteAsync(reference, ct);
    }
}
