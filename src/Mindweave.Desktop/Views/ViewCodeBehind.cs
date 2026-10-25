using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Mindweave.Desktop.Views;

public partial class MainView : UserControl { public MainView() => AvaloniaXamlLoader.Load(this); }
public partial class ContentView : UserControl { public ContentView() => AvaloniaXamlLoader.Load(this); }
public partial class StartupView : UserControl { public StartupView() => AvaloniaXamlLoader.Load(this); }
public partial class ChatView : UserControl { public ChatView() => AvaloniaXamlLoader.Load(this); }
public partial class SettingsView : UserControl { public SettingsView() => AvaloniaXamlLoader.Load(this); }
