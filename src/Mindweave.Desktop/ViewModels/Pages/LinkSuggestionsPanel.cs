using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Recommendations;
using Mindweave.Application.Storage;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// The workspace's AI-suggested links (requirements §10.3.4) as the network reviews them: 建议关联 (only when the user asks,
/// with at least three summaries), what the last request said when it stored none (quietly, with 重试 or a way to the
/// settings), and each pending suggestion with both summaries, its note (editable before accepting, at most 16 characters,
/// said while typing), its reason, 接受 and 忽略; plus 全部忽略 after one confirmation. The same list serves the details
/// panel and the list view, entirely from the keyboard (D-018).
/// </summary>
public sealed partial class LinkSuggestionsPanel : ObservableObject
{
    private readonly ILinkSuggestions _suggestions;
    private readonly WorkspaceId _workspace;
    private readonly Action<SummaryNodeId> _jump;
    private readonly Action _openSettings;
    private readonly SerialRefresh _load;
    private int _summaryCount;

    /// <summary>The suggestions 全部忽略 asked about (only these are ignored on confirming).</summary>
    private IReadOnlyCollection<LinkSuggestionId> _ignoreAllAsked = [];

    /// <param name="jump">Shows a summary of a suggestion: selects and centres it on the network.</param>
    /// <param name="openSettings">Opens 设置, when the last request needs a model or a key.</param>
    public LinkSuggestionsPanel(ILinkSuggestions suggestions, WorkspaceId workspace, Action<SummaryNodeId> jump, Action openSettings)
    {
        _suggestions = suggestions;
        _workspace = workspace;
        _jump = jump;
        _openSettings = openSettings;
        _load = new SerialRefresh(LoadAsync);
    }

    public ObservableCollection<LinkSuggestionItem> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>The section is worth showing: suggestions wait, a request runs, or the last one said something.</summary>
    public bool HasContent => HasItems || IsRunning || HasNote;

    /// <summary>The last reload (tests wait on it).</summary>
    internal Task Loaded { get; private set; } = Task.CompletedTask;

    /// <summary>The last 建议关联 the user started from this page (tests wait on it).</summary>
    internal Task<RecommendationOutcome?> LastRequest { get; private set; } = Task.FromResult<RecommendationOutcome?>(null);

    /// <summary>A request ran from here and stored suggestions (the page shows them).</summary>
    public event EventHandler? Suggested;

    /// <summary>A 建议关联 request runs for the workspace (from this page or one opened before).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContent))]
    [NotifyCanExecuteChangedFor(nameof(SuggestCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>The workspace has enough summaries for 建议关联 (at least three).</summary>
    public bool CanSuggest => _summaryCount >= LinkSuggestionPrompt.MinSummaries;

    /// <summary>The toolbar's help text: what it does, or why it is unavailable.</summary>
    public string SuggestHelp => CanSuggest ? UiText.SuggestLinksTip : UiText.SuggestLinksNeedsSummaries;

    /// <summary>What the last request said when it stored none: a failure, or that nothing new was found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote), nameof(HasContent))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    [ObservableProperty]
    public partial bool NoteCanRetry { get; private set; }

    [ObservableProperty]
    public partial bool NoteNeedsSettings { get; private set; }

    /// <summary>Why the last 接受 / 忽略 did nothing, or that it worked; cleared by the next action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    public bool HasNotice => Notice is not null;

    /// <summary>全部忽略 waits for its one confirmation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IgnoreAllConfirmText))]
    public partial bool IsConfirmingIgnoreAll { get; private set; }

    public string IgnoreAllConfirmText => UiText.IgnoreAllLinkSuggestionsConfirm(IsConfirmingIgnoreAll ? _ignoreAllAsked.Count : Items.Count);

    /// <summary>The number of saved summaries changed (the network reloaded).</summary>
    internal void SetSummaryCount(int count)
    {
        if (count == _summaryCount)
        {
            return;
        }

        _summaryCount = count;
        OnPropertyChanged(nameof(CanSuggest));
        OnPropertyChanged(nameof(SuggestHelp));
        SuggestCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reads the suggestions again (after a change anywhere); an open note editor survives by identity.</summary>
    public Task ReloadAsync() => Loaded = _load.Request();

    /// <summary>
    /// 建议关联 / 重试: asks once now. A second click while it runs does nothing (the command is disabled, and the service
    /// joins the running request). Never automatic.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunSuggest))]
    private Task<RecommendationOutcome?> SuggestAsync()
    {
        var run = RunAsync();
        LastRequest = run;
        return run;
    }

    private bool CanRunSuggest() => CanSuggest && !IsRunning;

    private async Task<RecommendationOutcome?> RunAsync()
    {
        IsRunning = true;
        ClearNote();
        Notice = null;
        IsConfirmingIgnoreAll = false;
        RecommendationOutcome outcome;
        try
        {
            outcome = await _suggestions.SuggestAsync(_workspace, CancellationToken.None);
        }
#pragma warning disable CA1031 // A suggestion is a hint: any failure is said beside it and can be retried.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            outcome = RecommendationOutcome.Refused(RecommendationProblem.Failed);
        }

        await ReloadAsync();
        ShowOutcome(outcome);
        if (outcome.Problem is null)
        {
            Suggested?.Invoke(this, EventArgs.Empty);
        }

        return outcome;
    }

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    [RelayCommand]
    private void AskIgnoreAll()
    {
        Notice = null;
        _ignoreAllAsked = [.. Items.Select(i => i.Id)];
        IsConfirmingIgnoreAll = HasItems;
    }

    [RelayCommand]
    private void CancelIgnoreAll() => IsConfirmingIgnoreAll = false;

    [RelayCommand]
    private async Task ConfirmIgnoreAllAsync()
    {
        IsConfirmingIgnoreAll = false;
        try
        {
            await _suggestions.IgnoreAllAsync(_workspace, _ignoreAllAsked, CancellationToken.None);
        }
        catch (StorageWriteException)
        {
            Notice = UiText.LinkStoragePaused;
        }

        await ReloadAsync();
    }

    /// <summary>A suggestion's line was clicked on the network: its row stands out and takes keyboard focus.</summary>
    internal void Highlight(LinkSuggestionId? id)
    {
        foreach (var item in Items)
        {
            item.IsHighlighted = item.Id == id;
        }
    }

    internal void Jump(SummaryNodeId node) => _jump(node);

    internal async Task AcceptAsync(LinkSuggestionItem item)
    {
        if (item.HasEditError)
        {
            return;
        }

        Notice = null;
        IsConfirmingIgnoreAll = false;
        try
        {
            await _suggestions.AcceptAsync(item.Suggestion, item.NoteToAccept, CancellationToken.None);
            Notice = UiText.LinkSuggestionAcceptedNotice;
        }
        catch (SummaryLinkRefusedException ex)
        {
            Notice = UiText.LinkSuggestionRefused(ex.Reason);
        }
        catch (StorageWriteException)
        {
            Notice = UiText.LinkStoragePaused;
        }

        await ReloadAsync();
    }

    internal async Task IgnoreAsync(LinkSuggestionItem item)
    {
        Notice = null;
        IsConfirmingIgnoreAll = false;
        try
        {
            await _suggestions.IgnoreAsync(item.Suggestion, CancellationToken.None);
        }
        catch (StorageWriteException)
        {
            Notice = UiText.LinkStoragePaused;
        }

        await ReloadAsync();
    }

    private void ShowOutcome(RecommendationOutcome? outcome)
    {
        if (outcome?.Problem is not { } problem || problem is RecommendationProblem.Cancelled or RecommendationProblem.Disabled)
        {
            ClearNote();
            return;
        }

        Note = UiText.LinkSuggestionsProblemText(problem, outcome.Error);
        NoteCanRetry = problem is RecommendationProblem.Failed or RecommendationProblem.StoragePaused;
        NoteNeedsSettings = problem is RecommendationProblem.NotConfigured or RecommendationProblem.Credentials;
    }

    private void ClearNote()
    {
        Note = null;
        NoteCanRetry = false;
        NoteNeedsSettings = false;
    }

    private async Task LoadAsync()
    {
        LinkSuggestionOffer offer;
        try
        {
            offer = await _suggestions.GetAsync(_workspace, CancellationToken.None);
        }
#pragma warning disable CA1031 // Unreadable suggestions are none; the network works as before.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            offer = LinkSuggestionOffer.None;
        }

        // Kept in place by identity, so an open note editor and keyboard focus survive a reload.
        var byId = Items.ToDictionary(i => i.Id);
        for (var i = 0; i < offer.Items.Count; i++)
        {
            var suggestion = offer.Items[i];
            if (byId.TryGetValue(suggestion.Id, out var existing))
            {
                existing.Update(suggestion);
                var at = Items.IndexOf(existing);
                if (at != i)
                {
                    Items.Move(at, i);
                }
            }
            else
            {
                Items.Insert(i, new LinkSuggestionItem(this, suggestion));
            }
        }

        while (Items.Count > offer.Items.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        // The question was about the suggestions shown then: when none is left, or one arrived since (a request that ended
        // meanwhile), it no longer says what confirming would do, so it closes.
        if (Items.Count == 0 || Items.Any(i => !_ignoreAllAsked.Contains(i.Id)))
        {
            IsConfirmingIgnoreAll = false;
        }

        // Opened again while a request runs or after one said something: the same is shown (the request was the user's).
        IsRunning = offer.IsRunning;
        ShowOutcome(IsRunning ? null : offer.Outcome);

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IgnoreAllConfirmText));
    }
}

/// <summary>One pending suggestion: both summaries, the note (editable before accepting), the reason, 接受 and 忽略.</summary>
public sealed partial class LinkSuggestionItem : ObservableObject
{
    private readonly LinkSuggestionsPanel _owner;

    internal LinkSuggestionItem(LinkSuggestionsPanel owner, LinkSuggestion suggestion)
    {
        _owner = owner;
        Suggestion = suggestion;
        Note = suggestion.Note;
    }

    public LinkSuggestion Suggestion { get; private set; }

    public LinkSuggestionId Id => Suggestion.Id;

    public string NameA => Suggestion.NameA;

    public string NameB => Suggestion.NameB;

    public string? Reason => Suggestion.Reason;

    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);

    /// <summary>The note to accept with: the suggested one, or what the user made of it (null for none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote), nameof(AutomationName))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    public string AutomationName => UiText.LinkSuggestionName(NameA, NameB, Note, Reason);

    public string AcceptName => UiText.AcceptLinkSuggestionName(NameA, NameB);

    public string IgnoreName => UiText.IgnoreLinkSuggestionName(NameA, NameB);

    public string EditName => UiText.EditSuggestedNoteName(NameA, NameB);

    public string EditorName => UiText.SuggestedNoteEditorName(NameA, NameB);

    public string JumpAName => UiText.ShowSuggestedSummaryName(NameA);

    public string JumpBName => UiText.ShowSuggestedSummaryName(NameB);

    /// <summary>Its line was clicked on the network.</summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    public partial bool IsEditing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditError), nameof(HasEditError), nameof(EditCount))]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand), nameof(AcceptCommand))]
    public partial string EditText { get; set; } = string.Empty;

    /// <summary>Said while typing when the note is too long (inline validation, at most 16 characters).</summary>
    public string? EditError => IsEditing && SummaryLinkNote.IsTooLong(EditText) ? UiText.LinkNoteTooLong(SummaryLinkNote.Length(EditText)) : null;

    public bool HasEditError => EditError is not null;

    public string EditCount => UiText.LinkNoteCount(SummaryLinkNote.Length(EditText));

    /// <summary>接受 while the editor is open takes what is typed (when valid); otherwise the note shown.</summary>
    internal string? NoteToAccept => IsEditing ? SummaryLinkNote.Normalize(EditText) : Note;

    internal void Update(LinkSuggestion suggestion)
    {
        var noteChanged = suggestion.Note != Suggestion.Note;
        Suggestion = suggestion;
        if (noteChanged && !IsEditing)
        {
            Note = suggestion.Note;
        }

        OnPropertyChanged(nameof(Suggestion));
        OnPropertyChanged(nameof(NameA));
        OnPropertyChanged(nameof(NameB));
        OnPropertyChanged(nameof(Reason));
        OnPropertyChanged(nameof(HasReason));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(AcceptName));
        OnPropertyChanged(nameof(IgnoreName));
        OnPropertyChanged(nameof(EditName));
        OnPropertyChanged(nameof(EditorName));
        OnPropertyChanged(nameof(JumpAName));
        OnPropertyChanged(nameof(JumpBName));
    }

    [RelayCommand]
    private void JumpA() => _owner.Jump(Suggestion.NodeA);

    [RelayCommand]
    private void JumpB() => _owner.Jump(Suggestion.NodeB);

    [RelayCommand]
    private void BeginEdit()
    {
        EditText = Note ?? string.Empty;
        IsEditing = true;
        OnPropertyChanged(nameof(EditError));
        OnPropertyChanged(nameof(HasEditError));
    }

    /// <summary>完成: keeps the edited note for 接受 (nothing is stored until accepted).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private void SaveEdit()
    {
        Note = SummaryLinkNote.Normalize(EditText);
        IsEditing = false;
    }

    private bool CanSaveEdit() => !HasEditError;

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private Task AcceptAsync() => _owner.AcceptAsync(this);

    private bool CanAccept() => !HasEditError;

    [RelayCommand]
    private Task IgnoreAsync() => _owner.IgnoreAsync(this);

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(EditError));
        OnPropertyChanged(nameof(HasEditError));
    }
}
