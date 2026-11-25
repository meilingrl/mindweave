using CommunityToolkit.Mvvm.ComponentModel;
using Mindweave.Application.Summaries;
using Mindweave.Core.Conversations;

namespace Mindweave.Desktop.ViewModels.Conversations;

public sealed partial class ConversationItemViewModel(ConversationId id, ConversationListViewModel? owner = null) : ViewModelBase
{
    public ConversationId Id { get; } = id;

    /// <summary>The list that holds the row; its context menu's commands take the row as their parameter.</summary>
    public ConversationListViewModel? Owner { get; } = owner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>
    /// The one light marker the row shows (requirements §6.1: no permanent status label), most urgent first: running,
    /// needs attention, unread.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMarker), nameof(IsGenerating), nameof(NeedsAttention), nameof(IsUnread), nameof(MarkerHint), nameof(HelpText))]
    [NotifyPropertyChangedFor(nameof(SummarizeBlockedReason), nameof(CanSummarize), nameof(ArchiveBlockedReason), nameof(CanArchive), nameof(DeleteBlockedReason), nameof(CanDelete))]
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

    /// <summary>It came from a shared file and nothing was said in it (requirements §10.7): its title is muted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnexploredHint), nameof(HelpText))]
    public partial bool IsUnexplored { get; set; }

    /// <summary>The row's tooltip while it is not explored yet; null otherwise.</summary>
    public string? UnexploredHint => IsUnexplored ? UiText.NotExplored : null;

    /// <summary>Read after the name by a screen reader: the marker, or that it is not explored yet.</summary>
    public string? HelpText => MarkerHint ?? UnexploredHint;

    /// <summary>The conversation it was branched from, when that one is listed too.</summary>
    internal ConversationId? ParentId { get; set; }

    /// <summary>Branch level: 0 for a root conversation. The view shows it as indentation (<c>Views.TreeIndent</c>), not as a separate tree control.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBranch))]
    public partial int Depth { get; set; }

    /// <summary>A branch, drawn with a small corner glyph so the tree reads as a hierarchy, not only as indentation.</summary>
    public bool IsBranch => Depth > 0;

    // ── The row's context menu (2026-11-10) ───────────────────────────────────────────────────────────────────────
    // An entry is disabled, with the refusal as its tooltip, when the same action on the page would be refused now.

    /// <summary>Storage takes no writes (requirements §9.3): every entry that writes is disabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenameBlockedReason), nameof(CanRename), nameof(SummarizeBlockedReason), nameof(CanSummarize))]
    [NotifyPropertyChangedFor(nameof(ArchiveBlockedReason), nameof(CanArchive), nameof(DeleteBlockedReason), nameof(CanDelete))]
    public partial bool IsStoragePaused { get; set; }

    /// <summary>An answer or summary runs in a listed branch below this row; archiving or deleting the row would take it along.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchiveBlockedReason), nameof(CanArchive), nameof(DeleteBlockedReason), nameof(CanDelete))]
    public partial bool IsBusyBelow { get; set; }

    public string? RenameBlockedReason => IsStoragePaused ? UiText.RowStoragePaused : null;

    public bool CanRename => RenameBlockedReason is null;

    /// <summary>As the summary service refuses: no summary while an answer of it or another summary of it runs (§6.4).</summary>
    public string? SummarizeBlockedReason =>
        IsStoragePaused ? UiText.RowStoragePaused
        : Mark == ConversationMark.Answering ? UiText.SummarizeRejected(SummarizeRejection.AnswerInProgress)
        : Mark == ConversationMark.Summarizing ? UiText.SummarizeRejected(SummarizeRejection.SummaryInProgress)
        : null;

    public bool CanSummarize => SummarizeBlockedReason is null;

    /// <summary>As archiving refuses: nothing running in the conversation or the branches it takes along.</summary>
    public string? ArchiveBlockedReason => IsStoragePaused ? UiText.RowStoragePaused : IsGenerating || IsBusyBelow ? UiText.RowArchiveBusy : null;

    public bool CanArchive => ArchiveBlockedReason is null;

    /// <summary>As a delete refuses before asking: nothing running in its scope.</summary>
    public string? DeleteBlockedReason => IsStoragePaused ? UiText.RowStoragePaused : IsGenerating || IsBusyBelow ? UiText.RowDeleteBusy : null;

    public bool CanDelete => DeleteBlockedReason is null;

    /// <summary>The row shows a box to type its new name in, in place of its title.</summary>
    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;
}
