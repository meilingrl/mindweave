using Microsoft.Extensions.DependencyInjection;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop;

/// <summary>Process-lifetime resources: the single-instance lock and the service provider.</summary>
internal sealed class AppHost(SingleInstance instance, ServiceProvider services) : IAsyncDisposable
{
    public SingleInstance Instance { get; } = instance;

    public ServiceProvider Services { get; } = services;

    /// <summary>Drains pending writes and closes the database before the lock is released.</summary>
    public async ValueTask DisposeAsync()
    {
        // Stop generation first (idempotent if the exit prompt already did), so terminal states are written
        // before the database closes.
        await Services.GetRequiredService<Mindweave.Application.Conversations.GenerationCoordinator>().ShutdownAsync().ConfigureAwait(false);
        await Services.GetRequiredService<Mindweave.Application.Reading.ReadingStateService>().FlushAsync().ConfigureAwait(false);
        await Services.GetRequiredService<SqliteDatabase>().CloseAsync().ConfigureAwait(false);
        await Services.DisposeAsync().ConfigureAwait(false);
        await Instance.DisposeAsync().ConfigureAwait(false);
    }
}
