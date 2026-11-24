using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Views;

/// <summary>The share dialog (docs/design/sharing.md §6.3). When it opens, the conversation it was opened on is in sight.</summary>
public sealed partial class ShareView : UserControl
{
    /// <summary>A dot's box is 40 wide with the dot in the middle of its bottom 40; its tick and title stand above the line.</summary>
    internal const double DotBox = 40;

    internal const double BoxHeight = 46;

    private ShareViewModel? _share;

    public ShareView() => InitializeComponent();

    public static FuncValueConverter<double, double> DotLeft { get; } = new(x => x - (DotBox / 2));

    public static FuncValueConverter<double, double> DotTop { get; } = new(y => y - BoxHeight + (DotBox / 2));

    public static FuncValueConverter<double, double> DotSize { get; } = new(radius => 2 * radius);

    public static FuncValueConverter<double, double> RingSize { get; } = new(radius => 2 * (radius + SummaryNetworkLayout.SummaryRingGap));

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Follow(DataContext as ShareViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Follow(null);
    }

    private void Follow(ShareViewModel? share)
    {
        if (_share is not null)
        {
            _share.Opened -= OnOpened;
        }

        _share = share;
        if (_share is not null)
        {
            _share.Opened += OnOpened;
        }
    }

    /// <summary>
    /// After the dots are laid out: the exploration the dialog was opened on is brought into the picture, or, when it is
    /// larger than that, the conversation itself.
    /// </summary>
    private void OnOpened(object? sender, EventArgs e) => Dispatcher.UIThread.Post(
        () =>
        {
            ShareScroller.Offset = default;
            var ticked = _share?.Dots.Where(d => d.IsTicked).ToList() ?? [];
            if (_share?.OpenedOn is not { } opened || ticked.Count == 0)
            {
                return; // Closed, or everything unticked, before the dots were laid out.
            }

            var view = ShareDotsScroller.Viewport;
            var (left, right) = (ticked.Min(d => d.X), ticked.Max(d => d.X));
            var (top, bottom) = (ticked.Min(d => d.Y), ticked.Max(d => d.Y));
            ShareDotsScroller.Offset = new Vector(
                Start(right - left + TitleRoom <= view.Width ? left - (view.Width / 8) : opened.X - (view.Width / 3)),
                Start(bottom - top + BoxHeight <= view.Height ? ((top + bottom - view.Height) / 2) : opened.Y - (view.Height / 2)));
        },
        DispatcherPriority.Loaded);

    /// <summary>About what a title takes to the right of its dot.</summary>
    private const double TitleRoom = 160;

    private static double Start(double offset) => Math.Max(0, offset);
}
