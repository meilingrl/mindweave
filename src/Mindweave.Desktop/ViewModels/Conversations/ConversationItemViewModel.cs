using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Core.Conversations;

namespace Mindweave.Desktop.ViewModels.Conversations;

public sealed partial class ConversationItemViewModel(ConversationId id) : ViewModelBase
{
    public ConversationId Id { get; } = id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>
    /// The one light marker the row shows (requirements §6.1: no permanent status label), most urgent first: running,
    /// needs attention, unread.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMarker), nameof(IsGenerating), nameof(NeedsAttention), nameof(IsUnread), nameof(MarkerHint))]
    public partial ConversationMark Mark { get; set; }

    /// <summary>Tooltip and accessible name of the marker; null without one.</summary>
    public string? MarkerHint => Mark switch
    {
        ConversationMark.Answering => UiText.MarkerGenerating,
        ConversationMark.Summarizing => UiText.MarkerSummarizing,
        ConversationMark.NeedsAttention => UiText.MarkerNeedsAttention,
        ConversationMark.Unread => UiText.MarkerNewAnswer,
        _ => null,
    };

    public bool HasMarker => Mark != ConversationMark.None;

    /// <summary>Drawn as a small accent spinner: an answer is queued or being written, or a summary is.</summary>
    public bool IsGenerating => Mark is ConversationMark.Answering or ConversationMark.Summarizing;

    /// <summary>Drawn as a small round error badge with "!".</summary>
    public bool NeedsAttention => Mark == ConversationMark.NeedsAttention;

    /// <summary>Drawn as a small accent dot: an answer finished while the conversation was not on screen.</summary>
    public bool IsUnread => Mark == ConversationMark.Unread;

    /// <summary>The stored state of the last answer, kept so the marker can be worked out again without a reload.</summary>
    internal MessageState? LastAnswerState { get; set; }

    /// <summary>The conversation shown in the content area (the row's "current" look), not the multi-selection.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Part of the sidebar's multi-selection (Ctrl/Cmd or Shift click, or the 选择 mode's checkbox).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial bool IsChecked { get; set; }

    /// <summary>The row's accessible name: its title, and whether it is part of the multi-selection (drawn only as a tick).</summary>
    public string AutomationName => IsChecked ? UiText.CheckedRowName(Title) : Title;

    /// <summary>The conversation it was branched from, when that one is listed too.</summary>
    internal ConversationId? ParentId { get; set; }

    /// <summary>Branch level: 0 for a root conversation. The view shows it as indentation (<c>Views.TreeIndent</c>), not as a separate tree control.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBranch))]
    public partial int Depth { get; set; }

    /// <summary>A branch, drawn with a small corner glyph so the tree reads as a hierarchy, not only as indentation.</summary>
    public bool IsBranch => Depth > 0;
}
