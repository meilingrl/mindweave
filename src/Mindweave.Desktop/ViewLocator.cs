using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.Views;

namespace Mindweave.Desktop;

public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data) => data switch
    {
        MainViewModel => new MainView(),
        ChatViewModel => new ChatView(),
        SettingsViewModel => new SettingsView(),
        ContentViewModel => new ContentView(),
        StartupViewModel => new StartupView(),
        null => null,
        _ => throw new InvalidOperationException($"No view registered for {data.GetType().Name}.")
    };
    public bool Match(object? data) => data is ViewModelBase;
}
