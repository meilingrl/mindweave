using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels;

public sealed partial class StartupViewModel : ViewModelBase
{
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private bool isBusy;
    public bool HasError => ErrorMessage is not null;
    public string DataDirectory { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand OpenDataDirectoryCommand { get; }
    public IRelayCommand ExitCommand { get; }
    public StartupViewModel(Func<Task> retry, IDesktopActions actions, string dataDirectory)
    {
        DataDirectory = dataDirectory;
        RetryCommand = new AsyncRelayCommand(retry);
        OpenDataDirectoryCommand = new AsyncRelayCommand(async () =>
        {
            try { await actions.OpenDataDirectoryAsync(); }
            catch (Exception error) { ErrorMessage = $"无法打开数据位置：{error.Message}"; }
        });
        ExitCommand = new RelayCommand(actions.Exit);
    }
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
