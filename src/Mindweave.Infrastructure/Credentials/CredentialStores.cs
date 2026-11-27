using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mindweave.Application.Credentials;
using Mindweave.Application.Diagnostics;

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

    /// <summary>
    /// Wraps the key store registered in <paramref name="services"/> so every key read from it or written to it is given to
    /// <see cref="KnownSecrets"/>, which the file log scrubs (D2, architecture §17.2). Called once all stores are registered.
    /// </summary>
    public static void RecordKeys(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registered = services.LastOrDefault(d => d.ServiceType == typeof(ICredentialStore));
        if (registered is null)
        {
            return;
        }

        services.RemoveAll<ICredentialStore>();
        services.AddSingleton<ICredentialStore>(sp => new RecordingCredentialStore(Inner(sp, registered), sp.GetRequiredService<KnownSecrets>()));

        static ICredentialStore Inner(IServiceProvider sp, ServiceDescriptor registered) =>
            registered.ImplementationInstance as ICredentialStore
            ?? registered.ImplementationFactory?.Invoke(sp) as ICredentialStore
            ?? (ICredentialStore)ActivatorUtilities.CreateInstance(sp, registered.ImplementationType!);
    }

    /// <summary>Remembers each key it passes; reads, writes and deletes are the inner store's own.</summary>
    private sealed class RecordingCredentialStore(ICredentialStore inner, KnownSecrets secrets) : ICredentialStore
    {
        public async Task<string?> ReadAsync(string reference, CancellationToken cancellationToken)
        {
            var secret = await inner.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
            secrets.Add(secret);
            return secret;
        }

        public Task WriteAsync(string reference, string secret, CancellationToken cancellationToken)
        {
            secrets.Add(secret);
            return inner.WriteAsync(reference, secret, cancellationToken);
        }

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) => inner.DeleteAsync(reference, cancellationToken);
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
