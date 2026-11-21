using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public SingleInstance Instance { get; } = instance;

    public AppSession Session { get; } = session;

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
            var (root, view) = (Session.Root, Session.View);
            var wait = view.GetRequiredService<ExitCoordinator>().Stage == ExitStage.Releasing
                ? TimeSpan.Zero
                : root.GetRequiredService<ExitOptions>().StopWait;
            await AppSession.EndAsync(root, view, wait).ConfigureAwait(false);
        }
        finally
        {
            await Instance.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The services of the open database, for as long as the window and the single-instance lock live. They are in two
/// providers (bilingual-ui.md §6.2): the root with every service, and the view on it with the window's view models.
/// <see cref="ReplaceView"/> replaces only the view, so running work goes on. Restoring a backup from inside the
/// application (2026-11-12) ends both and starts new ones on the same window and lock: every service, page and cache
/// then starts from the restored data exactly as after a fresh start, which resetting the state held by some twenty
/// singletons one by one could not promise.
/// </summary>
public sealed class AppSession : IInAppRestore
{
    private readonly Func<Action<IServiceCollection>, ServiceProvider> _buildRoot;
    private readonly Func<IServiceProvider, ServiceProvider> _buildView;

    /// <param name="buildRoot">Builds one session's root provider; the registrations it is given must be applied (they make this session reachable).</param>
    /// <param name="buildView">Builds a view provider on a root provider.</param>
    public AppSession(Func<Action<IServiceCollection>, ServiceProvider> buildRoot, Func<IServiceProvider, ServiceProvider> buildView)
    {
        ArgumentNullException.ThrowIfNull(buildRoot);
        ArgumentNullException.ThrowIfNull(buildView);
        _buildRoot = buildRoot;
        _buildView = buildView;
        (Root, View) = Build();
    }

    /// <summary>The root provider: every service. Replaced only by a restore.</summary>
    public ServiceProvider Root { get; private set; }

    /// <summary>The view provider the window shows. Replaced by <see cref="ReplaceView"/> and by a restore.</summary>
    public ServiceProvider View { get; private set; }

    public RootViewModel RootViewModel => View.GetRequiredService<RootViewModel>();

    /// <summary>A new view provider took over (after a restore, on a new root too): the window shows <see cref="RootViewModel"/> from now on.</summary>
    public event EventHandler? ViewReplaced;

    /// <summary>The last restore's hand-over and start, for tests; the UI never awaits it.</summary>
    internal Task LastRestore { get; private set; } = Task.CompletedTask;

    /// <summary>A logger that writes through the current root each time, so it outlives a restore.</summary>
    internal ILogger Logger(string category) => new SessionLogger(this, category);

    /// <summary>
    /// Builds a new view provider on the same root, makes it current and raises <see cref="ViewReplaced"/>. Returns the
    /// old one: the caller disposes it once the new one is shown. Nothing of the old view is carried over.
    /// </summary>
    internal ServiceProvider ReplaceView()
    {
        var old = View;
        View = _buildView(Root);
        ViewReplaced?.Invoke(this, EventArgs.Empty);
        return old;
    }

    /// <summary>
    /// Called by the restore once the old session has stopped its work and saved everything, and the current data is in
    /// the verified backup <paramref name="keptCopy"/>. The window moves to new services showing the start page, the old
    /// ones end (their database closes), then the new ones put the backup in place and start as usual.
    /// </summary>
    public Task RestoreAsync(BackupInfo backup, string keptCopy)
    {
        ArgumentNullException.ThrowIfNull(backup);
        LastRestore = RunAsync(backup, keptCopy);
        return LastRestore;
    }

    private async Task RunAsync(BackupInfo backup, string keptCopy)
    {
        var (oldRoot, oldView) = (Root, View);
        var (nextRoot, nextView) = Build();
        nextView.GetRequiredService<IRootNavigator>().Show(new StartupViewModel());
        (Root, View) = (nextRoot, nextView);
        ViewReplaced?.Invoke(this, EventArgs.Empty);

        // The old pages leave with their session: disposed, so none of their timers or reloads reaches the closed database.
        oldView.GetRequiredService<Workbench>().ReleaseAll();

        // Its generation was stopped by the restore's exit steps; nothing waits for it again. A failure while it ends is
        // logged and the restore goes on (low finding architecture-3): the window already shows the new session, and the
        // restore itself checks the files and puts the previous data back if it cannot use the copy.
        try
        {
            await EndAsync(oldRoot, oldView, TimeSpan.Zero);
        }
#pragma warning disable CA1031 // The old session is gone either way; the restore must not be left on a spinner.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            nextRoot.GetRequiredService<ILoggerFactory>().CreateLogger("Mindweave.Restore").LogOldSessionEndFailed(ex);
        }

        await nextView.GetRequiredService<AppStartup>().RestoreInPlaceAsync(backup, keptCopy);
    }

    private (ServiceProvider Root, ServiceProvider View) Build()
    {
        var root = _buildRoot(services => services.AddSingleton<IInAppRestore>(this));
        try
        {
            return (root, _buildView(root));
        }
        catch
        {
            root.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>
    /// Ends one session's services: stops generation (bounded by <paramref name="wait"/>), stops storage recovery, flushes
    /// kept writes and page state (all idempotent, normally nothing is left), then closes the database and disposes the
    /// view provider and the root provider.
    /// </summary>
    internal static async Task EndAsync(ServiceProvider root, ServiceProvider view, TimeSpan wait)
    {
        try
        {
            // Stop first, so terminal states are written before the database closes.
            await root.GetRequiredService<Mindweave.Application.Conversations.GenerationCoordinator>()
                .ShutdownAsync(wait).ConfigureAwait(false);

            // Recovery stops (and a probe or retry in progress finishes) before the database closes; then every
            // owner tries what it kept once more, while the database is still open (architecture review R3).
            var health = root.GetRequiredService<Mindweave.Application.Storage.StorageHealth>();
            await health.StopAsync().ConfigureAwait(false);
            await health.RetryKeptAsync().ConfigureAwait(false);
            await root.GetRequiredService<Mindweave.Application.Reading.ReadingStateService>().FlushAsync().ConfigureAwait(false);
            await root.GetRequiredService<Mindweave.Application.Conversations.ConversationMarksService>().FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await root.GetRequiredService<SqliteDatabase>().CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await view.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await root.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Logs through whichever root is current; between two sessions, or after the last one, nothing is logged.</summary>
    private sealed class SessionLogger(AppSession session, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => Current()?.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => Current()?.IsEnabled(logLevel) ?? false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Current()?.Log(logLevel, eventId, state, exception, formatter);

        private ILogger? Current()
        {
            try
            {
                return session.Root.GetRequiredService<ILoggerFactory>().CreateLogger(category);
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }
}

internal static partial class AppHostLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Ending the previous session before a restore failed; the restore goes on")]
    public static partial void LogOldSessionEndFailed(this ILogger logger, Exception exception);
}
