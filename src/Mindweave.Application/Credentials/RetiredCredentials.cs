using Microsoft.Extensions.Logging;
using Mindweave.Application.Storage;

namespace Mindweave.Application.Credentials;

/// <summary>
/// Model keys that were replaced or removed, waiting to be deleted from the system's key store once no unfinished task uses
/// them (MW-C15). The list is cleaned when settings are saved, at start-up and when a task ends (D2 §2.3; until 2026-11-27
/// only a save cleaned it, so a key retired while a task ran stayed until the next save). Every change to the list goes
/// through one gate, so a cleanup running in the background never drops a reference a save has just added. Deletes only;
/// never reads a key.
/// </summary>
public sealed partial class RetiredCredentials(
    IModelSettingsStore settings,
    IConversationStore conversations,
    ICredentialStore credentials,
    ILogger<RetiredCredentials> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _scheduled;

    /// <summary>The cleanup <see cref="CleanUpSoon"/> started last; it never faults (tests wait for it).</summary>
    public Task Latest { get; private set; } = Task.CompletedTask;

    /// <summary>Adds a reference that nothing new may use; it is deleted once no unfinished task holds it.</summary>
    public async Task RetireAsync(string reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var retired = await settings.RetiredCredentialRefsAsync(cancellationToken).ConfigureAwait(false);
            if (!retired.Contains(reference))
            {
                await settings.SetRetiredCredentialRefsAsync([.. retired, reference], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes the retired keys that no unfinished task and not the active settings refer to. A key store that is locked or
    /// does not answer keeps them for the next time. Safe to call any time.
    /// </summary>
    public async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var retired = await settings.RetiredCredentialRefsAsync(cancellationToken).ConfigureAwait(false);
            if (retired.Count == 0)
            {
                return;
            }

            var inUse = await conversations.CredentialRefsInUseAsync(cancellationToken).ConfigureAwait(false);
            var active = (await settings.GetActiveAsync(cancellationToken).ConfigureAwait(false))?.CredentialRef;
            var keep = new List<string>();
            var removed = 0;
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
                    removed++;
                }
                catch (CredentialUnavailableException ex)
                {
                    LogCleanupDeferred(logger, ex);
                    keep.Add(reference);
                }
            }

            await settings.SetRetiredCredentialRefsAsync(keep, cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                LogRemoved(logger, removed, keep.Count);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <see cref="CleanUpAsync"/> in the background, for start-up and the end of a task: it never delays them, calls that
    /// arrive while one waits to start join it, and a failure is only logged (the next occasion tries again).
    /// </summary>
    public void CleanUpSoon()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
        {
            return;
        }

        Latest = Task.Run(async () =>
        {
            // Cleared before the list is read: a task that ends from here on asks for a cleanup of its own.
            Volatile.Write(ref _scheduled, 0);
            try
            {
                await CleanUpAsync(CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Housekeeping: storage paused or closing, or anything else, waits for the next occasion.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogCleanupFailed(logger, ex);
            }
        });
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Credential cleanup deferred: credential store unavailable")]
    private static partial void LogCleanupDeferred(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} retired keys from the system's key store; {Kept} kept for now")]
    private static partial void LogRemoved(ILogger logger, int count, int kept);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Credential cleanup failed; it is tried again later")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}
