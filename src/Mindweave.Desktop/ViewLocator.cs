using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Conversations;
using Mindweave.Desktop.Views.Pages;

namespace Mindweave.Desktop;

/// <summary>
/// Picks the view for a view model from an explicit registry. Explicit rather than name-based reflection so
/// a missing view is caught by a test instead of at runtime, and trimming cannot remove a view silently.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Registry = new()
    {
        [typeof(StartupViewModel)] = () => new StartupView(),
        [typeof(RecoveryViewModel)] = () => new RecoveryView(),
        [typeof(OnboardingViewModel)] = () => new OnboardingView(),
        [typeof(ShellViewModel)] = () => new ShellView(),
        [typeof(ExploreHomeViewModel)] = () => new ExploreHomeView(),
        [typeof(SummaryNetworkHomeViewModel)] = () => new SummaryNetworkHomeView(),
        [typeof(SummaryViewModel)] = () => new SummaryView(),
        [typeof(SettingsHomeViewModel)] = () => new SettingsHomeView(),
        [typeof(StorageDetailsViewModel)] = () => new StorageDetailsView(),
        [typeof(UsageStatisticsViewModel)] = () => new UsageStatisticsView(),
        [typeof(WorkspacesViewModel)] = () => new WorkspacesView(),
        [typeof(ConversationViewModel)] = () => new ConversationView(),
        [typeof(NewConversationViewModel)] = () => new NewConversationView(),
        [typeof(ArchivedViewModel)] = () => new ArchivedView(),
    };

    public static IReadOnlyCollection<Type> RegisteredViewModels => Registry.Keys;

    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        return Registry.TryGetValue(param.GetType(), out var create)
            ? create()
            : new TextBlock { Text = $"No view registered for {param.GetType().Name}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
