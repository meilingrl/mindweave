using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Credentials;
using Mindweave.Application.Storage;
using Mindweave.Core.Models;

namespace Mindweave.Application.Models;

public enum ModelSettingsProblem
{
    InvalidUrl,
    InsecureUrl,
    MissingModel,
    MissingKey,
    InvalidContextWindow,
}

/// <param name="ContextWindowTokens">The model's context capacity as the user declared it; null when unknown (a default applies).</param>
public sealed record ModelSettingsInput(string Name, string BaseUrl, string Model, string? NewApiKey, int? ContextWindowTokens = null)
{
    public const int MinContextWindow = 2_000;
    public const int MaxContextWindow = 10_000_000;
}

public sealed class ModelSettingsException(ModelSettingsProblem problem) : Exception(problem.ToString())
{
    public ModelSettingsProblem Problem { get; } = problem;
}

public sealed record ConnectionTestResult(bool Succeeded, Mindweave.Core.Conversations.GenerationErrorKind? Error, string? Detail);

/// <summary>
/// The one active connection and model (first version manages a single configuration). Keys go to the
/// credential store under a new reference each time they change; the old reference is deleted only once no
/// unfinished task still uses it (MW-C15).
/// </summary>
public sealed partial class ModelSettingsService(
    IModelSettingsStore store,
    IConversationStore conversations,
    ICredentialStore credentials,
    IChatClient client,
    IClock clock,
    ILogger<ModelSettingsService> logger) : IModelSettings
{
    public event EventHandler? Changed;

    public Task<ModelSettings?> GetAsync(CancellationToken cancellationToken) => store.GetActiveAsync(cancellationToken);

    /// <summary>The frozen snapshot new tasks are accepted with, or null when nothing is configured.</summary>
    public async Task<ModelSnapshot?> GetActiveSnapshotAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        return settings is null
            ? null
            : new ModelSnapshot(settings.ModelConfigId, settings.BaseUrl, settings.Protocol, settings.Model, settings.CredentialRef, settings.MaxOutputTokens, settings.Capabilities);
    }

    public async Task SaveAsync(ModelSettingsInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var baseUrl = NormalizeBaseUrl(input.BaseUrl);
        var model = input.Model.Trim();
        if (model.Length == 0)
        {
            throw new ModelSettingsException(ModelSettingsProblem.MissingModel);
        }

        if (input.ContextWindowTokens is < ModelSettingsInput.MinContextWindow or > ModelSettingsInput.MaxContextWindow)
        {
            throw new ModelSettingsException(ModelSettingsProblem.InvalidContextWindow);
        }

        var existing = await store.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var connectionId = existing?.ConnectionId ?? Guid.CreateVersion7().ToString("D");
        var credentialRef = existing?.CredentialRef;
        var retired = (await store.RetiredCredentialRefsAsync(cancellationToken).ConfigureAwait(false)).ToList();

        if (!string.IsNullOrWhiteSpace(input.NewApiKey))
        {
            var newRef = $"{connectionId}/{Guid.CreateVersion7():D}";
            await credentials.WriteAsync(newRef, input.NewApiKey.Trim(), cancellationToken).ConfigureAwait(false);
            if (credentialRef is not null)
            {
                retired.Add(credentialRef);
            }

            credentialRef = newRef;
        }

        if (credentialRef is null)
        {
            throw new ModelSettingsException(ModelSettingsProblem.MissingKey);
        }

        var name = string.IsNullOrWhiteSpace(input.Name) ? new Uri(baseUrl).Host : input.Name.Trim();
        await store.SaveActiveAsync(
                new ModelSettings(
                    connectionId,
                    name,
                    baseUrl,
                    ChatProtocol.ChatCompletions,
                    credentialRef,
                    existing?.ModelConfigId ?? Guid.CreateVersion7().ToString("D"),
                    model,
                    existing?.MaxOutputTokens,
                    // Declared capabilities belong to a model; a different model starts from the protocol defaults. The
                    // context capacity is what the form says now.
                    (existing is not null && existing.Model == model ? existing.Capabilities : ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions))
                        with { ContextWindowTokens = input.ContextWindowTokens }),
                clock.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        await store.SetRetiredCredentialRefsAsync(retired, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        await CleanUpRetiredCredentialsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes replaced keys that no unfinished task references any more. Safe to call any time.</summary>
    public async Task CleanUpRetiredCredentialsAsync(CancellationToken cancellationToken)
    {
        var retired = await store.RetiredCredentialRefsAsync(cancellationToken).ConfigureAwait(false);
        if (retired.Count == 0)
        {
            return;
        }

        var inUse = await conversations.CredentialRefsInUseAsync(cancellationToken).ConfigureAwait(false);
        var active = (await store.GetActiveAsync(cancellationToken).ConfigureAwait(false))?.CredentialRef;
        var keep = new List<string>();
        foreach (var reference in retired)
        {
            if (inUse.Contains(reference) || reference == active)
            {
                keep.Add(reference);
                continue;
            }

            try
            {
                await credentials.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
            }
            catch (CredentialUnavailableException ex)
            {
                LogCleanupDeferred(logger, ex);
                keep.Add(reference);
            }
        }

        await store.SetRetiredCredentialRefsAsync(keep, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists models with the typed key, or with the saved key when none is typed. Not a generation request.</summary>
    public async Task<IReadOnlyList<string>> DiscoverModelsAsync(string baseUrl, string? typedKey, CancellationToken cancellationToken)
    {
        var key = await KeyForAsync(typedKey, cancellationToken).ConfigureAwait(false);
        return await client.ListModelsAsync(NormalizeBaseUrl(baseUrl), key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Explicit connection test: one very short generation, which the provider may bill.</summary>
    public async Task<ConnectionTestResult> TestAsync(string baseUrl, string model, string? typedKey, CancellationToken cancellationToken)
    {
        try
        {
            var key = await KeyForAsync(typedKey, cancellationToken).ConfigureAwait(false);
            var snapshot = new ModelSnapshot("connection-test", NormalizeBaseUrl(baseUrl), ChatProtocol.ChatCompletions, model.Trim(), "connection-test", MaxOutputTokens: 16, ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions));
            var request = new ChatRequest(snapshot, [new ChatTurn(ChatRole.User, "Reply with the single word OK.")]);
            await foreach (var item in client.StreamAsync(request, key, cancellationToken).ConfigureAwait(false))
            {
                if (item is ChatStreamEvent.Completed)
                {
                    return new ConnectionTestResult(true, null, null);
                }
            }

            return new ConnectionTestResult(false, Core.Conversations.GenerationErrorKind.InvalidResponse, "The stream ended without completing.");
        }
        catch (ChatClientException ex)
        {
            return new ConnectionTestResult(false, ex.Kind, ex.Message);
        }
        catch (CredentialUnavailableException ex)
        {
            return new ConnectionTestResult(false, Core.Conversations.GenerationErrorKind.CredentialUnavailable, ex.Message);
        }
    }

    /// <summary>Absolute http(s) URL without trailing slash; plain http only for this machine.</summary>
    public static string NormalizeBaseUrl(string raw)
    {
        if (!Uri.TryCreate((raw ?? string.Empty).Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ModelSettingsException(ModelSettingsProblem.InvalidUrl);
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new ModelSettingsException(ModelSettingsProblem.InsecureUrl);
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private async Task<string> KeyForAsync(string? typedKey, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(typedKey))
        {
            return typedKey.Trim();
        }

        var saved = await store.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var key = saved is null ? null : await credentials.ReadAsync(saved.CredentialRef, cancellationToken).ConfigureAwait(false);
        return key ?? throw new ModelSettingsException(ModelSettingsProblem.MissingKey);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Credential cleanup deferred: credential store unavailable")]
    private static partial void LogCleanupDeferred(ILogger logger, Exception exception);
}
