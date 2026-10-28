using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>What the workbench does after a conversation was deleted (requirements §9.5).</summary>
internal static class ConversationRemoval
{
    /// <summary>
    /// Every page of the conversation leaves every stack and is disposed; if it was being shown, its parent opens
    /// instead, or the explore home for a root conversation.
    /// </summary>
    public static void Forget(IWorkbenchNavigator navigator, ConversationId deleted, ConversationId? parent) =>
        navigator.Forget(route => route is ConversationRoute page && page.Id == deleted, parent is { } p ? new ConversationRoute(p) : null);
}
