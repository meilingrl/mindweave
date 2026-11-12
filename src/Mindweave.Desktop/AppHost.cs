using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Startup;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop;

/// <summary>Process-lifetime resources: the single-instance lock and the services of the current session.</summary>
internal sealed class AppHost(SingleInstance instance, AppSession session) : IAsyncDisposable
{
    /// <summary>A host over services that are never replaced (no in-app restore); used where only the shutdown matters.</summary>
    public AppHost(SingleInstance instance, ServiceProvider services)
        : this(instance, AppSession.Fixed(services))
    {
    }

    public SingleInstance Instance { get; } = instance;

    public AppSession Session { get; } = session;

    public ServiceProvider Services => Session.Services;

    /// <summary>
    /// Runs the desktop lifetime's loop (<paramref name="runUiLoop"/>), then releases everything. The database closes only
    /// once that loop has returned: by then Avalonia's dispatcher has shut down, so queued UI work was dropped and new posts
    /// are refused, and no page refresh can reach a closed database. Closing it in the lifetime's <c>Exit</c> event was too
    /// early: on macOS the loop still ran one queued UI job after that event in about half of the exits (2026-11-05).
    /// </summary>
    /// <remarks>
    /// Released on a pool thread: the UI thread's synchronization context may still be installed, and a continuation
    /// posted to the stopped dispatcher would never run.
    /// </remarks>
    public int Run(Func<int> runUiLoop)
    {
        ArgumentNullException.ThrowIfNull(runUiLoop);
        try
        {
            return runUiLoop();
        }
        finally
        {
            // Also when the loop throws: kept writes are flushed, the database closed and the single-instance lock released.
            Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Last resort after the exit coordinator (architecture §12.3 step 6): stops generation, stops storage recovery, and
    /// flushes kept writes and page state again (all idempotent, normally nothing is left), then closes the database. The single-instance
    /// lock is released whatever fails before it.
    /// </summary>
    /// <remarks>
    /// Runs after the UI loop has ended (see <see cref="Run"/>), with the window gone. When the coordinator has stopped
    /// generation (it is releasing) a task still running has already had its bounded wait, so this one does not wait for it again.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            var services = Services;
            var wait = services.GetRequiredService<ExitCoordinator>().Stage == ExitStage.Releasing
                ? TimeSpan.Zero
                : services.GetRequiredService<ExitOptions>().StopWait;
            await AppSession.EndAsync(services, wait).ConfigureAwait(false);
        }
        finally
        {
            await Instance.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The services of the open database, for as long as the window and the single-instance lock live. Restoring a backup
/// from inside the application (2026-11-12) ends these services and starts new ones on the same window and lock: every
/// service, page and cache then starts from the restored data exactly as after a fresh start, which resetting the
/// state held by some twenty singletons one by one could not promise.
/// </summary>
public sealed class AppSession : IInAppRestore
{
    private readonly Func<Action<IServiceCollection>, ServiceProvider>? _build;

    /// <param name="build">Builds one session's services; the registrations it is given must be applied (they make this session reachable).</param>
    public AppSession(Func<Action<IServiceCollection>, ServiceProvider> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        _build = build;
        Services = Build();
    }

    private AppSession(ServiceProvider services) => Services = services;

    public ServiceProvider Services { get; private set; }

    public RootViewModel Root => Services.GetRequiredService<RootViewModel>();

    /// <summary>New services took over: the window shows <see cref="Root"/> from now on.</summary>
    public event EventHandler? Replaced;

    /// <summary>The last restore's hand-over and start, for tests; the UI never awaits it.</summary>
    internal Task LastRestore { get; private set; } = Task.CompletedTask;

    /// <summary>A session over services that are never replaced; <see cref="RestoreAsync"/> is refused.</summary>
    internal static AppSession Fixed(ServiceProvider services) => new(services);

    /// <summary>
    /// Called by the restore once the old session has stopped its work and saved everything, and the current data is in
    /// the verified backup <paramref name="keptCopy"/>. The window moves to new services showing the start page, the old
    /// ones end (their database closes), then the new ones put the backup in place and start as usual.
    /// </summary>
    public Task RestoreAsync(BackupInfo backup, string keptCopy)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (_build is null)
        {
            throw new InvalidOperationException("This session cannot be replaced.");
        }

        LastRestore = RunAsync(backup, keptCopy);
        return LastRestore;
    }

    private async Task RunAsync(BackupInfo backup, string keptCopy)
    {
        var old = Services;
        var next = Build();
        next.GetRequiredService<IRootNavigator>().Show(new StartupViewModel());
        Services = next;
        Replaced?.Invoke(this, EventArgs.Empty);

        // The old pages leave with their session: disposed, so none of their timers or reloads reaches the closed database.
        old.GetRequiredService<Workbench>().ReleaseAll();

        // Its generation was stopped by the restore's exit steps; nothing waits for it again.
        await EndAsync(old, TimeSpan.Zero);
        await next.GetRequiredService<AppStartup>().RestoreInPlaceAsync(backup, keptCopy);
    }

    private ServiceProvider Build() => _build!(services => services.AddSingleton<IInAppRestore>(this));

    /// <summary>
    /// Ends one session's services: stops generation (bounded by <paramref name="wait"/>), stops storage recovery, flushes
    /// kept writes and page state (all idempotent, normally nothing is left), then closes the database and disposes them.
    /// </summary>
    internal static async Task EndAsync(ServiceProvider services, TimeSpan wait)
    {
        try
        {
            // Stop first, so terminal states are written before the database closes.
            await services.GetRequiredService<Mindweave.Application.Conversations.GenerationCoordinator>()
                .ShutdownAsync(wait).ConfigureAwait(false);

            // Recovery stops (and a probe or retry in progress finishes) before the database closes; then every
            // owner tries what it kept once more, while the database is still open (architecture review R3).
            var health = services.GetRequiredService<Mindweave.Application.Storage.StorageHealth>();
            await health.StopAsync().ConfigureAwait(false);
            await health.RetryKeptAsync().ConfigureAwait(false);
            await services.GetRequiredService<Mindweave.Application.Reading.ReadingStateService>().FlushAsync().ConfigureAwait(false);
            await services.GetRequiredService<Mindweave.Application.Conversations.ConversationMarksService>().FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await services.GetRequiredService<SqliteDatabase>().CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                await services.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
