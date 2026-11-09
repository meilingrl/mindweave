using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Desktop.ViewModels;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;
using static Mindweave.Desktop.Tests.ExitScenario;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The host around the exit (architecture §12.3): a bounded stop, a step that throws, a second launch, and the database
/// and single-instance lock released only after the UI loop has returned, even when something fails.
/// </summary>
public class AppHostLifecycleTests
{
    [AvaloniaFact]
    public async Task A_step_that_throws_while_stopping_shows_the_failure_instead_of_hanging()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        var generation = app.Services.GetRequiredService<GenerationCoordinator>();
        void Throwing(object? sender, EventArgs e) => throw new InvalidOperationException("simulated listener failure");
        generation.Changed += Throwing;
        try
        {
            window.Close();
            await app.Root.Exit.Running;
        }
        finally
        {
            generation.Changed -= Throwing;
        }

        Assert.Equal(ExitStage.Failed, app.Root.Exit.Stage);
        Assert.Equal(0, app.Shell.ExitRequests);

        app.Root.Exit.RetryCommand.Execute(null);
        await app.Root.Exit.Running;
        Assert.Equal(1, app.Shell.ExitRequests);
    }

    [AvaloniaFact]
    public async Task A_second_launch_during_the_final_release_does_not_touch_the_closed_window()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");

        window.Close();
        await app.Root.Exit.Running;
        Assert.Equal(ExitStage.Releasing, app.Root.Exit.Stage);
        window.Close();

        window.BringToFront();
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task The_host_does_not_wait_again_for_a_task_the_exit_already_gave_up_on()
    {
        var stubborn = new StubbornClient();
        await using var app = DesktopHarness.Create(s =>
        {
            s.AddSingleton<IChatClient>(stubborn);
            s.AddSingleton(new ExitOptions { StopWait = TimeSpan.FromSeconds(2) });
        });
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        await app.Services.GetRequiredService<ConversationService>().StartAsync(app.Workbench.CurrentWorkspace!.Id, "问", CancellationToken.None);
        await stubborn.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(app.Root.Exit.InterceptClose());
        app.Root.Exit.StopAndExitCommand.Execute(null);
        await app.Root.Exit.Running.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(ExitStage.Releasing, app.Root.Exit.Stage);

        var instance = SingleInstance.TryAcquire(app.Paths);
        Assert.NotNull(instance);
        var host = app.HandOverToHost(instance);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await host.DisposeAsync();
        }
        finally
        {
            watch.Stop();
            stubborn.Release.TrySetResult();
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"The host waited {watch.Elapsed}.");
    }

    /// <summary>
    /// TRACK 2026-11-02: a command flips its flag and then refreshes the page. On macOS the lifetime still ran one queued UI
    /// job after its Exit event in about half of the exits (probe, 2026-11-05), so the host closed the database too early when
    /// it did so in that event. Here that last job is such a command; it must find the database open, which closes once the
    /// UI loop has returned.
    /// </summary>
    [AvaloniaFact]
    public async Task The_database_closes_only_after_the_ui_loop_so_its_last_jobs_still_find_it_open()
    {
        await using var app = DesktopHarness.Create();
        var page = await app.StartWithConversationAsync(waitForTitle: false);
        await UiWait.UntilAsync(() => page.RunningAnswer is null);
        Assert.True(app.Root.Exit.InterceptClose());
        await app.Root.Exit.Running;
        Assert.Equal(ExitStage.Releasing, app.Root.Exit.Stage);
        var database = app.Services.GetRequiredService<SqliteDatabase>();

        var instance = SingleInstance.TryAcquire(app.Paths);
        Assert.NotNull(instance);
        var host = app.HandOverToHost(instance);
        Task? archiving = null;
        var openDuringLoop = false;
        var code = host.Run(() =>
        {
            // The lifetime has raised Exit; one queued job still runs: archive (the flag) and then the page refresh.
            openDuringLoop = database.IsOpen;
            archiving = page.ArchiveCommand.ExecuteAsync(null);
            PumpUntil(archiving);
            return 7;
        });

        Assert.Equal(7, code);
        Assert.True(openDuringLoop);
        Assert.NotNull(archiving);
        await archiving;
        Assert.True(page.IsArchived);
        Assert.False(database.IsOpen);
        await using var again = SingleInstance.TryAcquire(app.Paths);
        Assert.NotNull(again);
    }

    /// <summary>A UI loop that ends by throwing still has the database closed and the single-instance lock released.</summary>
    [AvaloniaFact]
    public async Task A_ui_loop_that_throws_still_closes_the_database_and_releases_the_lock()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithConversationAsync(waitForTitle: false);
        var database = app.Services.GetRequiredService<SqliteDatabase>();
        var instance = SingleInstance.TryAcquire(app.Paths);
        Assert.NotNull(instance);
        var host = app.HandOverToHost(instance);

        var thrown = Assert.Throws<InvalidOperationException>(() => host.Run(() => throw new InvalidOperationException("loop failed")));

        Assert.Equal("loop failed", thrown.Message);
        Assert.False(database.IsOpen);
        await using var again = SingleInstance.TryAcquire(app.Paths);
        Assert.NotNull(again);
    }

    /// <summary>Runs UI jobs synchronously until <paramref name="task"/> ends, as the lifetime's loop would before returning.</summary>
    private static void PumpUntil(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    [AvaloniaFact]
    public async Task The_stop_step_waits_a_bounded_time_for_a_task_that_does_not_acknowledge()
    {
        var stubborn = new StubbornClient();
        await using var app = DesktopHarness.Create(s =>
        {
            s.AddSingleton<IChatClient>(stubborn);
            s.AddSingleton(new ExitOptions { StopWait = TimeSpan.FromMilliseconds(300) });
        });
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        await app.Services.GetRequiredService<ConversationService>().StartAsync(app.Workbench.CurrentWorkspace!.Id, "问", CancellationToken.None);
        await stubborn.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(app.Root.Exit.InterceptClose());
        app.Root.Exit.StopAndExitCommand.Execute(null);
        await app.Root.Exit.Running.WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(1, app.Shell.ExitRequests);
        stubborn.Release.TrySetResult();
    }

    [Fact]
    public async Task The_host_releases_the_single_instance_lock_even_when_disposal_fails()
    {
        var paths = new AppPaths("test", Path.Combine(Path.GetTempPath(), "mindweave-desktop-tests", Guid.NewGuid().ToString("N")));
        var instance = SingleInstance.TryAcquire(paths);
        Assert.NotNull(instance);
        var services = CompositionRoot.Build(paths, new DesktopHarness.RecordingShell(), NullLoggerProvider.Instance, s =>
        {
            s.AddSingleton<IChatClient>(_ => new FailingDisposal());
            s.AddSingleton<Application.Credentials.ICredentialStore>(new MemoryCredentialStore());
        });
        var host = new AppHost(instance, services);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DisposeAsync().AsTask());

        await using var again = SingleInstance.TryAcquire(paths);
        Assert.NotNull(again);
    }

    /// <summary>A client whose disposal fails, standing in for any resource that throws while the host shuts down.</summary>
    private sealed class FailingDisposal : IChatClient, IAsyncDisposable
    {
        public IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => throw new InvalidOperationException("simulated disposal failure");
    }
}
