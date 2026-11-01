using Mindweave.Application.Storage;

namespace Mindweave.Application.Models;

/// <summary>The model connection settings as the settings page uses them (course requirement C3).</summary>
public interface IModelSettings
{
    Task<ModelSettings?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(ModelSettingsInput input, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> DiscoverModelsAsync(string baseUrl, string? typedKey, CancellationToken cancellationToken);

    Task<ConnectionTestResult> TestAsync(string baseUrl, string model, string? typedKey, CancellationToken cancellationToken);
}
