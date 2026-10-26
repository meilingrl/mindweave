using Mindweave.Core.Models;

namespace Mindweave.Application.Storage;

/// <summary>The active connection and model as stored (no secret).</summary>
public sealed record ModelSettings(
    string ConnectionId,
    string ConnectionName,
    string BaseUrl,
    ChatProtocol Protocol,
    string CredentialRef,
    string ModelConfigId,
    string Model,
    int? MaxOutputTokens,
    ModelCapabilities Capabilities);

public interface IModelSettingsStore
{
    Task<ModelSettings?> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>Saves connection and model config and marks them active, in one transaction.</summary>
    Task SaveActiveAsync(ModelSettings settings, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>References replaced by newer credentials, waiting to be deleted once no task uses them.</summary>
    Task<IReadOnlyList<string>> RetiredCredentialRefsAsync(CancellationToken cancellationToken);

    Task SetRetiredCredentialRefsAsync(IReadOnlyList<string> references, CancellationToken cancellationToken);
}
