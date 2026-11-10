using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop.ViewModels;

/// <summary>
/// One pop-up hint (2026-11-10): an answer ended in a workspace that is not open. One per workspace; a further answer
/// there updates it. Clicking it switches to the workspace (<see cref="ShellViewModel"/>), × closes it, and it closes
/// itself after a while unless the pointer is on it (<see cref="WorkspaceNewsViewModel"/> owns the timer).
/// </summary>
public sealed partial class WorkspaceToastViewModel : ObservableObject
{
    private readonly Action<WorkspaceToastViewModel> _close;

    internal WorkspaceToastViewModel(WorkspaceId workspace, string name, IReadOnlyList<ConversationId> conversations, Action<WorkspaceToastViewModel> close)
    {
        Workspace = workspace;
        Name = name;
        Conversations = conversations;
        _close = close;
    }

    public WorkspaceId Workspace { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(AutomationName))]
    public partial string Name { get; internal set; }

    /// <summary>The workspace's conversations with a new answer, as last counted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Count), nameof(Text), nameof(AutomationName))]
    public partial IReadOnlyList<ConversationId> Conversations { get; internal set; }

    public int Count => Conversations.Count;

    public string Text => UiText.WorkspaceNewsToast(Name, Count);

    /// <summary>What a screen reader announces and reads for the hint: the news, then what clicking does.</summary>
    public string AutomationName => Text + "。" + UiText.WorkspaceNewsToastAction;

    /// <summary>The pointer is on the hint: it stays until the pointer leaves.</summary>
    internal bool IsHeld { get; set; }

    /// <summary>Bumped whenever the timer restarts, so a timer that was replaced does nothing when it fires.</summary>
    internal int Generation { get; set; }

    internal ITimer? Timer { get; set; }

    [RelayCommand]
    private void Close() => _close(this);
}
