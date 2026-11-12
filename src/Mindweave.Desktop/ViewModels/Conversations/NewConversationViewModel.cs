using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Application.Recommendations;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>
/// Composing the first question. No conversation exists until it is sent, so abandoned drafts never leave
/// empty conversations behind. Under the input, up to three recommended questions (requirements §10.3.1, 2026-11-12):
/// the first also as a placeholder in the empty input; clicking one, or Tab on the placeholder, only fills the input.
/// A question sent after being filled in from one (edited or not) uses it up. Branch conversations never come here.
/// </summary>
public sealed partial class NewConversationViewModel : PageViewModel, IDisposable
{
    private readonly WorkspaceId _workspace;
    private readonly IMenuNavigator _menu;
    private readonly IRecommendations? _recommendations;
    private readonly IUiDispatcher? _dispatcher;
    private RecommendedQuestionViewModel? _source;
    private RecommendationId? _node;
    private int _loads;
    private bool _autoPrepares = true;
    private bool _disposed;

    public NewConversationViewModel(
        WorkspaceId workspace,
        IConversations conversations,
        IContentNavigator content,
        IMenuNavigator menu,
        IRecommendations? recommendations = null,
        IUiDispatcher? dispatcher = null,
        NewConversationRoute? route = null)
        : base(route ?? NewConversationRoute.Instance)
    {
        _workspace = workspace;
        _menu = menu;
        _recommendations = recommendations;
        _dispatcher = dispatcher;
        Composer = new Composer(async text =>
        {
            var source = _source;
            var node = _node;
            var outcome = await conversations.StartAsync(workspace, text, CancellationToken.None);
            if (outcome.Accepted)
            {
                if (source is not null)
                {
                    Marking = MarkUsedAsync(source.Recommendation);
                }
                else if (node is { } id)
                {
                    // Explored from a recommended node: it leaves the network now (§10.3.2); no branch source is made.
                    Marking = MarkNodeUsedAsync(id);
                }

                content.Replace(new ConversationRoute(outcome.Conversation));
            }

            return outcome;
        });
        // From a recommended node: its question waits in the input, unsent; no conversation exists until it is sent.
        if (route is { From: { } from, Question: { } question })
        {
            _node = from;
            Composer.Text = question;
        }

        Composer.PropertyChanged += OnComposerChanged;
        if (recommendations is not null)
        {
            recommendations.Changed += OnRecommendationsChanged;
            Loaded = LoadAsync();
            recommendations.NewExplorationOpened(workspace);
        }
    }

    public Composer Composer { get; }

    public override string Title => UiText.NewConversation;

    /// <summary>The recommended questions shown under the input, in order; the first is also the placeholder.</summary>
    public ObservableCollection<RecommendedQuestionViewModel> Recommendations { get; } = [];

    /// <summary>The first load of the recommendations (tests).</summary>
    public Task Loaded { get; } = Task.CompletedTask;

    /// <summary>The latest reload after a change (tests).</summary>
    public Task Reloaded { get; private set; } = Task.CompletedTask;

    /// <summary>Marking the recommendation a sent question came from as used (tests).</summary>
    public Task Marking { get; private set; } = Task.CompletedTask;

    /// <summary>Recommended questions are on, and the workspace has titles or summaries to draw on.</summary>
    [ObservableProperty]
    public partial bool ShowsRecommendations { get; private set; }

    /// <summary>A background request for them runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBusyLine), nameof(BusyText), nameof(ShowsEmptyHint))]
    public partial bool IsPreparing { get; private set; }

    /// <summary>换一批 (or 重试) runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBusyLine), nameof(BusyText), nameof(ShowsEmptyHint))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsRefreshing { get; private set; }

    /// <summary>Why nothing new came: a manual request explains; a background failure says so quietly.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote), nameof(ShowsEmptyHint))]
    public partial string? Note { get; private set; }

    [ObservableProperty]
    public partial bool NoteCanRetry { get; private set; }

    [ObservableProperty]
    public partial bool NoteNeedsSettings { get; private set; }

    public bool HasNote => Note is not null;

    /// <summary>The spinner line: 换一批 runs, or the background prepares while nothing is shown yet.</summary>
    public bool ShowsBusyLine => IsRefreshing || (IsPreparing && Recommendations.Count == 0);

    public string BusyText => IsRefreshing ? UiText.RefreshingRecommendations : UiText.PreparingRecommendations;

    /// <summary>
    /// Nothing to show, nothing running, nothing to explain, and nothing will come by itself (自动准备推荐 off): how to get
    /// some. With it on, the background is about to look, and the line would only flash.
    /// </summary>
    public bool ShowsEmptyHint => Recommendations.Count == 0 && !IsRefreshing && !IsPreparing && Note is null && !_autoPrepares;

    /// <summary>A question's row removed itself by 不感兴趣 at this index (the view moves the focus on).</summary>
    public event EventHandler<int>? Ignored;

    /// <summary>换一批: asks for new questions now; the old ones stay until new ones arrive.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (_recommendations is null)
        {
            return;
        }

        IsRefreshing = true;
        ClearNote();
        RecommendationOutcome outcome;
        try
        {
            outcome = await _recommendations.RefreshQuestionsAsync(_workspace, CancellationToken.None);
        }
#pragma warning disable CA1031 // A recommendation is a hint: any failure is said beside it and can be retried.
        catch (Exception)
#pragma warning restore CA1031
        {
            outcome = RecommendationOutcome.Refused(RecommendationProblem.Failed);
        }

        if (_disposed)
        {
            IsRefreshing = false;
            return;
        }

        // The new ones are shown before the line saying they are coming goes, so the old ones never show alone in between.
        await ReloadAsync();
        IsRefreshing = false;
        if (outcome.Problem is { } problem and not RecommendationProblem.Cancelled and not RecommendationProblem.Disabled)
        {
            Note = UiText.RecommendationProblemText(problem, outcome.Error);
            NoteCanRetry = problem is RecommendationProblem.Failed or RecommendationProblem.Empty or RecommendationProblem.StoragePaused;
            NoteNeedsSettings = problem is RecommendationProblem.NotConfigured or RecommendationProblem.Credentials;
        }
    }

    private bool CanRefresh() => !IsRefreshing;

    [RelayCommand]
    private void OpenSettings() => _menu.Navigate(new NavigationRequest(MainSection.Settings));

    /// <summary>Click or Enter on a question: it goes into the input, not sent.</summary>
    internal void Fill(RecommendedQuestionViewModel item)
    {
        _source = item;
        _node = null;
        Composer.Fill(item.Question);
    }

    internal async Task IgnoreAsync(RecommendedQuestionViewModel item)
    {
        var index = Recommendations.IndexOf(item);
        if (index < 0 || _recommendations is null)
        {
            return;
        }

        Recommendations.RemoveAt(index);
        if (ReferenceEquals(_source, item))
        {
            _source = null;
        }

        SyncGhost();
        RaiseListChanged();
        Ignored?.Invoke(this, index);
        try
        {
            await _recommendations.IgnoreAsync(item.Recommendation, CancellationToken.None);
        }
#pragma warning disable CA1031 // Not remembered this time: the next load shows it again.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        await ReloadAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        Composer.PropertyChanged -= OnComposerChanged;
        if (_recommendations is not null)
        {
            _recommendations.Changed -= OnRecommendationsChanged;
        }
    }

    private async Task MarkUsedAsync(Recommendation recommendation)
    {
        try
        {
            await _recommendations!.MarkUsedAsync(recommendation, CancellationToken.None);
        }
#pragma warning disable CA1031 // A hint: if it cannot be marked, it is offered again; the question was sent either way.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private async Task MarkNodeUsedAsync(RecommendationId id)
    {
        try
        {
            await _recommendations!.MarkUsedAsync(_workspace, id, CancellationToken.None);
        }
#pragma warning disable CA1031 // A hint: if it cannot be marked, it stays in the network; the question was sent either way.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void OnRecommendationsChanged(object? sender, WorkspaceId workspace)
    {
        if (workspace != _workspace || _disposed)
        {
            return;
        }

        if (_dispatcher is null)
        {
            Reloaded = ReloadAsync();
        }
        else
        {
            _dispatcher.Post(() => Reloaded = ReloadAsync());
        }
    }

    /// <summary>
    /// The input says which recommendation a question would come from: one put there (or typed exactly), kept while it
    /// is edited, and forgotten once the input is empty again.
    /// </summary>
    private void OnComposerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Composer.Text))
        {
            return;
        }

        var text = Composer.Text;
        if (string.IsNullOrEmpty(text))
        {
            _source = null;
            _node = null;
        }
        else if (Recommendations.FirstOrDefault(r => r.Question == text) is { } match)
        {
            _source = match;
        }
    }

    private Task LoadAsync() => ReloadAsync();

    private async Task ReloadAsync()
    {
        if (_recommendations is null || _disposed)
        {
            return;
        }

        var load = ++_loads;
        QuestionOffer offer;
        try
        {
            offer = await _recommendations.GetQuestionsAsync(_workspace, CancellationToken.None);
        }
#pragma warning disable CA1031 // Unreadable recommendations are none: the page works as before.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        // Only the newest load applies (a slower earlier one must not bring back what was removed meanwhile).
        if (load != _loads || _disposed)
        {
            return;
        }

        ShowsRecommendations = offer.Enabled && offer.HasContent;
        _autoPrepares = offer.AutoPrepares;
        IsPreparing = offer.IsPreparing;
        if (!offer.Enabled)
        {
            Recommendations.Clear();
        }
        else if (!offer.Items.Select(i => i.Id).SequenceEqual(Recommendations.Select(r => r.Recommendation.Id)))
        {
            Recommendations.Clear();
            foreach (var item in offer.Items)
            {
                Recommendations.Add(new RecommendedQuestionViewModel(this, item));
            }

            // New questions answer whatever was said before.
            if (Recommendations.Count > 0 && !IsRefreshing)
            {
                ClearNote();
            }
        }

        if (_source is not null && Recommendations.FirstOrDefault(r => r.Recommendation.Id == _source.Recommendation.Id) is { } same)
        {
            _source = same;
        }

        // A background failure is mentioned quietly, unless the user's own request already said something.
        if (offer.Failure is not null)
        {
            if (Note is null && !IsRefreshing)
            {
                Note = UiText.RecommendationsQuietFailure;
                NoteCanRetry = true;
                NoteNeedsSettings = false;
            }
        }
        else if (Note == UiText.RecommendationsQuietFailure)
        {
            ClearNote();
        }

        SyncGhost();
        RaiseListChanged();
    }

    private void ClearNote()
    {
        Note = null;
        NoteCanRetry = false;
        NoteNeedsSettings = false;
    }

    /// <summary>The first question still shown is the placeholder.</summary>
    private void SyncGhost()
    {
        var first = Recommendations.Count > 0 ? Recommendations[0].Question : null;
        if (Composer.GhostSuggestions is not [var current] || current != first)
        {
            Composer.GhostSuggestions = first is null ? null : [first];
        }
    }

    private void RaiseListChanged()
    {
        OnPropertyChanged(nameof(ShowsBusyLine));
        OnPropertyChanged(nameof(ShowsEmptyHint));
    }
}

/// <summary>One recommended question under the input.</summary>
public sealed partial class RecommendedQuestionViewModel(NewConversationViewModel page, Recommendation recommendation) : ViewModelBase
{
    public Recommendation Recommendation { get; } = recommendation ?? throw new ArgumentNullException(nameof(recommendation));

    public string Question => Recommendation.Question;

    /// <summary>"延伸自：…" when the question extends a summary; null otherwise.</summary>
    public string? BasisText => Recommendation.BasisName is { } name ? UiText.ExtendsFrom(name) : null;

    public bool HasBasis => BasisText is not null;

    /// <summary>The reason the model gave, as a tooltip.</summary>
    public string? Reason => Recommendation.Reason;

    public string AutomationName => UiText.RecommendedQuestionName(Question, Recommendation.BasisName);

    public string IgnoreName => UiText.IgnoreRecommendationName(Question);

    [RelayCommand]
    private void Fill() => page.Fill(this);

    [RelayCommand]
    private Task IgnoreAsync() => page.IgnoreAsync(this);
}
