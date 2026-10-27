using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Desktop.Markdown;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Views;

public partial class SummaryView : UserControl
{
    private readonly ScrollViewer scroll;
    private readonly MarkdownMessageView markdown;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly long start = Stopwatch.GetTimestamp();
    private SummaryViewModel? page;
    private Window? window;
    private Guid? readingVersion;
    private long epoch, lifecycle;
    private bool observing;
    public Func<TimeSpan> Clock { get; set; }
    public bool AutomaticObservation { get; set; } = true;
    public SummaryView()
    {
        Clock = () => Stopwatch.GetElapsedTime(start);
        AvaloniaXamlLoader.Load(this); scroll = this.FindControl<ScrollViewer>("SummaryScroll")!; markdown = this.FindControl<MarkdownMessageView>("SummaryMarkdown")!;
        timer.Tick += Tick;
        scroll.ScrollChanged += Tick;
        DataContextChanged += (_, _) => Attach();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); window = TopLevel.GetTopLevel(this) as Window; if (window is not null) { window.Activated += Tick; window.Deactivated += Tick; } Attach(); timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop(); lifecycle++;
        if (window is not null) { window.Activated -= Tick; window.Deactivated -= Tick; } window = null;
        if (page is not null) { page.PropertyChanged -= PageChanged; page.CaptureReadingRequested -= CaptureForNavigationAsync; _ = InterruptAsync(page, readingVersion, epoch); }
        page = null; readingVersion = null; base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == IsVisibleProperty && page is not null) Tick(this, EventArgs.Empty); }
    private void Attach()
    {
        if (page == DataContext) return;
        lifecycle++; if (page is not null) { page.PropertyChanged -= PageChanged; page.CaptureReadingRequested -= CaptureForNavigationAsync; _ = InterruptAsync(page, readingVersion, epoch); }
        page = DataContext as SummaryViewModel; readingVersion = null;
        if (page is not null) { page.PropertyChanged += PageChanged; page.CaptureReadingRequested += CaptureForNavigationAsync; }
    }
    private void PageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SummaryViewModel.SavedBody)) { readingVersion = null; lifecycle++; }
        if (e.PropertyName == nameof(SummaryViewModel.IsEditing) && page is { IsEditing: true }) _ = InterruptAsync(page, readingVersion, epoch);
    }
    private async Task InterruptAsync(SummaryViewModel target, Guid? id, long observedEpoch)
    { if (id is { } version) await target.ObserveReadingAsync(version, observedEpoch, Clock(), false, false, false, [], null); }
    private async Task CaptureForNavigationAsync()
    {
        if (page is not { } target || target.IsEditing || target.IsLoading || readingVersion is not { } id ||
            target.Version?.Header.Identity.Id != id || TopLevel.GetTopLevel(this) is null) return;
        scroll.UpdateLayout(); var measured = SummaryReadingAdapter.Measure(markdown.Renderer, scroll);
        if (measured is not null)
            await target.ObserveReadingAsync(id, epoch, Clock(), false, false, false, [], measured.Location, captureOnly: true);
    }
    private async void Tick(object? sender, EventArgs args)
    { if (AutomaticObservation) await ObserveNowAsync(); }
    public async Task ObserveNowAsync(bool? activeOverride = null)
    {
        if (observing || page is null || TopLevel.GetTopLevel(this) is null) return;
        observing = true;
        try
        {
            var target = page; var stamp = lifecycle; var id = target.Version?.Header.Identity.Id;
            var active = (activeOverride ?? window?.IsActive == true) && IsVisible && this.GetVisualAncestors().All(a => a.IsVisible) && target.CanRead;
            if (!active) { await InterruptAsync(target, readingVersion, epoch); return; }
            if (id is null || markdown.Body != target.SavedBody) return;
            scroll.UpdateLayout(); var measured = SummaryReadingAdapter.Measure(markdown.Renderer, scroll); if (measured is null) return;
            if (readingVersion != id)
            {
                if (!await target.BeginReadingAsync(id.Value, measured.ReadableText) || stamp != lifecycle || page != target) return;
                readingVersion = id; epoch = target.Reading!.State.Epoch;
                if (target.Reading.Location is { } location) { SummaryReadingAdapter.Restore(location, markdown.Renderer, scroll); measured = SummaryReadingAdapter.Measure(markdown.Renderer, scroll)!; }
            }
            if (stamp != lifecycle || measured is null) return;
            await target.ObserveReadingAsync(id.Value, epoch, Clock(), active, true, measured.FitsViewport, measured.Intervals, measured.Location);
        }
        finally { observing = false; }
    }
    private async void VersionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (page is null || sender is not ComboBox { SelectedItem: Mindweave.Core.Summaries.SummaryVersionHeader selected } ||
            page.IsLoading || page.Version?.Header.Identity.Id == selected.Identity.Id) return;
        await page.SelectVersionAsync(selected.Identity.Id);
    }
}
