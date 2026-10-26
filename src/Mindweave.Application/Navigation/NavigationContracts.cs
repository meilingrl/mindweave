using Mindweave.Core.Branches;
namespace Mindweave.Application.Navigation;

public enum MainArea { Explore, Network, Settings }
public enum ContentKind { Overview, Conversation, Summary }
public enum NavigationIntent { Open, ReturnToSource, Restore }
public sealed record NavigationRequest(MainArea Area, ContentKind Kind = ContentKind.Overview,
    Guid? TargetId = null, NavigationIntent Intent = NavigationIntent.Open, SourceNavigation? Source = null);

public enum RootDestination { StartupRecovery, Workbench }
public interface IRootNavigationService { void Navigate(RootDestination destination); }
public interface IMenuNavigationService { Task NavigateAsync(MainArea area); }
public interface IContentNavigationService
{
    Task NavigateAsync(NavigationRequest request);
    Task GoBackAsync();
}

public sealed record SourceNavigation(Guid ChildConversationId, Guid ParentConversationId, SourceSelection Selection);
