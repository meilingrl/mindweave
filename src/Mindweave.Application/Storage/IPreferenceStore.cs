namespace Mindweave.Application.Storage;

/// <summary>
/// Small per-installation preferences kept in the local database (<c>app_state</c>), never in the credential store.
/// Keys and values are opaque to storage; the services that own a preference give them meaning.
/// </summary>
public interface IPreferenceStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string value, CancellationToken cancellationToken);
}
