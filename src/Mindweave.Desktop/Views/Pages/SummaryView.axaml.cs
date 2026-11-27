using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// Renders the shown version with the same Markdown pipeline as answers, and reports how far its body has been in
/// view. The position is the scroll offset, saved per version; summaries are not anchored to like answers are.
/// </summary>
public sealed partial class SummaryView : UserControl
{
    private readonly MarkdownRenderer _renderer = new();
    private readonly DispatcherTimer _report;
    private SummaryViewModel? _page;
    private string? _restoreLocator;

    public SummaryView()
    {
        InitializeComponent();
        BodyHost.Content = _renderer;
        AttachTextColumn();
        _renderer.LinkClick += (_, e) => e.Handled = true;
        _report = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => Report());
        Scroller.ScrollChanged += (_, _) => _report.Start();
        Scroller.LayoutUpdated += (_, _) => RestoreOnce();

        // The export options close when the export starts; the save dialog takes over from there.
        ExportRunButton.Click += (_, _) => ExportButton.Flyout?.Hide();
    }

    internal MarkdownRenderer Renderer => _renderer;

    /// <summary>How much of the body has been in view, 0–1 (1 when it fits on one screen).</summary>
    internal double Progress =>
        Scroller.Extent.Height <= 0 ? 0 : Math.Clamp((Scroller.Offset.Y + Scroller.Viewport.Height) / Scroller.Extent.Height, 0, 1);

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPageChanged;
        }

        _page = DataContext as SummaryViewModel;
        FollowTextWidth(_page?.TextWidth);
        if (_page is not null)
        {
            _page.PropertyChanged += OnPageChanged;
            Render();
        }

        base.OnDataContextChanged(e);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Report();
        _report.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SummaryViewModel.Shown))
        {
            Render();
        }
        else if (e.PropertyName == nameof(SummaryViewModel.IsEditing) && _page is { IsEditing: false })
        {
            _report.Start();
        }
    }

    private void Render()
    {
        if (_page?.Shown is not { } version)
        {
            _renderer.DocumentUpdate = null;
            return;
        }

        _renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(AnswerMarkdown.Parse(version.Body));
        _restoreLocator = _page.ShownLocator;
        Scroller.Offset = default;

        // A short body is fully in view at once; report it so the minimum visible time can start counting.
        _report.Start();
    }

    private void RestoreOnce()
    {
        if (_restoreLocator is { } locator && Scroller.Extent.Height > Scroller.Viewport.Height &&
            double.TryParse(locator, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            _restoreLocator = null;
            Scroller.Offset = new Vector(0, Math.Min(y, Scroller.Extent.Height - Scroller.Viewport.Height));
        }
    }

    private void Report()
    {
        _report.Stop();
        if (_page is { IsEditing: false, Shown: not null } page && IsVisible)
        {
            _ = page.ReportProgressAsync(Progress, Scroller.Offset.Y.ToString("0.#", CultureInfo.InvariantCulture));
        }
    }
}
