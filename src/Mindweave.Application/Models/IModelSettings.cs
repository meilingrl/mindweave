using Mindweave.Application.Storage;

namespace Mindweave.Application.Models;

/// <summary>The model connection settings as the settings page uses them (course requirement C3).</summary>
public interface IModelSettings
{
    Task<ModelSettings?> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether the saved settings' key is still in the system's credential store. A restored backup can point at a key
    /// replaced (and deleted) since (health check 2026-11-12, gap-schema-2). True when the store cannot be asked (locked):
    /// the key may well be there.
    /// </summary>
    Task<bool> SavedKeyExistsAsync(CancellationToken cancellationToken);

    Task SaveAsync(ModelSettingsInput input, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> DiscoverModelsAsync(string baseUrl, string? typedKey, CancellationToken cancellationToken);

    Task<ConnectionTestResult> TestAsync(string baseUrl, string model, string? typedKey, CancellationToken cancellationToken);
}
