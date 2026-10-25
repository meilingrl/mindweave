using Mindweave.Application.Credentials;

namespace Mindweave.Infrastructure.Credentials;

public static class CredentialStores
{
    /// <summary>The platform store, namespaced by channel so separate tracks and builds never share secrets.</summary>
    public static ICredentialStore ForPlatform(string channel)
    {
        var name = $"Mindweave.{channel}";
        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainCredentialStore(name);
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore(name);
        }

        return new UnsupportedCredentialStore();
    }

    /// <summary>Linux is a later target: say so clearly instead of storing secrets somewhere weaker.</summary>
    private sealed class UnsupportedCredentialStore : ICredentialStore
    {
        public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken) => throw Unsupported();

        public Task WriteAsync(string reference, string secret, CancellationToken cancellationToken) => throw Unsupported();

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) => throw Unsupported();

        private static CredentialUnavailableException Unsupported() =>
            new("No supported credential store on this platform yet.");
    }
}
