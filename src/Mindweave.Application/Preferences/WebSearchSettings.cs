using Microsoft.Extensions.Logging;
using Mindweave.Application.Credentials;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Languages;
using Mindweave.Application.Storage;
using Mindweave.Application.Tools;
using Mindweave.Core.Runs;

namespace Mindweave.Application.Preferences;

/// <summary>What 设置 › 工具 › 网络搜索 shows of the service: which one, whether a key is set, the switch's default.</summary>
public sealed record WebSearchState(string ProviderId, string ProviderName, bool HasKey, WebSearchMode DefaultMode);

/// <summary>The outcome of 测试: usable, or why not (in words, in both languages).</summary>
public sealed record WebSearchTestResult(bool Succeeded, BilingualText? Problem, SearchFailureKind? Kind = null)
{
    /// <summary>When the test ran and how long it took (D2 §2.1); the reason is <see cref="Problem"/>.</summary>
    public FailureDetails? Details { get; init; }
}

/// <summary>
/// 设置 › 工具 › 网络搜索 (requirements §10.4.5, decision W14): the search service, its key and the default of the switch beside
/// the composer. The key lives in the system's credential store; preferences hold only its reference
/// (<c>websearch.credential</c>), the service (<c>websearch.provider</c>) and the default (<c>websearch.default</c>). The key is
/// never returned to the page, written to a log or exported.
/// </summary>
public interface IWebSearchSettings
{
    Task<WebSearchState> GetAsync(CancellationToken cancellationToken);

    /// <summary>Whether a key is set and can be read (a locked store counts as none).</summary>
    Task<bool> HasKeyAsync(CancellationToken cancellationToken);

    /// <summary>The key, for a tool's request only; null when none is set. Throws <see cref="CredentialUnavailableException"/>.</summary>
    Task<string?> ReadKeyAsync(CancellationToken cancellationToken);

    /// <summary>Stores a new key under a new reference and retires the old one.</summary>
    Task SetKeyAsync(string key, CancellationToken cancellationToken);

    Task RemoveKeyAsync(CancellationToken cancellationToken);

    Task<WebSearchMode> GetDefaultModeAsync(CancellationToken cancellationToken);

    Task SetDefaultModeAsync(WebSearchMode mode, CancellationToken cancellationToken);

    /// <summary>One real search with one result (1 credit), with <paramref name="key"/> or, when null, the stored key.</summary>
    Task<WebSearchTestResult> TestAsync(string? key, CancellationToken cancellationToken);

    /// <summary>The service the settings name (the only one in phase 3).</summary>
    ISearchProvider Provider { get; }

    /// <summary>A key was set or removed, or the default changed.</summary>
    event EventHandler? Changed;
}

public sealed partial class WebSearchSettingsService : IWebSearchSettings
{
    public const string ProviderKey = "websearch.provider";
    public const string CredentialKey = "websearch.credential";
    public const string DefaultKey = "websearch.default";

    /// <summary>What 测试 searches for: a word with results everywhere, nothing about the user.</summary>
    internal const string TestQuery = "Mindweave";

    private readonly IPreferenceStore _store;
    private readonly ICredentialStore _credentials;
    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly IClock _clock;
    private readonly KnownSecrets _secrets;
    private readonly ILogger<WebSearchSettingsService> _logger;

    public WebSearchSettingsService(
        IPreferenceStore store,
        ICredentialStore credentials,
        IEnumerable<ISearchProvider> providers,
        IClock clock,
        KnownSecrets secrets,
        ILogger<WebSearchSettingsService> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _clock = clock;
        _secrets = secrets;
        _logger = logger;
        _store = store;
        _credentials = credentials;
        _providers = [.. providers];
        if (_providers.Count == 0)
        {
            throw new ArgumentException("At least one search service is needed.", nameof(providers));
        }
    }

    public event EventHandler? Changed;

    public ISearchProvider Provider => _providers[0];

    public async Task<WebSearchState> GetAsync(CancellationToken cancellationToken) =>
        new(Provider.Id, Provider.DisplayName, await HasKeyAsync(cancellationToken).ConfigureAwait(false), await GetDefaultModeAsync(cancellationToken).ConfigureAwait(false));

    public async Task<bool> HasKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadKeyAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 };
        }
        catch (CredentialUnavailableException)
        {
            return false;
        }
    }

    public async Task<string?> ReadKeyAsync(CancellationToken cancellationToken)
    {
        var reference = await _store.GetAsync(CredentialKey, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(reference) ? null : await _credentials.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetKeyAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var old = await _store.GetAsync(CredentialKey, cancellationToken).ConfigureAwait(false);
        var reference = $"websearch/{Provider.Id}/{Guid.CreateVersion7():D}";
        await _credentials.WriteAsync(reference, key.Trim(), cancellationToken).ConfigureAwait(false);
        await _store.SetAsync(ProviderKey, Provider.Id, cancellationToken).ConfigureAwait(false);
        await _store.SetAsync(CredentialKey, reference, cancellationToken).ConfigureAwait(false);
        await RetireAsync(old).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveKeyAsync(CancellationToken cancellationToken)
    {
        var old = await _store.GetAsync(CredentialKey, cancellationToken).ConfigureAwait(false);
        await _store.SetAsync(CredentialKey, string.Empty, cancellationToken).ConfigureAwait(false);
        await RetireAsync(old).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<WebSearchMode> GetDefaultModeAsync(CancellationToken cancellationToken) =>
        ParseMode(await _store.GetAsync(DefaultKey, cancellationToken).ConfigureAwait(false));

    public async Task SetDefaultModeAsync(WebSearchMode mode, CancellationToken cancellationToken)
    {
        await _store.SetAsync(DefaultKey, Format(mode), cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<WebSearchTestResult> TestAsync(string? key, CancellationToken cancellationToken)
    {
        var started = _clock.UtcNow;
        var result = await TestCoreAsync(key, cancellationToken).ConfigureAwait(false);
        var outcome = result.Succeeded ? "succeeded" : $"failed ({result.Kind?.ToString() ?? "key store"})";
        var took = (long)(_clock.UtcNow - started).TotalMilliseconds;
        LogTested(_logger, Provider.Id, outcome, took); // Low finding observability-8: a test leaves a trace.
        return result.Succeeded
            ? result
            : result with { Details = new FailureDetails(started, null, null, null, _clock.UtcNow - started, null) };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Web search test with {Provider}: {Outcome}, {Milliseconds} ms")]
    private static partial void LogTested(ILogger logger, string provider, string outcome, long milliseconds);

    private async Task<WebSearchTestResult> TestCoreAsync(string? key, CancellationToken cancellationToken)
    {
        string? secret;
        try
        {
            secret = string.IsNullOrWhiteSpace(key) ? await ReadKeyAsync(cancellationToken).ConfigureAwait(false) : key.Trim();
        }
        catch (CredentialUnavailableException ex)
        {
            return new WebSearchTestResult(false, new BilingualText(ex.Message, ex.Message));
        }

        if (string.IsNullOrEmpty(secret))
        {
            return new WebSearchTestResult(false, new BilingualText("No key has been entered yet.", "还没有填写密钥。"), SearchFailureKind.MissingKey);
        }

        _secrets.Add(secret); // A typed key never passes the key store; the log scrubs it all the same (D2).

        try
        {
            await Provider.SearchAsync(TestQuery, 1, secret, cancellationToken).ConfigureAwait(false);
            return new WebSearchTestResult(true, null);
        }
        catch (SearchProviderException ex)
        {
            // Held for the settings page: it reads in the language of the moment it is shown.
            return new WebSearchTestResult(false, ex.Text, ex.Kind);
        }
    }

    public static WebSearchMode ParseMode(string? stored) => stored switch
    {
        "on" => WebSearchMode.On,
        "off" => WebSearchMode.Off,
        _ => WebSearchMode.Auto,
    };

    public static string Format(WebSearchMode mode) => mode switch
    {
        WebSearchMode.On => "on",
        WebSearchMode.Off => "off",
        _ => "auto",
    };

    /// <summary>The old key is deleted once the new reference is stored; a locked store leaves it (it is unreachable either way).</summary>
    private async Task RetireAsync(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        try
        {
            await _credentials.DeleteAsync(reference, CancellationToken.None).ConfigureAwait(false);
        }
        catch (CredentialUnavailableException)
        {
        }
    }
}
