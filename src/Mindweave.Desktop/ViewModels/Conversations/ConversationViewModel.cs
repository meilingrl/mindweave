using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.Reading;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>An ancestor shown above a branch, so the reader always sees where they are and how they got there.</summary>
public sealed record LineageItem(ConversationId Id, string Title);

/// <summary>
/// One conversation: its messages (finished ones from storage, unfinished ones live from the coordinator), the
/// branches made from its answers, and — for a branch — its ancestors and source. Reading position and draft are
/// kept per viewer slot by <see cref="ReadingStateService"/>; the view converts geometry to positions and back.
/// </summary>
public sealed partial class ConversationViewModel : PageViewModel, IDisposable
{
    private readonly ConversationService _conversations;
    private readonly ReadingStateService _reading;
    private readonly IWorkbenchNavigator _navigator;
    private readonly IUiDispatcher _dispatcher;
    private readonly ViewerSlotKey _slot;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private BranchSource? _source;
    private IReadOnlySet<ConversationId> _related = new HashSet<ConversationId>();
    private bool _disposed;

    public ConversationViewModel(ConversationRoute route, PageContext context, ConversationService conversations, ReadingStateService reading, IUiDispatcher dispatcher)
        : base(route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(context);
        Id = route.Id;
        _slot = context.Slot;
        _conversations = conversations;
        _reading = reading;
        _navigator = context.Navigator;
        _dispatcher = dispatcher;
        _load = new SerialRefresh(LoadAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(30), () => _ = _load.Request());
        Composer = new Composer(text => _conversations.AskAsync(Id, text, CancellationToken.None));
        _conversations.ConversationChanged += OnConversationChanged;
        Loaded = LoadFirstAsync(route.SourceOf);
    }

    public ConversationId Id { get; }

    public Composer Composer { get; }

    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];

    /// <summary>Ancestors from the root to the direct parent; empty for a root conversation.</summary>
    public ObservableCollection<LineageItem> Lineage { get; } = [];

    /// <summary>Completes when the first load finished, including reading position and draft; the view and tests await it.</summary>
    internal Task Loaded { get; }

    private BranchSource? _sourceFocus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string DisplayTitle { get; private set; } = UiText.NewConversation;

    public override string Title => DisplayTitle;

    [ObservableProperty]
    public partial bool IsMissing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBranch))]
    public partial string? SourceQuote { get; private set; }

    public bool IsBranch => SourceQuote is not null;

    /// <summary>Shown when the source passage could not be located exactly (MW-C13); never replaced by a text search.</summary>
    [ObservableProperty]
    public partial string? SourceNotice { get; set; }

    /// <summary>Why creating a branch was refused, if it was.</summary>
    [ObservableProperty]
    public partial string? BranchNotice { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial MessageItemViewModel? RunningAnswer { get; private set; }

    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    /// <summary>The view reports where the reader is; kept in memory at once, stored shortly after.</summary>
    public void ReportPosition(ReadingPosition position) => _reading.SetPosition(Id, _slot, position);

    /// <summary>Where the reader last was in this slot (newest in-memory value first), for a view to restore.</summary>
    public Task<ReadingPosition?> CurrentPositionAsync() => _reading.GetPositionAsync(Id, _slot, CancellationToken.None);

    /// <summary>
    /// The branch whose source this page was opened to show (return to source). Taken once: a view built later
    /// for this page (after back) restores the reading position instead.
    /// </summary>
    public BranchSource? TakeSourceFocus() => Interlocked.Exchange(ref _sourceFocus, null);

    /// <summary>Creates a branch from a selection the view captured, then opens it.</summary>
    public async Task CreateBranchAsync(BranchSelection selection)
    {
        var outcome = await _conversations.CreateBranchAsync(Id, selection, CancellationToken.None);
        BranchNotice = outcome.Rejection is { } rejection ? UiText.BranchRejected(rejection) : null;
        if (outcome.Accepted)
        {
            _navigator.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(outcome.Child)));
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunningAnswer is { } answer ? _conversations.StopAsync(answer.Id) : Task.CompletedTask;

    private bool CanStop() => RunningAnswer is not null;

    [RelayCommand]
    private async Task RetryAsync(MessageItemViewModel answer)
    {
        var outcome = await _conversations.RetryAsync(Id, answer.Id, CancellationToken.None);
        Composer.Show(outcome.Rejection);
    }

    /// <summary>Back to the passage this branch was created from; follows the branch relation, not the back history.</summary>
    [RelayCommand]
    private void ReturnToSource()
    {
        if (_source is { } source)
        {
            _navigator.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(source.Parent, SourceOf: Id), NavigationIntent.ReturnToSource));
        }
    }

    /// <summary>Re-entering a branch or an ancestor is plain navigation: it restores that conversation's own place.</summary>
    [RelayCommand]
    private void OpenConversation(ConversationId id) =>
        _navigator.Navigate(new NavigationRequest(MainSection.Explore, new ConversationRoute(id)));

    [RelayCommand]
    private void BeginRename()
    {
        RenameText = DisplayTitle;
        IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        IsRenaming = false;
        await _conversations.RenameAsync(Id, RenameText, CancellationToken.None);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private void OpenSettings() => _navigator.Navigate(new NavigationRequest(MainSection.Settings));

    public void Dispose()
    {
        _disposed = true;
        _conversations.ConversationChanged -= OnConversationChanged;
        Composer.PropertyChanged -= OnComposerChanged;
        foreach (var message in Messages)
        {
            message.Dispose();
        }
    }

    private async Task LoadFirstAsync(ConversationId? sourceOf)
    {
        // Page state first, so the draft is in place before the reader can type.
        Composer.Text = await _reading.GetDraftAsync(Id, _slot, CancellationToken.None);
        Composer.PropertyChanged += OnComposerChanged;
        if (sourceOf is { } child)
        {
            _sourceFocus = await _conversations.GetSourceAsync(child, CancellationToken.None);
        }

        await _load.Request();
    }

    private void OnComposerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Composer.Text))
        {
            _reading.SetDraft(Id, _slot, Composer.Text);
        }
    }

    private void OnConversationChanged(object? sender, ConversationId changed)
    {
        // Raised on worker threads; the throttle marshals the reload to the UI thread. Ancestors (titles shown
        // above) and branches (entries under answers) count as this page's content.
        if (!_disposed && (changed == Id || Volatile.Read(ref _related).Contains(changed)))
        {
            _reload.Request();
        }
    }

    private async Task LoadAsync()
    {
        var content = await _conversations.LoadAsync(Id, CancellationToken.None);
        if (_disposed)
        {
            return;
        }

        if (content is null)
        {
            IsMissing = true;
            DisplayTitle = UiText.ConversationMissing;
            return;
        }

        var lineage = await _conversations.GetLineageAsync(Id, CancellationToken.None);
        var branches = await _conversations.ListBranchesAsync(Id, CancellationToken.None);
        if (_disposed)
        {
            return;
        }

        _source = lineage.Count > 0 ? lineage[^1].Source : null;
        SourceQuote = _source?.Quote;
        DisplayTitle = UiText.ConversationTitle(content.Conversation, content.Messages.FirstOrDefault(m => m.Role == MessageRole.User)?.Text, _source?.Quote);
        Lineage.Clear();
        foreach (var ancestor in lineage.Take(Math.Max(0, lineage.Count - 1)))
        {
            Lineage.Add(new LineageItem(ancestor.Conversation.Id, UiText.ConversationTitle(ancestor.Conversation, null, ancestor.Source?.Quote)));
        }

        // Update in place: a message view that is still valid keeps its control, so a selection, a highlighted
        // source or the layout being read survives a reload (a title arriving, a branch being made). Only new
        // messages are added, and an answer that was live and is now stored as finished is replaced, so its
        // view switches from the live builder to the stored text that anchors are captured against.
        var last = ConversationRules.LastAnswer(content.Messages);
        var superseded = content.Messages.Where(m => m.Supersedes is not null).Select(m => m.Supersedes!.Value).ToHashSet();
        var stored = content.Messages.Select(m => m.Id).ToHashSet();
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (!stored.Contains(Messages[i].Id))
            {
                Messages[i].Dispose();
                Messages.RemoveAt(i);
            }
        }

        for (var i = 0; i < content.Messages.Count; i++)
        {
            var message = content.Messages[i];
            var item = i < Messages.Count && Messages[i].Id == message.Id ? Messages[i] : null;
            if (item is not null && item.IsLive && message.IsFinished)
            {
                item.Dispose();
                item = null;
                Messages.RemoveAt(i);
            }

            if (item is null)
            {
                item = NewItem(message);
                Messages.Insert(i, item);
            }

            item.IsSuperseded = superseded.Contains(message.Id);
            item.CanRetry = last?.Id == message.Id && message.IsFinished;
            var entries = branches.Where(b => b.SourceMessage == message.Id).Select(b => new BranchEntry(b.Child, b.Quote)).ToList();
            if (!entries.SequenceEqual(item.Branches))
            {
                item.Branches.Clear();
                foreach (var entry in entries)
                {
                    item.Branches.Add(entry);
                }
            }
        }

        Volatile.Write(ref _related, lineage.Select(l => l.Conversation.Id).Concat(branches.Select(b => b.Child)).ToHashSet());
        UpdateRunning();
    }

    private MessageItemViewModel NewItem(Message message)
    {
        var item = new MessageItemViewModel(message, _conversations.Observe(message.Id), _dispatcher);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MessageItemViewModel.State))
            {
                UpdateRunning();
            }
        };
        return item;
    }

    private void UpdateRunning()
    {
        RunningAnswer = Messages.LastOrDefault(m => m.IsAnswer && m.IsInProgress);
        Composer.IsBlocked = RunningAnswer is not null;
        var last = Messages.LastOrDefault();
        if (last is { IsAnswer: true })
        {
            last.CanRetry = !last.IsInProgress;
        }
    }
}
