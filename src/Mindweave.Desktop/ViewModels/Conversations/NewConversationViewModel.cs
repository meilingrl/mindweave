using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>
/// Composing the first question. No conversation exists until it is sent, so abandoned drafts never leave
/// empty conversations behind.
/// </summary>
public sealed partial class NewConversationViewModel : PageViewModel
{
    private readonly IMenuNavigator _menu;

    public NewConversationViewModel(WorkspaceId workspace, IConversations conversations, IContentNavigator content, IMenuNavigator menu)
        : base(NewConversationRoute.Instance)
    {
        _menu = menu;
        Composer = new Composer(async text =>
        {
            var outcome = await conversations.StartAsync(workspace, text, CancellationToken.None);
            if (outcome.Accepted)
            {
                content.Replace(new ConversationRoute(outcome.Conversation));
            }

            return outcome;
        });
    }

    public Composer Composer { get; }

    public override string Title => UiText.NewConversation;

    [RelayCommand]
    private void OpenSettings() => _menu.Navigate(new NavigationRequest(MainSection.Settings));
}
