using Mindweave.Application.Storage;
using Mindweave.Core.Models;

namespace Mindweave.Application.Models;

/// <summary>The model connection settings as the settings page uses them (course requirement C3).</summary>
public interface IModelSettings
{
    Task<ModelSettings?> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether the active model can do <paramref name="capability"/> (take images, call tools, …): the one question a
    /// feature asks before offering something that needs it, instead of looking at the service or model name (E4).
    /// False while no model is configured and for anything the model is not declared to have. A request already
    /// accepted asks its own frozen snapshot (<c>ModelSnapshot.Capabilities.Has</c>) instead.
    /// </summary>
    Task<bool> ActiveModelHasAsync(ModelCapability capability, CancellationToken cancellationToken);

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
