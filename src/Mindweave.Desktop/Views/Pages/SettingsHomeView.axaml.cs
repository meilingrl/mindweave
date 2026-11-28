using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Views.Pages;

/// <summary>
/// Settings. The arrow keys move along the appearance and language segments (keyboard handling about the control, not
/// the settings); the model field's own handling lives in <see cref="ModelSettingsForm"/>.
/// </summary>
public sealed partial class SettingsHomeView : UserControl
{
    public SettingsHomeView()
    {
        InitializeComponent();
        SegmentKeys.Attach(AppearancePicker);
        SegmentKeys.Attach(LanguagePicker);
        Scroller.ScrollChanged += OnScrollChanged;
        AttachFinding();
    }

    /// <summary>
    /// The page keeps how far it is scrolled, so a language switch can carry it over; a carried position is applied as
    /// soon as the page is long enough for it (sections fill in as their settings are read), and kept no longer.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not SettingsHomeViewModel page)
        {
            return;
        }

        if (page.PendingScroll is { } wanted)
        {
            if (Scroller.Extent.Height - Scroller.Viewport.Height >= wanted - 0.5)
            {
                page.PendingScroll = null;
                Scroller.Offset = new Vector(Scroller.Offset.X, wanted);
            }

            return;
        }

        page.ScrollOffset = Scroller.Offset.Y;
    }

    /// <summary>
    /// Each showing builds this view anew: whichever of attaching and receiving the page comes second tells the page it is
    /// shown, so it can read the application's settings again (health check 2026-11-12, ui-vm-3).
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree())
        {
            (DataContext as SettingsHomeViewModel)?.Refresh();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is SettingsHomeViewModel page)
        {
            page.Refresh();

            // Always: a link may ask for a section just after this view is shown (the workbench sets it once it navigated).
            _ = SettleAsync(page);
        }
    }

    /// <summary>
    /// Once the settings are read and laid out after a language switch: a carried position the page is too short for
    /// goes as far as it can, and when the language was chosen here, its row is brought into view with its choice
    /// focused (bilingual-ui.md §6.8).
    /// </summary>
    private async Task SettleAsync(SettingsHomeViewModel page)
    {
        await page.Loaded;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        if (!ReferenceEquals(DataContext, page) || !this.IsAttachedToVisualTree())
        {
            return;
        }

        if (page.PendingScroll is { } wanted)
        {
            page.PendingScroll = null;
            Scroller.Offset = new Vector(Scroller.Offset.X, wanted);
            page.ScrollOffset = Scroller.Offset.Y;
        }

        if (page.PendingSection is { } section)
        {
            page.PendingSection = null;
            JumpTo(section switch
            {
                Navigation.SettingsSection.WebSearch => WebSearchSection,
                _ => ModelSection,
            });
        }

        if (page.FocusesLanguage)
        {
            page.FocusesLanguage = false;
            LanguagePicker.BringIntoView();
            (page.IsEnglish ? LanguageEnglish : LanguageChinese).Focus();
        }
    }
}
