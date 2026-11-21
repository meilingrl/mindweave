using Microsoft.Extensions.DependencyInjection;
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
    /// shows it. Returns the old one, which the caller releases and disposes. If building or preparing throws, the new
    /// one is disposed and the current one stays.
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
/// view it is called from is the one it replaces. A switch waits until no prompt, exit, backup flow or attachment store
/// is under way, saves the choice, then rebuilds the view layer in one go on the UI thread: snapshot, detach the old
/// root view, set the language, build the new view and replay the snapshot through it, show it, and release the old one.
/// Running answers, runs, approvals, summary generation and MCP servers live in the services and go on; the startup path
/// is never run again (it would mark running answers as interrupted).
/// </summary>
/// <remarks>
/// The choice is saved before the rebuild rather than after it (the plan's step 9 moved up): the pages of the new view
/// then know at once whether saving failed and show the notice, without listening to this service.
/// </remarks>
public sealed class LanguageSwitch
{
    /// <summary>How often a waiting switch looks again whether the flows it waits for have ended.</summary>
    internal static TimeSpan SettleInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    private readonly IViewSession _session;
    private readonly ILanguageChoice _choice;
    private UiLanguage _wanted;
    private UiLanguage _written;

    public LanguageSwitch(IViewSession session, ILanguageChoice choice)
    {
        _session = session;
        _choice = choice;
        _wanted = _written = CurrentLanguage.Value;
    }

    /// <summary>The last choice could not be saved: it applies until the application quits.</summary>
    public bool SaveFailed { get; private set; }

    /// <summary>The switch under way, or the last one (tests).</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>How many times the view layer was rebuilt (tests).</summary>
    internal int Rebuilds { get; private set; }

    /// <summary>
    /// Switches to <paramref name="target"/>. A call made while a switch is waiting or running joins it: only the last
    /// language asked for is applied, and the view is rebuilt at most once for it.
    /// </summary>
    public Task SwitchAsync(UiLanguage target)
    {
        _wanted = target;
        if (Running.IsCompleted)
        {
            Running = RunAsync();
        }

        return Running;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            // Never inside the click or key that chose: the control handling it is about to leave the window.
            await Task.Yield();
            await WhenSettledAsync();
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

            var previous = Rebuild(target);
            await previous.DisposeAsync();
        }
    }

    /// <summary>
    /// Steps 2 to 8 of the switch, with nothing awaited in between, so no other work sees half a view. Returns the old
    /// view, released, for the caller to dispose. If the new view cannot be built, the old language and view come back.
    /// </summary>
    private ServiceProvider Rebuild(UiLanguage target)
    {
        var old = _session.View;
        var language = CurrentLanguage.Value;
        var snapshot = old.GetRequiredService<AppStartup>().Snapshot();
        _session.DetachView();
        CurrentLanguage.Set(target);
        ServiceProvider previous;
        try
        {
            previous = _session.ReplaceView(view => view.GetRequiredService<AppStartup>().Resume(snapshot));
        }
        catch
        {
            CurrentLanguage.Set(language);
            _session.ShowView();
            throw;
        }

        Rebuilds++;

        // The old pages leave with their view: disposed, so none of their timers or reloads runs on.
        previous.GetRequiredService<Workbench>().ReleaseAll();
        return previous;
    }

    /// <summary>Waits until nothing is open or under way that a rebuild would cut short or that holds the old view.</summary>
    private async Task WhenSettledAsync()
    {
        while (Busy(_session.View))
        {
            await Task.Delay(SettleInterval);
        }
    }

    private static bool Busy(IServiceProvider view)
    {
        var root = view.GetRequiredService<RootViewModel>();
        if (root.Current is null or StartupViewModel
            || root.Exit.Stage != ExitStage.Idle
            || root.LayoutPrompt.IsOpen
            || root.WorkspaceDeletion.IsOpen
            || root.ConversationDeletion.IsOpen
            || root.BackupRestore.IsOpen
            || root.BackupRestore.IsBusy
            || root.BackupDeletion.IsOpen
            || root.BackupDeletion.IsBusy)
        {
            return true;
        }

        // Attachments still being stored would be lost with their page; they take a moment (§6.5).
        return view.GetRequiredService<Workbench>().AllPages()
            .Select(page => page switch
            {
                ConversationViewModel conversation => conversation.Composer,
                NewConversationViewModel fresh => fresh.Composer,
                _ => null,
            })
            .Any(composer => composer?.IsStoringAttachments == true);
    }
}
