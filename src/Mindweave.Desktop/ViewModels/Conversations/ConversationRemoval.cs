using Mindweave.Application.Storage;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>What the workbench does after conversations were deleted (requirements v0.12 §8.2, §9.5).</summary>
internal static class ConversationRemoval
{
    /// <summary>
    /// Every page of a deleted conversation or of one of its deleted summaries leaves every stack and is disposed. If
    /// one of them was being shown, the conversation its tree was branched from opens instead (it is outside the scope),
    /// or, for a root conversation or a summary, the page before it in the same section.
    /// </summary>
    public static void Forget(IWorkbenchNavigator navigator, ConversationDeleteScope scope)
    {
        var conversations = scope.Conversations.ToHashSet();
        var summaries = scope.Summaries.ToHashSet();
        navigator.Forget(
            route => route is ConversationRoute c && conversations.Contains(c.Id) || route is SummaryRoute s && summaries.Contains(s.Node),
            shown => shown is ConversationRoute c && scope.RootOf.TryGetValue(c.Id, out var root) && scope.ParentOf.TryGetValue(root, out var parent)
                ? new ConversationRoute(parent)
                : null);
    }
}
