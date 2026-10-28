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
    /// Last resort after the exit coordinator (architecture §12.3 step 6): stops generation and flushes page state again
    /// (both idempotent, normally nothing is left), drains pending writes and closes the database. The single-instance
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
                await Services.GetRequiredService<Mindweave.Application.Reading.ReadingStateService>().FlushAsync().ConfigureAwait(false);
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
