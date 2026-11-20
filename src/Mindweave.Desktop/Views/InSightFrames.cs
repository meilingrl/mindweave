using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Mindweave.Desktop.Views;

/// <summary>
/// Animation frames for a small looping mark (the sidebar's spinner, the weaving indicator), delivered only while the
/// mark is in sight: in a window that is not minimised, effectively visible and at least partly inside the viewport its
/// scrolling ancestors leave it. Nothing runs while it is hidden, collapsed away, scrolled out, minimised or detached;
/// frames resume as soon as it is in sight again. The owner forwards its attach, detach and size events.
/// </summary>
internal sealed class InSightFrames(Control owner, Func<bool> animates, Action<TimeSpan> onFrame)
{
    private readonly List<IDisposable> _watches = [];
    private TopLevel? _topLevel;
    private Window? _window;
    private Rect? _viewport;
    private bool _framePending;

    /// <summary>In sight now: frames are asked for while this holds (and animation is on).</summary>
    public bool IsInSight =>
        _topLevel is not null && owner.IsEffectivelyVisible && IsInViewport && _window?.WindowState != WindowState.Minimized;

    /// <summary>Frames delivered, in sight or not; tests check that none arrive while the mark is out of sight.</summary>
    public int Received { get; private set; }

    /// <summary>Not yet told otherwise, or some of it lies inside the effective viewport.</summary>
    private bool IsInViewport => _viewport is not { } viewport || viewport.Intersects(new Rect(owner.Bounds.Size));

    public void Attached()
    {
        _topLevel = TopLevel.GetTopLevel(owner);

        // Effective visibility changes when the mark or any ancestor is shown or hidden: each of them is watched while
        // attached, so frames resume as soon as it shows again.
        foreach (var visual in owner.GetSelfAndVisualAncestors())
        {
            _watches.Add(visual.GetPropertyChangedObservable(Visual.IsVisibleProperty).Subscribe(new Watch(this)));
        }

        owner.EffectiveViewportChanged += OnEffectiveViewportChanged;
        _window = _topLevel as Window;
        if (_window is not null)
        {
            _watches.Add(_window.GetPropertyChangedObservable(Window.WindowStateProperty).Subscribe(new Watch(this)));
        }

        Request();
    }

    public void Detached()
    {
        foreach (var watch in _watches)
        {
            watch.Dispose();
        }

        _watches.Clear();
        owner.EffectiveViewportChanged -= OnEffectiveViewportChanged;
        _viewport = null;
        _window = null;
        _topLevel = null;
    }

    /// <summary>Asks for the next frame if the mark is in sight and none is pending.</summary>
    public void Request()
    {
        if (_framePending || !animates() || !IsInSight)
        {
            return;
        }

        _framePending = true;
        _topLevel!.RequestAnimationFrame(OnFrame);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport;
        Request();
    }

    private void OnFrame(TimeSpan time)
    {
        _framePending = false;
        Received++;
        if (!animates() || !IsInSight)
        {
            // Hidden or gone: no further frames until it is in sight again.
            return;
        }

        onFrame(time);
        Request();
    }

    private sealed class Watch(InSightFrames frames) : IObserver<AvaloniaPropertyChangedEventArgs>
    {
        // Posted: effective visibility is worked out after the change is announced.
        public void OnNext(AvaloniaPropertyChangedEventArgs value) => Dispatcher.UIThread.Post(frames.Request);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
