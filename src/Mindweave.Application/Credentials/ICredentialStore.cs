namespace Mindweave.Application.Credentials;

/// <summary>
/// The operating system's secret store (Keychain, Windows Credential Manager). Secrets never enter the
/// database; it stores only the reference. When the store is locked or missing this fails loudly —
/// there is no plain-text fallback.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Returns null when no secret exists for the reference.</summary>
    Task<string?> ReadAsync(string reference, CancellationToken cancellationToken);

    Task WriteAsync(string reference, string secret, CancellationToken cancellationToken);

    Task DeleteAsync(string reference, CancellationToken cancellationToken);
}

public sealed class CredentialUnavailableException : Exception
{
    public CredentialUnavailableException()
    {
    }

    public CredentialUnavailableException(string message)
        : base(message)
    {
    }

    public CredentialUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
