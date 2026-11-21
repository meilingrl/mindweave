using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop.ViewModels;

/// <summary>
/// One pop-up hint (2026-11-10), of one of two kinds. For a workspace that is not open: one per workspace, a further
/// answer there updates it, and clicking it switches to the workspace. For a conversation of the open workspace that is
/// not on screen (<see cref="Conversation"/> set): one per conversation, and clicking it opens the conversation. Either
/// way clicking goes through <see cref="ShellViewModel"/>, × closes it, and it closes itself after a while unless the
/// pointer is on it (<see cref="WorkspaceNewsViewModel"/> owns the timer).
/// </summary>
public sealed partial class WorkspaceToastViewModel : ObservableObject
{
    private readonly Action<WorkspaceToastViewModel> _close;

    /// <summary>A hint for a workspace that is not open.</summary>
    internal WorkspaceToastViewModel(WorkspaceId workspace, string name, IReadOnlyList<ConversationId> conversations, Action<WorkspaceToastViewModel> close)
    {
        Workspace = workspace;
        Name = name;
        Conversations = conversations;
        _close = close;
    }

    /// <summary>A hint for one conversation of the workspace open when its answer ended; <paramref name="title"/> as the sidebar shows it.</summary>
    internal WorkspaceToastViewModel(WorkspaceId workspace, ConversationId conversation, string title, Action<WorkspaceToastViewModel> close)
        : this(workspace, title, [conversation], close)
    {
        Conversation = conversation;
    }

    /// <summary>The workspace the news is in (for a conversation's hint, the conversation's workspace).</summary>
    public WorkspaceId Workspace { get; }

    /// <summary>Set for a conversation's hint; null for a workspace's.</summary>
    public ConversationId? Conversation { get; }

    /// <summary>The workspace's name, or the conversation's title for a conversation's hint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(AutomationName))]
    public partial string Name { get; internal set; }

    /// <summary>The conversations with a new answer, as last counted (a conversation's hint: just that one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Count), nameof(Text), nameof(AutomationName))]
    public partial IReadOnlyList<ConversationId> Conversations { get; internal set; }

    public int Count => Conversations.Count;

    public string Text => Conversation is null ? UiText.WorkspaceNewsToast(Name, Count) : UiText.ConversationNewsToast(Name);

    /// <summary>The line under the news: what clicking does.</summary>
    public string ActionText => Conversation is null ? UiText.WorkspaceNewsToastAction : UiText.ConversationNewsToastAction;

    /// <summary>What a screen reader announces and reads for the hint: the news (a title in full), then what clicking does.</summary>
    public string AutomationName => Conversation is null ? UiText.Sentences(Text, ActionText) : UiText.ConversationNewsToastName(Name);

    /// <summary>The pointer is on the hint: it stays until the pointer leaves.</summary>
    internal bool IsHeld { get; set; }

    /// <summary>Bumped whenever the timer restarts, so a timer that was replaced does nothing when it fires.</summary>
    internal int Generation { get; set; }

    internal ITimer? Timer { get; set; }

    [RelayCommand]
    private void Close() => _close(this);
}
