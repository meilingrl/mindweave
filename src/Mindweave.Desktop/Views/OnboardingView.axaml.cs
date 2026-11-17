using Avalonia.Controls;
using Avalonia.Data.Converters;
using Mindweave.Desktop.Text;

namespace Mindweave.Desktop.Views;

public sealed partial class OnboardingView : UserControl
{
    /// <summary>A name chip's accessible name: what pressing it does.</summary>
    public static readonly IValueConverter ExampleName =
        new FuncValueConverter<string?, string>(name => UiText.UseWorkspaceExampleName(name ?? string.Empty));

    public OnboardingView() => InitializeComponent();
}
