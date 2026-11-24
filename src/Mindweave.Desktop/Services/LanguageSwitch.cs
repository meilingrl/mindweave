using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Languages;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Services;

/// <summary>
/// The session's view layer as a language switch sees it (bilingual-ui.md §6): the view shown, a way to take its root
/// view out of the window, and its replacement by a new view on the same services. The application session implements it.
/// </summary>
public interface IViewSession
{
    /// <summary>The view provider the window shows.</summary>
    ServiceProvider View { get; }

    /// <summary>The window lets go of the current root view, so its pages save what they keep on leaving.</summary>
    void DetachView();

    /// <summary>The window shows the current view again, in a new root view (after a rebuild that failed).</summary>
    void ShowView();

    /// <summary>
    /// Builds a new view provider on the same services, hands it to <paramref name="prepare"/>, then makes it current and
    /// shows it. Returns the old one, which the caller releases and disposes. If building, preparing or showing throws,
    /// the new one's pages are released, it is disposed, and the current one stays.
    /// </summary>
    ServiceProvider ReplaceView(Action<ServiceProvider> prepare);
}

/// <summary>Where nothing can replace the view (services built without the application session): a switch is refused.</summary>
internal sealed class UnavailableViewSession : IViewSession
{
    public ServiceProvider View => throw Unavailable();

    public void DetachView() => throw Unavailable();

    public void ShowView() => throw Unavailable();

    public ServiceProvider ReplaceView(Action<ServiceProvider> prepare) => throw Unavailable();

    private static InvalidOperationException Unavailable() => new("Switching the language needs the application session.");
}

/// <summary>
/// Switches the interface language in place (bilingual-ui.md §6, §7). It lives with the services, not with a view: the
/// view it is called from is the one it replaces. A switch waits until no prompt, exit, backup flow, send or attachment
/// store is under way, saves the choice, then rebuilds the view layer in one go on the UI thread: snapshot, detach the
/// old root view, set the language, build the new view and replay the snapshot through it, show it, and release the old
/// one. Running answers, runs, approvals, summary generation and MCP servers live in the services and go on; the startup
/// path is never run again (it would mark running answers as interrupted).
/// </summary>
/// <remarks>
/// The choice is saved before the rebuild rather than after it (the plan's step 9 moved up): the pages of the new view
/// then know at once whether saving failed and show the notice. A switch that does not happen (the rebuild failed, the
/// wait was too long, the reader left the page they chose on) writes the language in use back, so the stored choice and
/// the screen agree. A switch never ends in an exception: what went wrong is logged and said beside the picker
/// (<see cref="Notice"/>).
/// </remarks>
public sealed partial class LanguageSwitch
{
    /// <summary>How often a waiting switch looks again whether the flows it waits for have ended.</summary>
    internal static TimeSpan SettleInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long a switch waits for those flows before it gives up (review of merge 2, M1).</summary>
    internal static TimeSpan WaitLimit { get; set; } = TimeSpan.FromSeconds(10);

    private readonly IViewSession _session;
    private readonly ILanguageChoice _choice;
    private readonly ILogger<LanguageSwitch> _logger;
    private UiLanguage _wanted;
    private UiLanguage _written;
    private object? _origin;
    private Outcome _outcome;

    public LanguageSwitch(IViewSession session, ILanguageChoice choice, ILogger<LanguageSwitch> logger)
    {
        _session = session;
        _choice = choice;
        _logger = logger;
        _wanted = _written = CurrentLanguage.Value;
    }

    /// <summary>Why the last switch did not happen.</summary>
    private enum Outcome
    {
        None,
        Failed,
        NotNow,
    }

    /// <summary>The waiting, or what came of the last switch, changed: a picker reads <see cref="Notice"/> again.</summary>
    public event EventHandler? Changed;

    /// <summary>The last choice could not be saved: it applies until the application quits.</summary>
    public bool SaveFailed { get; private set; }

    /// <summary>The switch waits for a flow to end before it rebuilds.</summary>
    public bool IsWaiting { get; private set; }

    /// <summary>What a picker says beside it: the switch waits, it did not happen, or its choice was not saved; else null.</summary>
    public string? Notice =>
        IsWaiting ? UiText.LanguageSwitchWaiting
        : _outcome == Outcome.Failed ? UiText.LanguageSwitchFailed
        : _outcome == Outcome.NotNow ? UiText.LanguageSwitchNotNow
        : SaveFailed ? UiText.LanguageSaveFailed
        : null;

    /// <summary>The switch under way, or the last one (tests). It never faults.</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>How many times the view layer was rebuilt (tests).</summary>
    internal int Rebuilds { get; private set; }

    /// <summary>
    /// Switches to <paramref name="target"/>. A call made while a switch is waiting or running joins it: only the last
    /// language asked for is applied, and the view is rebuilt at most once for it. <paramref name="origin"/> is the page
    /// the choice was made on: leaving it while the switch waits drops the switch.
    /// </summary>
    public Task SwitchAsync(UiLanguage target, object? origin = null)
    {
        _wanted = target;
        _origin = origin;
        if (_outcome != Outcome.None)
        {
            _outcome = Outcome.None;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (Running.IsCompleted)
        {
            Running = RunAsync();
        }

        return Running;
    }

    private async Task RunAsync()
    {
        try
        {
            await SwitchesAsync();
        }
#pragma warning disable CA1031 // A switch that fails leaves the language in use and says so; it never ends the application.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSwitchFailed(_logger, ex);
            _outcome = Outcome.Failed;
            await KeepLanguageInUseAsync();
        }
        finally
        {
            IsWaiting = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task SwitchesAsync()
    {
        while (true)
        {
            // Never inside the click or key that chose: the control handling it is about to leave the window.
            await Task.Yield();
            if (!await WhenSettledAsync())
            {
                return;
            }

            var target = _wanted;
            if (target != _written)
            {
                SaveFailed = !await _choice.WriteAsync(target);
                _written = target;

                // Another choice may have come meanwhile; look again before rebuilding.
                continue;
            }

            if (target == CurrentLanguage.Value)
            {
                return;
            }

            await ReleaseAsync(Rebuild(target));
        }
    }

    /// <summary>
    /// Steps 2 to 8 of the switch, with nothing awaited in between, so no other work sees half a view. Returns the old
    /// view for the caller to release. If anything fails, the old language and view come back and the failure is thrown.
    /// </summary>
    private ServiceProvider Rebuild(UiLanguage target)
    {
        var language = CurrentLanguage.Value;
        var snapshot = _session.View.GetRequiredService<AppStartup>().Snapshot();
        try
        {
            _session.DetachView();
            CurrentLanguage.Set(target);
            var previous = _session.ReplaceView(view => view.GetRequiredService<AppStartup>().Resume(snapshot));
            Rebuilds++;
            return previous;
        }
        catch
        {
            CurrentLanguage.Set(language);
            ShowOldView();
            throw;
        }
    }

    /// <summary>The window shows the view it had again; if even that fails, it is logged beside the failure that led here.</summary>
    private void ShowOldView()
    {
        try
        {
            _session.ShowView();
        }
#pragma warning disable CA1031 // The switch's own failure is what is reported; this one is logged with it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogShowFailed(_logger, ex);
        }
    }

    /// <summary>
    /// The old pages leave with their view: released, so none of their timers or reloads runs on, then disposed even if
    /// a page fails to let go. The switch itself is done; a failure here is only logged.
    /// </summary>
    private async Task ReleaseAsync(ServiceProvider previous)
    {
        try
        {
            previous.GetRequiredService<Workbench>().ReleaseAll();
        }
#pragma warning disable CA1031 // The new view is shown; the old one goes either way.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReleaseFailed(_logger, ex);
        }
        finally
        {
            try
            {
                await previous.DisposeAsync();
            }
#pragma warning disable CA1031 // Nothing waits for the old view to be disposed; a failure is only logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogReleaseFailed(_logger, ex);
            }
        }
    }

    /// <summary>
    /// The switch does not happen: the choice goes back to the language in use, written back if another one was written
    /// (best effort: if that fails too, it is logged, and the next choice writes again).
    /// </summary>
    private async Task KeepLanguageInUseAsync()
    {
        var current = CurrentLanguage.Value;
        _wanted = current;
        SaveFailed = false;
        if (_written == current)
        {
            return;
        }

        try
        {
            if (await _choice.WriteAsync(current))
            {
                _written = current;
                return;
            }

            LogWriteBackFailed(_logger, null);
        }
#pragma warning disable CA1031 // Best effort: the language in use stays either way.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWriteBackFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Waits until nothing is open or under way that a rebuild would cut short or that holds the old view. False when the
    /// switch is not to happen after all: the choice went back to the language in use, the reader left the page they
    /// chose on, or the wait passed <see cref="WaitLimit"/>.
    /// </summary>
    private async Task<bool> WhenSettledAsync()
    {
        if (!Busy(_session.View))
        {
            return true;
        }

        var started = Stopwatch.GetTimestamp();
        IsWaiting = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            while (Busy(_session.View))
            {
                if (_wanted == _written && _wanted == CurrentLanguage.Value)
                {
                    return false;
                }

                if (_origin is { } origin && !Shows(_session.View, origin))
                {
                    await KeepLanguageInUseAsync();
                    return false;
                }

                if (Stopwatch.GetElapsedTime(started) >= WaitLimit)
                {
                    LogGaveUp(_logger, WaitLimit);
                    _outcome = Outcome.NotNow;
                    await KeepLanguageInUseAsync();
                    return false;
                }

                await Task.Delay(SettleInterval);
            }

            return true;
        }
        finally
        {
            IsWaiting = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The page a choice was made on is still what the window shows.</summary>
    private static bool Shows(IServiceProvider view, object origin)
    {
        var root = view.GetRequiredService<RootViewModel>();
        return ReferenceEquals(root.Current, origin)
            || (root.Current is ShellViewModel && ReferenceEquals(view.GetRequiredService<Workbench>().CurrentPage, origin));
    }

    private static bool Busy(IServiceProvider view)
    {
        var root = view.GetRequiredService<RootViewModel>();
        if (root.Current is null or StartupViewModel
            || root.Exit.Stage != ExitStage.Idle
            || root.WorkspaceDeletion.IsOpen
            || root.ConversationDeletion.IsOpen
            || root.BackupRestore.IsOpen
            || root.BackupRestore.IsBusy
            || root.BackupDeletion.IsOpen
            || root.BackupDeletion.IsBusy)
        {
            return true;
        }

        // Attachments still being stored would be lost with their page; they take a moment (§6.5). A question being
        // sent finishes on its old page, which then opens the conversation it started (review of merge 2, M2).
        return view.GetRequiredService<Workbench>().AllPages()
            .Select(page => page switch
            {
                ConversationViewModel conversation => conversation.Composer,
                NewConversationViewModel fresh => fresh.Composer,
                _ => null,
            })
            .Any(composer => composer is not null && (composer.IsStoringAttachments || composer.SendCommand.IsRunning));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The language switch failed; the language in use stays")]
    private static partial void LogSwitchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Showing the view again after a failed language switch failed")]
    private static partial void LogShowFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Releasing the view a language switch replaced failed")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing back the language in use after a switch that did not happen failed")]
    private static partial void LogWriteBackFailed(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "A language switch waited {Limit} and gave up")]
    private static partial void LogGaveUp(ILogger logger, TimeSpan limit);
}
