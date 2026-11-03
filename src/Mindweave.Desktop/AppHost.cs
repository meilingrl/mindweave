using Microsoft.Extensions.DependencyInjection;
using Mindweave.Desktop.ViewModels;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop;

/// <summary>Process-lifetime resources: the single-instance lock and the service provider.</summary>
internal sealed class AppHost(SingleInstance instance, ServiceProvider services) : IAsyncDisposable
{
    public SingleInstance Instance { get; } = instance;

    public ServiceProvider Services { get; } = services;

    /// <summary>
    /// Last resort after the exit coordinator (architecture §12.3 step 6): stops generation, stops storage recovery, and
    /// flushes kept writes and page state again (all idempotent, normally nothing is left), then closes the database. The single-instance
    /// lock is released whatever fails before it.
    /// </summary>
    /// <remarks>
    /// Runs on the UI thread with the window already gone. When the coordinator has stopped generation (it is
    /// releasing) a task still running has already had its bounded wait, so this one does not wait for it again.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            try
            {
                // Stop first, so terminal states are written before the database closes; bounded like the exit.
                var wait = Services.GetRequiredService<ExitCoordinator>().Stage == ExitStage.Releasing
                    ? TimeSpan.Zero
                    : Services.GetRequiredService<ExitOptions>().StopWait;
                await Services.GetRequiredService<Mindweave.Application.Conversations.GenerationCoordinator>()
                    .ShutdownAsync(wait).ConfigureAwait(false);

                // Recovery stops (and a probe or retry in progress finishes) before the database closes; then every
                // owner tries what it kept once more, while the database is still open (architecture review R3).
                var health = Services.GetRequiredService<Mindweave.Application.Storage.StorageHealth>();
                await health.StopAsync().ConfigureAwait(false);
                await health.RetryKeptAsync().ConfigureAwait(false);
                await Services.GetRequiredService<Mindweave.Application.Reading.ReadingStateService>().FlushAsync().ConfigureAwait(false);
                await Services.GetRequiredService<Mindweave.Application.Conversations.ConversationMarksService>().FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await Services.GetRequiredService<SqliteDatabase>().CloseAsync().ConfigureAwait(false);
                }
                finally
                {
                    await Services.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await Instance.DisposeAsync().ConfigureAwait(false);
        }
    }
}
