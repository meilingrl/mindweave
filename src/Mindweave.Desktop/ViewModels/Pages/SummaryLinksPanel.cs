using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Storage;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// One summary's content links (requirements §10.3.3) as the network's details and the summary page show them: each link
/// with the other summary and its note, jump to it, edit the note inline (at most 16 characters, said as it is typed) and
/// delete it after one confirmation; and "关联到…" as a searchable list of the workspace's other summaries that works
/// entirely from the keyboard (canvas nodes are not keyboard-reachable, D-018). A new link opens its note editor, which
/// can be skipped.
/// </summary>
public sealed partial class SummaryLinksPanel : ObservableObject
{
    private readonly ISummaryLinks _links;
    private readonly Action<SummaryNodeId> _jump;
    private readonly SerialRefresh _load;
    private IReadOnlyList<SummaryLinkCandidate> _candidates = [];

    /// <param name="jump">Goes to the other summary: selects and centres it on the network, opens it from the summary page.</param>
    /// <param name="opens">The jump opens the other summary's page (its button says so); otherwise it shows it on the network.</param>
    public SummaryLinksPanel(ISummaryLinks links, Action<SummaryNodeId> jump, bool opens)
    {
        _links = links;
        _jump = jump;
        Opens = opens;
        _load = new SerialRefresh(LoadAsync);
    }

    /// <summary>The summary whose links are shown; null when none is.</summary>
    public SummaryNodeId? Node { get; private set; }

    /// <summary>Its shown name, for the picker's heading and the hint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickerHeading))]
    public partial string NodeName { get; private set; } = string.Empty;

    internal bool Opens { get; }

    public ObservableCollection<SummaryLinkItem> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>The last reload (tests wait on it).</summary>
    internal Task Loaded { get; private set; } = Task.CompletedTask;

    /// <summary>Why the last action did nothing (a refusal, paused storage).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; set; }

    public bool HasNotice => Notice is not null;

    /// <summary>The keyboard picker is open.</summary>
    [ObservableProperty]
    public partial bool IsPickerOpen { get; private set; }

    [ObservableProperty]
    public partial string PickerQuery { get; set; } = string.Empty;

    public ObservableCollection<SummaryLinkCandidateItem> PickerItems { get; } = [];

    public string PickerHeading => UiText.LinkPickerHeading(NodeName);

    /// <summary>Said in place of the list when it is empty: no other summary, or none matching the search.</summary>
    [ObservableProperty]
    public partial string? PickerEmptyText { get; private set; }

    /// <summary>Shows the links of <paramref name="node"/> (null: none). A different summary closes the picker and any editor.</summary>
    public Task ShowAsync(SummaryNodeId? node, string name)
    {
        if (node != Node)
        {
            Node = node;
            Items.Clear();
            OnPropertyChanged(nameof(HasItems));
            Notice = null;
            ClosePicker();
        }

        NodeName = name;
        return ReloadAsync();
    }

    /// <summary>Reads the links again (after a change anywhere); editors and confirmations open on a link stay open.</summary>
    public Task ReloadAsync() => Loaded = _load.Request();

    [RelayCommand]
    private async Task OpenPickerAsync()
    {
        if (Node is not { } node)
        {
            return;
        }

        Notice = null;
        try
        {
            _candidates = await _links.CandidatesAsync(node, CancellationToken.None);
        }
#pragma warning disable CA1031 // An unreadable list is an empty one; the network's own pick mode still works.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            _candidates = [];
        }

        if (Node != node)
        {
            return;
        }

        PickerQuery = string.Empty;
        Filter();
        IsPickerOpen = true;
    }

    public void ClosePicker()
    {
        IsPickerOpen = false;
        PickerQuery = string.Empty;
        PickerItems.Clear();
    }

    /// <summary>Esc or 取消 in the picker: closed without a link, and keyboard focus goes back to "关联到…".</summary>
    [RelayCommand]
    private void CancelPicker()
    {
        ClosePicker();
        ReturnFocus();
    }

    /// <summary>
    /// Turns true each time the picker or the network's pick mode is left without a link, so "关联到…" takes keyboard focus
    /// again instead of focus being lost with the hidden picker.
    /// </summary>
    [ObservableProperty]
    public partial bool ReturnsFocus { get; private set; }

    internal void ReturnFocus()
    {
        ReturnsFocus = false;
        ReturnsFocus = true;
    }

    /// <summary>Picks a row of the picker: the link is made and its note editor opens.</summary>
    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task PickAsync(SummaryLinkCandidateItem? item)
    {
        if (item is { CanPick: true } && await LinkToAsync(item.Node, item.Name))
        {
            ClosePicker();
        }
    }

    private static bool CanPick(SummaryLinkCandidateItem? item) => item is { CanPick: true };

    /// <summary>Enter in the search box: the first row that can be picked.</summary>
    [RelayCommand]
    private Task PickFirstAsync() => PickerItems.FirstOrDefault(i => i.CanPick) is { } first ? PickAsync(first) : Task.CompletedTask;

    /// <summary>
    /// Links the shown summary to <paramref name="other"/>, then opens the new link's note editor (optional, can be
    /// skipped). False when refused (said in <see cref="Notice"/>); nothing was written then.
    /// </summary>
    public async Task<bool> LinkToAsync(SummaryNodeId other, string otherName)
    {
        if (Node is not { } node)
        {
            return false;
        }

        Notice = null;
        SummaryLink link;
        try
        {
            link = await _links.CreateAsync(node, other, null, CancellationToken.None);
        }
        catch (SummaryLinkRefusedException ex)
        {
            Notice = UiText.SummaryLinkRefused(ex.Reason);
            return false;
        }
        catch (StorageWriteException)
        {
            Notice = UiText.LinkStoragePaused;
            return false;
        }

        await ReloadAsync();
        if (Items.FirstOrDefault(i => i.Id == link.Id) is { } added)
        {
            added.BeginNew();
        }
        else
        {
            Notice = UiText.LinkCreated(otherName);
        }

        return true;
    }

    partial void OnPickerQueryChanged(string value)
    {
        if (IsPickerOpen)
        {
            Filter();
        }
    }

    private void Filter()
    {
        var text = PickerQuery.Trim();
        PickerItems.Clear();
        foreach (var candidate in _candidates.Where(c => text.Length == 0 || c.DisplayName.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
        {
            PickerItems.Add(new SummaryLinkCandidateItem(candidate));
        }

        PickerEmptyText = PickerItems.Count > 0 ? null : _candidates.Count == 0 ? UiText.LinkPickerNone : UiText.LinkPickerNoMatch;
    }

    internal void Jump(SummaryLinkItem item) => _jump(item.Other);

    internal async Task SaveNoteAsync(SummaryLinkItem item)
    {
        if (item.HasEditError)
        {
            return;
        }

        Notice = null;
        try
        {
            await _links.EditNoteAsync(item.Id, item.EditText, CancellationToken.None);
            item.EndEdit();
        }
        catch (SummaryLinkRefusedException ex)
        {
            Notice = UiText.SummaryLinkRefused(ex.Reason);
        }
        catch (StorageWriteException)
        {
            Notice = UiText.LinkStoragePaused;
        }

        await ReloadAsync();
    }

    internal async Task DeleteAsync(SummaryLinkItem item)
    {
        Notice = null;
        try
        {
            await _links.DeleteAsync(item.Id, CancellationToken.None);
        }
        catch (StorageWriteException)
        {
            Notice = UiText.LinkStoragePaused;
            item.CancelDelete();
        }

        await ReloadAsync();
    }

    private async Task LoadAsync()
    {
        if (Node is not { } node)
        {
            return;
        }

        IReadOnlyList<SummaryLinkEntry> entries;
        try
        {
            entries = await _links.ListForNodeAsync(node, CancellationToken.None);
        }
#pragma warning disable CA1031 // Unreadable links are shown as none; the summary itself is unaffected.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            entries = [];
        }

        if (Node != node)
        {
            return;
        }

        // Kept in place by identity, so an open editor, a confirmation and keyboard focus survive a reload.
        var byId = Items.ToDictionary(i => i.Id);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (byId.TryGetValue(entry.Link.Id, out var existing))
            {
                existing.Update(entry);
                var at = Items.IndexOf(existing);
                if (at != i)
                {
                    Items.Move(at, i);
                }
            }
            else
            {
                Items.Insert(i, new SummaryLinkItem(this, entry));
            }
        }

        while (Items.Count > entries.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        OnPropertyChanged(nameof(HasItems));
    }
}

/// <summary>One link of the shown summary: the other summary, the note, and its inline note editor and delete confirmation.</summary>
public sealed partial class SummaryLinkItem : ObservableObject
{
    private readonly SummaryLinksPanel _owner;

    internal SummaryLinkItem(SummaryLinksPanel owner, SummaryLinkEntry entry)
    {
        _owner = owner;
        Entry = entry;
    }

    public SummaryLinkEntry Entry { get; private set; }

    public SummaryLinkId Id => Entry.Link.Id;

    public SummaryNodeId Other => Entry.Other;

    public string OtherName => Entry.OtherName;

    public string? Note => Entry.Link.Note;

    public bool HasNote => Note is not null;

    /// <summary>Suggested by AI and accepted (requirements §10.3.4): listed with "AI 建议，经你确认".</summary>
    public bool IsConfirmed => Entry.Link.Origin == SummaryLinkOrigin.Confirmed;

    public string AutomationName => UiText.LinkItemName(OtherName, Note, IsConfirmed);

    public string JumpName => _owner.Opens ? UiText.OpenLinkedName(OtherName) : UiText.JumpToLinkedName(OtherName);

    public string EditName => UiText.EditLinkNoteName(OtherName);

    public string DeleteName => UiText.DeleteLinkName(OtherName);

    public string EditorName => UiText.LinkNoteEditorName(OtherName);

    public string DeleteConfirmText => UiText.DeleteLinkConfirm(OtherName);

    /// <summary>The note editor is open (after 改说明, or right after the link was made).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelEditLabel))]
    public partial bool IsEditing { get; private set; }

    /// <summary>Just made: the editor's way out is 跳过 rather than 取消.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelEditLabel))]
    public partial bool IsNew { get; private set; }

    public string CancelEditLabel => IsNew ? UiText.LinkNoteSkip : UiText.Cancel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditError), nameof(HasEditError), nameof(EditCount))]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    public partial string EditText { get; set; } = string.Empty;

    /// <summary>Said while typing when the note is too long (inline validation, requirements §10.3.3).</summary>
    public string? EditError => SummaryLinkNote.IsTooLong(EditText) ? UiText.LinkNoteTooLong(SummaryLinkNote.Length(EditText)) : null;

    public bool HasEditError => EditError is not null;

    public string EditCount => UiText.LinkNoteCount(SummaryLinkNote.Length(EditText));

    [ObservableProperty]
    public partial bool IsConfirmingDelete { get; private set; }

    internal void Update(SummaryLinkEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(nameof(Entry));
        OnPropertyChanged(nameof(OtherName));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(IsConfirmed));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(JumpName));
        OnPropertyChanged(nameof(EditName));
        OnPropertyChanged(nameof(DeleteName));
        OnPropertyChanged(nameof(EditorName));
        OnPropertyChanged(nameof(DeleteConfirmText));
    }

    internal void BeginNew()
    {
        IsNew = true;
        EditText = string.Empty;
        IsConfirmingDelete = false;
        IsEditing = true;
    }

    internal void EndEdit()
    {
        IsEditing = false;
        IsNew = false;
    }

    internal void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private void Jump() => _owner.Jump(this);

    [RelayCommand]
    private void BeginEdit()
    {
        IsNew = false;
        EditText = Note ?? string.Empty;
        IsConfirmingDelete = false;
        IsEditing = true;
    }

    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private Task SaveEditAsync() => _owner.SaveNoteAsync(this);

    private bool CanSaveEdit() => !HasEditError;

    [RelayCommand]
    private void CancelEdit() => EndEdit();

    [RelayCommand]
    private void AskDelete()
    {
        EndEdit();
        IsConfirmingDelete = true;
    }

    [RelayCommand]
    private Task ConfirmDeleteAsync()
    {
        IsConfirmingDelete = false;
        return _owner.DeleteAsync(this);
    }

    [RelayCommand]
    private void CancelDeleteConfirmation() => CancelDelete();
}

/// <summary>A row of the "关联到…" picker: another summary of the workspace; an already-linked one is shown but cannot be picked.</summary>
public sealed record SummaryLinkCandidateItem(SummaryLinkCandidate Candidate)
{
    public SummaryNodeId Node => Candidate.Node;

    public string Name => Candidate.DisplayName;

    public bool Linked => Candidate.Linked;

    public bool CanPick => !Candidate.Linked;

    public string AutomationName => UiText.LinkCandidateName(Name, Linked);
}
