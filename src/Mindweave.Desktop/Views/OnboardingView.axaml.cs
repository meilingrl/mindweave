using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mindweave.Desktop.Text;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Views;

public sealed partial class OnboardingView : UserControl
{
    /// <summary>A name chip's accessible name: what pressing it does.</summary>
    public static readonly IValueConverter ExampleName =
        new FuncValueConverter<string?, string>(name => UiText.UseWorkspaceExampleName(name ?? string.Empty));

    public OnboardingView()
    {
        InitializeComponent();

        // The arrow keys move between the two languages, as along the settings' segments.
        SegmentKeys.Attach(OnboardingLanguage);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree() && DataContext is OnboardingViewModel { FocusesLanguage: true } page)
        {
            _ = FocusLanguageAsync(page);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is OnboardingViewModel { FocusesLanguage: true } page)
        {
            _ = FocusLanguageAsync(page);
        }
    }

    /// <summary>
    /// The step was rebuilt after the language was chosen on it (review of merge 2, M5): the keyboard goes back to the
    /// choice, after the step's own focus request has put it on Next.
    /// </summary>
    private async Task FocusLanguageAsync(OnboardingViewModel page)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (!page.FocusesLanguage || !ReferenceEquals(DataContext, page) || !this.IsAttachedToVisualTree())
        {
            return;
        }

        page.FocusesLanguage = false;
        (page.IsEnglish ? OnboardingEnglish : OnboardingChinese).Focus();
    }
}
