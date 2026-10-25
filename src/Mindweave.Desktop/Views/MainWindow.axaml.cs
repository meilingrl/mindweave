using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Mindweave.Desktop.ViewModels;

namespace Mindweave.Desktop.Views;

public partial class MainWindow : Window
{
    private bool closedSafely;
    private bool preparingClose;
    private RootViewModel? observedRoot;
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closing += HandleClosing;
        DataContextChanged += (_, _) =>
        {
            if (observedRoot is not null) observedRoot.CloseCompleted -= OnCloseCompleted;
            observedRoot = DataContext as RootViewModel;
            if (observedRoot is not null) observedRoot.CloseCompleted += OnCloseCompleted;
        };
        Closed += (_, _) => { if (observedRoot is not null) observedRoot.CloseCompleted -= OnCloseCompleted; };
    }

    private void OnCloseCompleted() { closedSafely = true; Close(); }

    private async void HandleClosing(object? sender, WindowClosingEventArgs args)
    {
        if (closedSafely || DataContext is not RootViewModel root) return;
        args.Cancel = true;
        if (preparingClose) return;
        preparingClose = true;
        try
        {
            await root.CloseAsync();
            if (root.IsClosed && !closedSafely) OnCloseCompleted();
        }
        catch (Exception error) { root.ReportCloseError(error); }
        finally { preparingClose = false; }
    }
}
