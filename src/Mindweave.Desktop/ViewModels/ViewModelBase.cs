using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Navigation;

namespace Mindweave.Desktop.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    public virtual void SetParameter(NavigationRequest parameter) { }
}
