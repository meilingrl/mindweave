using System.Collections.Concurrent;
using Mindweave.Application.Credentials;

namespace Mindweave.Infrastructure.Tests.Chat;

public sealed class MemoryCredentialStore : ICredentialStore
{
    public ConcurrentDictionary<string, string> Items { get; } = new();

    /// <summary>Simulates a locked or missing system credential service.</summary>
    public bool Unavailable { get; set; }

    public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return Task.FromResult(Items.GetValueOrDefault(reference));
    }

    public Task WriteAsync(string reference, string secret, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        Items[reference] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        Items.TryRemove(reference, out _);
        return Task.CompletedTask;
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new CredentialUnavailableException("simulated: keychain locked");
        }
    }
}
