using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>
/// A conversation page's archive, restore and delete actions, with the question asked before a delete or before a
/// restore that brings back archived ancestors, and the notice saying why one did not happen. The page reloads
/// itself through the callback given here; an archived conversation is summarized only after a restore (D-021).
/// </summary>
public sealed partial class ConversationLifecycleActions : ViewModelBase
{
    private readonly ConversationId _conversation;
    private readonly IConversations _conversations;
    private readonly IConversationDeletion? _deletion;
    private readonly ConversationDeletionRequests? _deletionPrompt;
    private readonly IWorkbenchNavigator _navigator;
    private readonly Func<WorkspaceId> _workspace;
    private readonly Func<Task> _reload;
    private readonly Func<Task> _summarize;

    internal ConversationLifecycleActions(
        ConversationId conversation,
        IConversations conversations,
        IConversationDeletion? deletion,
        ConversationDeletionRequests? deletionPrompt,
        IWorkbenchNavigator navigator,
        Func<WorkspaceId> workspace,
        Func<Task> reload,
        Func<Task> summarize)
    {
        _conversation = conversation;
        _conversations = conversations;
        _deletion = deletion;
        _deletionPrompt = deletionPrompt;
        _navigator = navigator;
        _workspace = workspace;
        _reload = reload;
        _summarize = summarize;
    }

    /// <summary>Why archiving or deleting did not happen.</summary>
    [ObservableProperty]
    public partial string? ArchiveNotice { get; private set; }

    /// <summary>A question asked before deleting, or before a restore that brings back archived ancestors.</summary>
    [ObservableProperty]
    public partial PendingConfirmation? Confirmation { get; private set; }

    /// <summary>Hides the conversation and its branches; refused (and named) while an answer or summary in them runs.</summary>
    [RelayCommand]
    private async Task ArchiveAsync()
    {
        ArchiveOutcome outcome;
        try
        {
            outcome = await _conversations.ArchiveAsync(_conversation, CancellationToken.None);
        }
        catch (StorageWriteException)
        {
            ArchiveNotice = UiText.ArchiveStorageFailed;
            return;
        }

        ArchiveNotice = outcome.Busy.Count > 0 ? UiText.ArchiveRefused(await TitlesAsync(outcome.Busy)) : null;
        await _reload();
    }

    [RelayCommand]
    private Task RestoreAndContinueAsync() => RestoreThenAsync(null);

    /// <summary>An archived conversation is never summarized directly (D-021): restore first, then summarize.</summary>
    [RelayCommand]
    private Task RestoreAndSummarizeAsync() => RestoreThenAsync(_summarize);

    /// <summary>
    /// Asks first (requirements v0.12 §8.2). A conversation without branches or summary is confirmed here, as before: it
    /// goes with its messages and unsent drafts. One that takes branches or summaries along opens the window's dialog,
    /// which states the counts, offers archiving instead and backs up first. Refused, and named, while an answer or
    /// summary in it runs.
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        ArchiveNotice = null;
        Confirmation = null;
        if (_deletion is null)
        {
            return;
        }

        ConversationDeleteScope scope;
        try
        {
            scope = await _deletion.InspectAsync([_conversation], CancellationToken.None);
        }
#pragma warning disable CA1031 // An unreadable scope deletes nothing and says so.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            ArchiveNotice = UiText.ConversationDeleteCountFailed;
            return;
        }

        if (scope.IsCascade)
        {
            _deletionPrompt?.Open(_workspace(), [_conversation]);
            return;
        }

        if (scope.Busy.Count > 0)
        {
            // Refused before asking: confirming a delete that cannot happen yet only to be told so afterwards.
            ArchiveNotice = await BusyNoticeAsync(scope);
            return;
        }

        Confirmation = new PendingConfirmation(UiText.DeleteConfirm, () => DeletePlainAsync(scope), IsDestructive: true);
    }

    private async Task DeletePlainAsync(ConversationDeleteScope confirmed)
    {
        ConversationDeleteOutcome outcome;
        try
        {
            outcome = await _deletion!.DeleteAsync(confirmed, CancellationToken.None);
        }
        catch (StorageWriteException)
        {
            ArchiveNotice = UiText.ArchiveStorageFailed;
            return;
        }

        switch (outcome.Status)
        {
            case ConversationDeleteStatus.Deleted:
            case ConversationDeleteStatus.Missing:
                ConversationRemoval.Forget(_navigator, outcome.Scope.IsEmpty ? confirmed : outcome.Scope);
                break;
            case ConversationDeleteStatus.Busy:
                ArchiveNotice = await BusyNoticeAsync(outcome.Scope);
                break;
            default:
                // A branch or summary came meanwhile: the scope is stated again, with its counts, in the dialog.
                _deletionPrompt?.Open(_workspace(), [_conversation]);
                break;
        }
    }

    private async Task<string> BusyNoticeAsync(ConversationDeleteScope scope) =>
        UiText.DeleteWorkspaceBusy([.. (await TitlesAsync([.. scope.Busy.Select(b => b.Conversation)]))
            .Zip(scope.Busy, (title, b) => (title, b.Answer, b.Summary))]);

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Confirmation is { } pending)
        {
            Confirmation = null;
            await pending.Proceed();
        }
    }

    [RelayCommand]
    private void CancelConfirmation() => Confirmation = null;

    /// <summary>Restores this conversation's path, asking first when archived ancestors come back with it, then continues.</summary>
    private async Task RestoreThenAsync(Func<Task>? then)
    {
        ArchiveNotice = null;
        var plan = await _conversations.PlanRestoreAsync(_conversation, CancellationToken.None);
        async Task Restore()
        {
            if (plan.IsHidden)
            {
                try
                {
                    await _conversations.RestoreAsync(_conversation, CancellationToken.None);
                }
                catch (StorageWriteException)
                {
                    ArchiveNotice = UiText.ArchiveStorageFailed;
                    return;
                }

                await _reload();
            }

            if (then is not null)
            {
                await then();
            }
        }

        if (plan.ArchivedAncestors.Count > 0)
        {
            Confirmation = new PendingConfirmation(UiText.RestoreConfirm(await TitlesAsync(plan.ArchivedAncestors), plan.OthersReappearing), Restore);
        }
        else
        {
            await Restore();
        }
    }

    private Task<IReadOnlyList<string>> TitlesAsync(IEnumerable<ConversationId> ids) => ConversationArchiving.TitlesAsync(_conversations, _workspace(), ids);
}
