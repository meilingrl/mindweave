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

    /// <summary>An answer output limit below <see cref="ModelSettingsInput.MinOutputLimit"/> or above half the context capacity.</summary>
    InvalidOutputLimit,

    /// <summary>A user name or password in the address (low finding security-5): it would be stored and sent in plain text.</summary>
    CredentialsInUrl,
}

/// <param name="ContextWindowTokens">The model's context capacity as the user declared it; null when unknown (a default applies).</param>
/// <param name="Capabilities">
/// What the model can do as the form declares it (a preset's suggestion or the user's override); its context capacity is
/// ignored in favour of <paramref name="ContextWindowTokens"/>. Null keeps what is saved for the same model and starts a
/// different model from the protocol defaults.
/// </param>
/// <param name="MaxOutputTokens">
/// The answer output limit as the user set it (高级设置 › 输出上限, 2026-11-21); null sends none and the service's default
/// applies. Internal requests (summaries, compression, names) keep their own limits.
/// </param>
public sealed record ModelSettingsInput(
    string Name,
    string BaseUrl,
    string Model,
    string? NewApiKey,
    int? ContextWindowTokens = null,
    ModelCapabilities? Capabilities = null,
    int? MaxOutputTokens = null)
{
    public const int MinContextWindow = 2_000;
    public const int MaxContextWindow = 10_000_000;

    /// <summary>Below this an answer is cut off almost at once, and a reasoning model may never reach its answer.</summary>
    public const int MinOutputLimit = 256;

    /// <summary>
    /// The largest output limit for this input: half the context capacity (declared, or the default), so the reserve
    /// never leaves the question less room than the answer.
    /// </summary>
    public int MaxOutputLimit => (ContextWindowTokens ?? Mindweave.Core.Context.ContextBudget.DefaultCapacity) / 2;
}

public sealed class ModelSettingsException(ModelSettingsProblem problem) : Exception(problem.ToString())
{
    public ModelSettingsProblem Problem { get; } = problem;
}

/// <summary>
/// A saved key is only ever sent to the service it was saved for. Same service means same scheme, host and port; a
/// different path on the same host (another API version) is the same service.
/// </summary>
public static class ServiceAddress
{
    public static bool SameService(string? savedBaseUrl, string? baseUrl) =>
        Uri.TryCreate((savedBaseUrl ?? string.Empty).Trim(), UriKind.Absolute, out var saved)
        && Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out var current)
        && Uri.Compare(saved, current, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
}

/// <summary>The outcome of 测试连接; a failure carries its details (D2 §2.1): time, model, status, request ID, duration and text.</summary>
public sealed record ConnectionTestResult(
    bool Succeeded,
    Mindweave.Core.Conversations.GenerationErrorKind? Error,
    string? Detail,
    Diagnostics.FailureDetails? Details = null);

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
    Diagnostics.KnownSecrets secrets,
    ILogger<ModelSettingsService> logger) : IModelSettings
{
    public event EventHandler? Changed;

    public Task<ModelSettings?> GetAsync(CancellationToken cancellationToken) => store.GetActiveAsync(cancellationToken);

    public async Task<bool> ActiveModelHasAsync(ModelCapability capability, CancellationToken cancellationToken) =>
        ((await store.GetActiveAsync(cancellationToken).ConfigureAwait(false))?.Capabilities ?? ModelCapabilities.None).Has(capability);

    public async Task<bool> SavedKeyExistsAsync(CancellationToken cancellationToken)
    {
        if (await store.GetActiveAsync(cancellationToken).ConfigureAwait(false) is not { CredentialRef: { Length: > 0 } reference })
        {
            return false;
        }

        try
        {
            return await credentials.ReadAsync(reference, cancellationToken).ConfigureAwait(false) is { Length: > 0 };
        }
        catch (CredentialUnavailableException)
        {
            return true;
        }
    }

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

        if (input.MaxOutputTokens is { } limit && (limit < ModelSettingsInput.MinOutputLimit || limit > input.MaxOutputLimit))
        {
            throw new ModelSettingsException(ModelSettingsProblem.InvalidOutputLimit);
        }

        var existing = await store.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var connectionId = existing?.ConnectionId ?? Guid.CreateVersion7().ToString("D");
        // A saved key belongs to the service it was saved for: another host needs its own key.
        var credentialRef = existing is not null && ServiceAddress.SameService(existing.BaseUrl, baseUrl) ? existing.CredentialRef : null;
        var retired = (await store.RetiredCredentialRefsAsync(cancellationToken).ConfigureAwait(false)).ToList();

        if (!string.IsNullOrWhiteSpace(input.NewApiKey))
        {
            var newRef = $"{connectionId}/{Guid.CreateVersion7():D}";
            await credentials.WriteAsync(newRef, input.NewApiKey.Trim(), cancellationToken).ConfigureAwait(false);
            if (existing?.CredentialRef is { } replaced)
            {
                retired.Add(replaced);
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
                    input.MaxOutputTokens,
                    // What the form declares wins. Otherwise declared capabilities belong to a model, and a different model
                    // starts from the protocol defaults. The context capacity is what the form says now.
                    (input.Capabilities
                     ?? (existing is not null && existing.Model == model ? existing.Capabilities : ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions)))
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

    /// <summary>
    /// Lists models with the typed key, or with the saved key when none is typed and the address is the saved service's.
    /// Not a generation request.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverModelsAsync(string baseUrl, string? typedKey, CancellationToken cancellationToken)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        var key = await KeyForAsync(normalized, typedKey, cancellationToken).ConfigureAwait(false);
        return await client.ListModelsAsync(normalized, key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Explicit connection test: one very short generation, which the provider may bill.</summary>
    public async Task<ConnectionTestResult> TestAsync(string baseUrl, string model, string? typedKey, CancellationToken cancellationToken)
    {
        var started = clock.UtcNow;
        var name = model.Trim();
        var normalized = NormalizeBaseUrl(baseUrl);
        int? status = null;
        string? requestId = null;
        ConnectionTestResult result;
        try
        {
            var key = await KeyForAsync(normalized, typedKey, cancellationToken).ConfigureAwait(false);
            secrets.Add(key); // A typed key never passes the key store; the log scrubs it all the same (D2).
            var snapshot = new ModelSnapshot("connection-test", normalized, ChatProtocol.ChatCompletions, name, "connection-test", MaxOutputTokens: 16, ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions));
            var request = new ChatRequest(snapshot, [new ChatTurn(ChatRole.User, "Reply with the single word OK.")]);
            result = Failed(Core.Conversations.GenerationErrorKind.InvalidResponse, "The stream ended without completing.");
            await foreach (var item in client.StreamAsync(request, key, cancellationToken).ConfigureAwait(false))
            {
                if (item is ChatStreamEvent.ResponseStarted response)
                {
                    (status, requestId) = (response.HttpStatus, response.ProviderRequestId);
                }
                else if (item is ChatStreamEvent.Completed)
                {
                    result = new ConnectionTestResult(true, null, null);
                    break;
                }
            }
        }
        catch (ChatClientException ex)
        {
            (status, requestId) = (ex.HttpStatus ?? status, ex.ProviderRequestId ?? requestId);
            result = Failed(ex.Kind, ex.Message);
        }
        catch (CredentialUnavailableException ex)
        {
            result = Failed(Core.Conversations.GenerationErrorKind.CredentialUnavailable, ex.Message);
        }

        // Low finding observability-8: a connection test leaves a trace, without the key or the address's path.
        var host = Host(normalized);
        var outcome = result.Succeeded ? "succeeded" : $"failed ({result.Error})";
        var took = (long)(clock.UtcNow - started).TotalMilliseconds;
        LogTested(logger, host, name, outcome, status, took);
        return result;

        ConnectionTestResult Failed(Core.Conversations.GenerationErrorKind kind, string detail) =>
            new(false, kind, detail, new Diagnostics.FailureDetails(started, name, status, requestId, clock.UtcNow - started, Diagnostics.Redaction.Scrub(detail)));
    }

    /// <summary>Only the host of a service address goes into the log.</summary>
    private static string Host(string normalizedBaseUrl) =>
        Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var uri) ? uri.Authority : "?";

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

        if (uri.UserInfo.Length > 0)
        {
            throw new ModelSettingsException(ModelSettingsProblem.CredentialsInUrl);
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private async Task<string> KeyForAsync(string normalizedBaseUrl, string? typedKey, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(typedKey))
        {
            return typedKey.Trim();
        }

        var saved = await store.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null && !ServiceAddress.SameService(saved.BaseUrl, normalizedBaseUrl))
        {
            throw new ModelSettingsException(ModelSettingsProblem.MissingKey);
        }

        var key = saved is null ? null : await credentials.ReadAsync(saved.CredentialRef, cancellationToken).ConfigureAwait(false);
        return key ?? throw new ModelSettingsException(ModelSettingsProblem.MissingKey);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection test to {Host}, model {Model}: {Outcome}, status {Status}, {Milliseconds} ms")]
    private static partial void LogTested(ILogger logger, string host, string model, string outcome, int? status, long milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Credential cleanup deferred: credential store unavailable")]
    private static partial void LogCleanupDeferred(ILogger logger, Exception exception);
}
