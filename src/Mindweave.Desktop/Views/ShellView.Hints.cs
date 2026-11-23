using System.ComponentModel;
using Avalonia;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Views;

// The new-answer hints and the page under them (the owner, 2026-11-23). On a conversation they float above the input, over the
// text. The settings' controls stand at the right edge, where a hint would lie on them and take a press meant for a switch:
// there the hints stand at the bottom edge, and the page ends above them while any shows.
public sealed partial class ShellView
{
    /// <summary>Space between the page and a hint below it.</summary>
    private const double HintGap = 12;

    /// <summary>However many hints show, the page keeps this much.</summary>
    private const double MinPageHeight = 120;

    private Thickness _pageMargin;
    private Thickness _hintMargin;
    private ShellViewModel? _watched;

    private void AttachHints()
    {
        _pageMargin = PageHost.Margin;
        _hintMargin = NewsToasts.Margin;
        NewsToasts.SizeChanged += (_, _) => ArrangeHints();
        ContentArea.SizeChanged += (_, _) => ArrangeHints();
        DataContextChanged += (_, _) => WatchSection(ViewModel);
        AttachedToVisualTree += (_, _) => WatchSection(ViewModel);
        DetachedFromVisualTree += (_, _) => WatchSection(null);
    }

    private void WatchSection(ShellViewModel? shell)
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnShellChanged;
        }

        _watched = shell;
        if (shell is not null)
        {
            shell.PropertyChanged += OnShellChanged;
        }

        ArrangeHints();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsSettings))
        {
            ArrangeHints();
        }
    }

    private void ArrangeHints()
    {
        var inSettings = _watched?.IsSettings == true;
        NewsToasts.Margin = inSettings ? new Thickness(_hintMargin.Left, _hintMargin.Top, _hintMargin.Right, _pageMargin.Bottom) : _hintMargin;
        var height = NewsToasts.Bounds.Height;
        var room = inSettings && height > 0 ? height + HintGap : 0;

        // What the page could have without any hint is what is left of it now and what it already gave up.
        var available = PageHost.Bounds.Height + PageHost.Margin.Bottom - _pageMargin.Bottom;
        if (available > 0)
        {
            room = Math.Min(room, Math.Max(0, available - MinPageHeight));
        }

        PageHost.Margin = new Thickness(_pageMargin.Left, _pageMargin.Top, _pageMargin.Right, _pageMargin.Bottom + room);
    }
}
