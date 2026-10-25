using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels;

/// <summary>The window's root content; the root navigator swaps it between startup, recovery, onboarding and workbench.</summary>
public sealed partial class RootViewModel : ViewModelBase, IRootNavigator
{
    [ObservableProperty]
    public partial ViewModelBase? Current { get; private set; }

    public void Show(ViewModelBase page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Current = page;
    }
}
