using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Views.Conversations;

namespace Mindweave.Desktop.Tests;

public partial class ConversationActionsTests
{
    /// <summary>
    /// Windows full runs (2026-11-03): clicking copy or retry under a stopped answer occasionally timed out. A stopped
    /// answer shows "stopped" in its live row before the terminal write; the page swaps in the stored row only after the
    /// write and a reload. On a slow disk that reload can land inside the click (headless input first runs pending
    /// dispatcher jobs), so a button taken from the live row is detached before the press and the wait on it never ends.
    /// Here the reload is held and released at the click, which makes that ordering certain.
    /// </summary>
    [AvaloniaFact]
    public async Task A_click_on_the_live_row_is_lost_when_the_stored_row_replaces_it_so_waits_are_for_the_stored_row()
    {
        var reload = new HeldReload();
        await using var app = DesktopHarness.Create(s => s.AddSingleton<IConversations>(sp => reload.Wrap(sp.GetRequiredService<ConversationService>())));
        var window = Open(app);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold("半句");
        var page = await AskAsync(app, window, "问");
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UiWait.UntilAsync(() => page.Messages.Count == 2 && page.Messages[1].Body == "半句");

        reload.Hold();
        page.StopCommand.Execute(null);
        await UiWait.UntilAsync(() => reload.IsHolding);

        // The live row already says "stopped"; the stored row is not there yet, and the stored wait does not pass.
        await UiWait.UntilAsync(() => page.Messages[1].State == MessageState.Stopped);
        Assert.True(page.Messages[1].IsLive);
        var stored = UiWait.StoredAsync(page, 1, MessageState.Stopped);
        Assert.False(stored.IsCompleted);

        // The old pattern: take the copy button from the live row and aim at it; the reload lands as the press starts
        // (the headless press runs pending dispatcher jobs before it delivers the input).
        window.UpdateLayout();
        var view = CurrentView(window);
        var liveCopy = VisibleNamed<CopyButton>(view, "AnswerCopy");
        liveCopy.BringIntoView();
        window.UpdateLayout();
        var centre = liveCopy.TranslatePoint(new Point(liveCopy.Bounds.Width / 2, liveCopy.Bounds.Height / 2), window)!.Value;
        reload.Release();
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        await stored;
        Assert.False(liveCopy.ShowsCopied);
        Assert.Null(TopLevel.GetTopLevel(liveCopy));

        // The shared click measures after pending work runs, so it reports a control that is gone instead of missing.
        Assert.Throws<InvalidOperationException>(() => Click(window, liveCopy));

        // After the stored wait the row is the one on screen, and its copy confirms.
        window.UpdateLayout();
        var copy = VisibleNamed<CopyButton>(view, "AnswerCopy");
        Assert.NotSame(liveCopy, copy);
        Click(window, copy);
        await UiWait.UntilAsync(() => copy.ShowsCopied);
        window.Close();
    }

    /// <summary>
    /// Holds the page's reload just before it applies: the reload's last read (<c>PlanRestoreAsync</c>) completes, then
    /// waits, so releasing it on the UI thread queues the step that swaps the rows as one dispatcher job. Only a reload
    /// that read the answer as finished is held.
    /// </summary>
    private sealed class HeldReload
    {
        private readonly TaskCompletionSource _gate = new();
        private volatile bool _holding;
        private volatile bool _armed;
        private volatile bool _sawFinished;

        public bool IsHolding => _holding;

        public void Hold() => _armed = true;

        public void Release()
        {
            _armed = false;
            _gate.SetResult();
        }

        public IConversations Wrap(IConversations inner)
        {
            var proxy = DispatchProxy.Create<IConversations, Proxy>();
            ((Proxy)(object)proxy).Init(inner, this);
            return proxy;
        }

        private async Task<ConversationContent?> Load(Task<ConversationContent?> load)
        {
            var content = await load.ConfigureAwait(false);
            _sawFinished = content?.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant)?.IsFinished == true;
            return content;
        }

        private async Task<RestorePlan> Plan(Task<RestorePlan> plan)
        {
            var result = await plan.ConfigureAwait(false);
            if (_armed && _sawFinished)
            {
                _holding = true;

                // Continues synchronously on the releasing (UI) thread; the page's own await then posts one job.
                await _gate.Task.ConfigureAwait(false);
            }

            return result;
        }

        // DispatchProxy needs a public, unsealed type with a parameterless constructor.
#pragma warning disable CA1812, CA1852
        public class Proxy : DispatchProxy
#pragma warning restore CA1812, CA1852
        {
            private IConversations _inner = null!;
            private HeldReload _owner = null!;

            internal void Init(IConversations inner, HeldReload owner)
            {
                _inner = inner;
                _owner = owner;
            }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                object? result;
                try
                {
                    result = targetMethod!.Invoke(_inner, args);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    ExceptionDispatchInfo.Throw(ex.InnerException);
                    throw;
                }

                return targetMethod.Name switch
                {
                    nameof(IConversations.LoadAsync) => _owner.Load((Task<ConversationContent?>)result!),
                    nameof(IConversations.PlanRestoreAsync) => _owner.Plan((Task<RestorePlan>)result!),
                    _ => result,
                };
            }
        }
    }
}
