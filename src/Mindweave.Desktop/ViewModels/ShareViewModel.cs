using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application;
using Mindweave.Application.Conversations;
using Mindweave.Application.Exchange;
using Mindweave.Application.Export;
using Mindweave.Application.Share;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.SummaryNetwork;

namespace Mindweave.Desktop.ViewModels;

/// <summary>Opens the share dialog on a conversation's exploration; the sidebar and the network page hold this, not the dialog.</summary>
public interface ISharePrompt
{
    void Open(WorkspaceId workspace, ConversationId conversation);
}

/// <summary>Hands a share request to the window's dialog.</summary>
public sealed class ShareRequests : ISharePrompt
{
    public event EventHandler<(WorkspaceId Workspace, ConversationId Conversation)>? Requested;

    public void Open(WorkspaceId workspace, ConversationId conversation) => Requested?.Invoke(this, (workspace, conversation));
}

public enum ShareLineKind
{
    /// <summary>A branch from its parent.</summary>
    Branch,

    /// <summary>The main line through the explorations, in time.</summary>
    Main,

    /// <summary>Two linked summaries.</summary>
    Link,
}

/// <summary>A line of the dialog's timeline. <paramref name="IsShared"/>: both ends are ticked, so the line leaves with them.</summary>
public sealed record ShareLineView(ShareLineKind Kind, double FromX, double FromY, double ToX, double ToY, bool IsShared);

/// <summary>The date over the root that starts a new day.</summary>
public sealed record ShareDayView(string Text, double X, double Y);

/// <summary>A conversation in the dialog: a dot of the timeline with a tick and the name it would leave with.</summary>
public sealed partial class ShareDotViewModel(ShareViewModel owner, ShareableConversation conversation, double x, double y, ShareDotViewModel? parent) : ViewModelBase
{
    public ConversationId Id { get; } = conversation.Id;

    public ShareDotViewModel? Parent { get; } = parent;

    public string Name { get; } = conversation.Name ?? UiText.UntitledConversation;

    /// <summary>The dot's centre on the dialog's canvas.</summary>
    public double X { get; } = x;

    public double Y { get; } = y;

    public bool HasSummary { get; } = conversation.HasSummary;

    public bool IsAnswering { get; } = conversation.IsAnswering;

    private bool _isTicked;

    /// <summary>Whether it is shared; the owner applies the rules of S1 to the whole selection.</summary>
    public bool IsTicked
    {
        get => _isTicked;
        set
        {
            if (value != _isTicked)
            {
                owner.SetTicked(Id, value);
            }
        }
    }

    /// <summary>By what is shared of it and its branches (S7); the smallest size while it is not.</summary>
    [ObservableProperty]
    public partial double Radius { get; private set; }

    internal void Show(bool ticked, int weight)
    {
        Radius = SummaryNetworkLayout.DotRadius(ticked ? weight : 1);
        SetProperty(ref _isTicked, ticked, nameof(IsTicked));
    }
}

/// <summary>
/// The share dialog (docs/design/sharing.md §6.3): the workspace's conversations as the timeline's dots with ticks, the
/// share's title, what will leave, and the file. It shows names exactly as they leave. While it is open it follows the
/// conversations, and what changed while the save dialog was open is shown before anything is written, so nothing leaves
/// that the user did not see.
/// </summary>
public sealed partial class ShareViewModel : ViewModelBase, IDisposable
{
    /// <summary>Room round the dots on the dialog's canvas, for the date over a dot and the dot's own size.</summary>
    internal const double EdgeX = 28;
    internal const double EdgeTop = 56;
    internal const double EdgeBottom = 28;

    /// <summary>Lanes closer together than on the Mindweave page: the picture is small, and a title stands just above its dot.</summary>
    private const double LaneScale = 56 / TimelineLayout.LaneGap;

    /// <summary>Canvas units a tick takes before its title.</summary>
    private const double TickWidth = 26;

    private readonly IShares _shares;
    private readonly IConversations _conversations;
    private readonly ISummaries _summaries;
    private readonly ISystemShell _shell;
    private readonly AppEnvironment _environment;
    private readonly TimeProvider _time;
    private readonly ShareRequests _requests;
    private readonly UiThrottle _recheck;
    private readonly SerialRefresh _reload;
    private readonly List<string> _leftOut = [];
    private volatile bool _watching;
    private bool _opening;
    private int _opened;
    private WorkspaceId _workspace;
    private ConversationId _conversation;
    private ShareSource _source = new([], []);
    private ShareSelection _selection = ShareSelection.Exploration(new Dictionary<ConversationId, ConversationId?>(), default);

    public ShareViewModel(IShares shares, IConversations conversations, ISummaries summaries, ISystemShell shell, AppEnvironment environment, TimeProvider time, ShareRequests requests, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(summaries);
        _shares = shares;
        _conversations = conversations;
        _summaries = summaries;
        _shell = shell;
        _environment = environment;
        _time = time;
        _requests = requests;
        requests.Requested += OnRequested;

        // While open: an answer finishing, a title arriving, a summary saved, a conversation deleted.
        _reload = new SerialRefresh(ReloadAsync);
        _recheck = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(100), () =>
        {
            if (IsOpen && !IsBusy)
            {
                Loading = _reload.Request();
            }
        });
        conversations.ActivityChanged += OnActivityChanged;
        conversations.ConversationChanged += OnConversationChanged;
        summaries.Changed += OnConversationChanged;
    }

    /// <summary>Stops listening to the services; the view it belongs to is being replaced.</summary>
    public void Dispose()
    {
        _requests.Requested -= OnRequested;
        _conversations.ActivityChanged -= OnActivityChanged;
        _conversations.ConversationChanged -= OnConversationChanged;
        _summaries.Changed -= OnConversationChanged;
    }

    /// <summary>The dialog opened and its dots are there: the view brings <see cref="OpenedOn"/> into sight.</summary>
    public event EventHandler? Opened;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>The share's own title (S7); it starts as the exploration's name and is the user's to change.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>Every conversation of the workspace, in start order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ShareDotViewModel> Dots { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ShareLineView> Lines { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ShareDayView> Days { get; private set; } = [];

    [ObservableProperty]
    public partial double CanvasWidth { get; private set; }

    [ObservableProperty]
    public partial double CanvasHeight { get; private set; }

    /// <summary>What the file will hold, one line each (S7: nothing leaves that is not listed).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Contents { get; private set; } = [];

    /// <summary>Answers of ticked conversations still being generated (§9); null without any.</summary>
    [ObservableProperty]
    public partial string? AnsweringNote { get; private set; }

    /// <summary>Saved, left out, changed, or why it was not saved.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    [ObservableProperty]
    public partial bool NoticeIsError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(CloseCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The conversation the dialog was opened on; null until read.</summary>
    public ShareDotViewModel? OpenedOn => Dots.FirstOrDefault(d => d.Id == _conversation);

    /// <summary>The last read of the conversations; tests await it.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    public void Open(WorkspaceId workspace, ConversationId conversation)
    {
        if (IsBusy)
        {
            return;
        }

        _workspace = workspace;
        _conversation = conversation;
        _opening = true;
        _opened++;
        _leftOut.Clear();
        _source = new ShareSource([], []);
        Title = string.Empty;
        Notice = null;
        Draw();
        IsOpen = true;
        _watching = true;
        Loading = _reload.Request();
    }

    /// <summary>Ticks or unticks a conversation, and with it what S1 says goes along.</summary>
    internal void SetTicked(ConversationId id, bool ticked)
    {
        if (IsBusy)
        {
            return;
        }

        _selection = ticked ? _selection.Tick(id) : _selection.Untick(id);
        ShowSelection();
    }

    private bool CanSave() => !IsBusy && _selection.Ticked.Count > 0 && !string.IsNullOrWhiteSpace(Title);

    /// <summary>
    /// Asks where, reads the conversations again and writes the file. A ticked conversation deleted meanwhile is left out
    /// and said (§9); anything else that changed is shown first, and nothing is written.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        Notice = null;
        try
        {
            var shown = Document();
            var name = ShareFileName.Suggest(shown.Title, DateOnly.FromDateTime(_time.GetLocalNow().DateTime));
            if (await _shell.PickShareFileAsync(name + ".html", CancellationToken.None) is not { } destination)
            {
                return;
            }

            Apply(await _shares.ReadAsync(_workspace, CancellationToken.None));
            var document = Document();
            if (document.Conversations.Count == 0)
            {
                Say(UiText.ShareNothingLeft, error: true);
                return;
            }

            if (!document.SameContents(shown.Without(shown.Conversations.Select(c => c.Id).Except(document.Conversations.Select(c => c.Id)))))
            {
                Say(UiText.ShareChanged, error: true);
                return;
            }

            await _shares.SaveAsync(document, destination, CancellationToken.None);
            Say(UiText.ShareSaved(destination.DisplayName), error: false);
        }
        catch (ExportWriteException ex)
        {
            Say(UiText.ExportFailed(ex.Failure, ex.Detail), error: true);
        }
#pragma warning disable CA1031 // Anything else (a failing save dialog, a store already closed by exit) is reported, never thrown at the UI thread.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(ex);
            Say(UiText.ExportFailedUnexpectedly, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanClose() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        _watching = false;
        IsOpen = false;
        _source = new ShareSource([], []);
        Draw();
    }

    private void OnRequested(object? sender, (WorkspaceId Workspace, ConversationId Conversation) request) => Open(request.Workspace, request.Conversation);

    private void OnActivityChanged(object? sender, EventArgs e) => Watch();

    private void OnConversationChanged(object? sender, ConversationId e) => Watch();

    /// <summary>Raised on worker threads.</summary>
    private void Watch()
    {
        if (_watching)
        {
            _recheck.Request();
        }
    }

    private async Task ReloadAsync()
    {
        var opened = _opened;
        ShareSource source;
        try
        {
            source = await _shares.ReadAsync(_workspace, CancellationToken.None);
        }
#pragma warning disable CA1031 // Unreadable conversations are said; closing still works.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed);
            Say(UiText.ShareReadFailed, error: true);
            return;
        }

        // Closed, opened on something else, or saving meanwhile (which reads by itself).
        if (!IsOpen || IsBusy || opened != _opened)
        {
            return;
        }

        if (!_opening)
        {
            Apply(source);
            return;
        }

        _opening = false;
        _source = source;
        _selection = ShareSelection.Exploration(source.Parents, _conversation);
        Draw();
        Title = Dots.FirstOrDefault(d => d.IsTicked && d.Parent is null)?.Name ?? string.Empty;
        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The conversations as they are now, with the ticks kept; ticked ones that are gone are said.</summary>
    private void Apply(ShareSource source)
    {
        var same = source.Conversations.SequenceEqual(_source.Conversations) && source.Links.SequenceEqual(_source.Links);
        if (same)
        {
            return;
        }

        var parents = source.Parents;
        var gone = Dots.Where(d => d.IsTicked && !parents.ContainsKey(d.Id)).Select(d => d.Name).ToList();
        _source = source;
        _selection = _selection.Over(parents);
        Draw();
        if (gone.Count > 0)
        {
            _leftOut.AddRange(gone);
            Say(null, error: false);
        }
    }

    /// <summary>The notice, with the conversations left out since the dialog opened.</summary>
    private void Say(string? text, bool error)
    {
        NoticeIsError = error;
        Notice = _leftOut.Count == 0 ? text : string.Join(' ', new[] { text, UiText.ShareLeftOut(_leftOut) }.Where(t => t is not null));
    }

    private ExchangeDocument Document() => ExchangeDocument.Structure(Title, _source, _selection, _time.GetUtcNow(), _environment.Version);

    /// <summary>Lays the dots out as the timeline does (session-network.md §5.1), wide enough for each tick and whole name.</summary>
    private void Draw()
    {
        var ordered = _source.Conversations.OrderBy(c => c.StartedAt).ThenBy(c => c.Id.Value).ToList();
        var arrangement = TimelineLayout.Arrange([.. ordered.Select((c, i) => new TimelineNode(
            new SummaryNodeId(c.Id.Value), c.Parent is { } p ? new SummaryNodeId(p.Value) : null, SummaryNetworkSession.LocalDay(c.StartedAt), i))]);
        var places = TimelineColumns.Widen(
            arrangement.Positions,
            ordered.ToDictionary(c => new SummaryNodeId(c.Id.Value), c => TickWidth + SummaryNetworkLayout.LabelWidth(c.Name ?? UiText.UntitledConversation)),
            TimelineColumns.MinColumn);
        var top = places.Values.Select(p => p.Y).DefaultIfEmpty(0).Min();
        (double X, double Y) At(ConversationId id) => places.TryGetValue(new SummaryNodeId(id.Value), out var p) ? (p.X + EdgeX, ((p.Y - top) * LaneScale) + EdgeTop) : default;

        // From the roots down, so a parent's dot is there before its branches'.
        var byId = ordered.ToDictionary(c => c.Id);
        var branches = ordered.ToLookup(c => c.Parent is { } p && byId.ContainsKey(p) ? c.Parent : null);
        var dots = new Dictionary<ConversationId, ShareDotViewModel>();
        var pending = new Queue<ShareableConversation>(branches[null]);
        while (pending.TryDequeue(out var next))
        {
            var (x, y) = At(next.Id);
            dots[next.Id] = new ShareDotViewModel(this, next, x, y, next.Parent is { } parent ? dots.GetValueOrDefault(parent) : null);
            foreach (var branch in branches[next.Id])
            {
                pending.Enqueue(branch);
            }
        }

        Dots = [.. ordered.Where(c => dots.ContainsKey(c.Id)).Select(c => dots[c.Id])];
        CanvasWidth = ordered.Select(c => At(c.Id).X + TickWidth + SummaryNetworkLayout.LabelWidth(c.Name ?? UiText.UntitledConversation)).DefaultIfEmpty(0).Max() + EdgeX;
        CanvasHeight = Dots.Select(d => d.Y).DefaultIfEmpty(0).Max() + EdgeBottom;
        var today = SummaryNetworkSession.LocalDay(_time.GetUtcNow());
        Days = [.. arrangement.DayStarts.Select(id => dots[new ConversationId(id.Value)])
            .Select(d => new ShareDayView(UiText.DayLabel(SummaryNetworkSession.LocalDay(byId[d.Id].StartedAt), today), d.X - EdgeX + 12, d.Y - EdgeTop + 2))];
        ShowSelection();
    }

    /// <summary>The ticks, the sizes of what is ticked, the lines between it, and what would leave (S7).</summary>
    private void ShowSelection()
    {
        var ticked = _selection.Ticked;
        var shared = _source.Conversations.Where(c => ticked.Contains(c.Id)).ToList();
        var weights = ConversationWeights.Of(shared.ToDictionary(c => c.Id, c => c.Parent), shared.ToDictionary(c => c.Id, c => c.Questions));
        foreach (var dot in Dots)
        {
            dot.Show(ticked.Contains(dot.Id), weights.GetValueOrDefault(dot.Id, 1));
        }

        var dots = Dots.ToDictionary(d => d.Id);
        ShareLineView Line(ShareLineKind kind, ShareDotViewModel from, ShareDotViewModel to) =>
            new(kind, from.X, from.Y, to.X, to.Y, from.IsTicked && to.IsTicked);
        var roots = Dots.Where(d => d.Parent is null).ToList();
        Lines =
        [
            .. roots.Zip(roots.Skip(1), (a, b) => Line(ShareLineKind.Main, a, b)),
            .. Dots.Where(d => d.Parent is not null).Select(d => Line(ShareLineKind.Branch, d.Parent!, d)),
            .. _source.Links.Where(l => dots.ContainsKey(l.A) && dots.ContainsKey(l.B)).Select(l => Line(ShareLineKind.Link, dots[l.A], dots[l.B])),
        ];

        var document = Document();
        Contents =
        [
            UiText.ShareCounts(document.Explorations, document.Conversations.Count, document.Questions),
            UiText.ShareHoldsConversations,
            UiText.ShareHoldsLines(document.Links.Count),
            UiText.ShareHoldsItself(document.AppVersion),
        ];
        var answering = Dots.Where(d => d.IsTicked && d.IsAnswering).Select(d => d.Name).ToList();
        AnsweringNote = answering.Count > 0 ? UiText.ShareAnswering(answering) : null;
        SaveCommand.NotifyCanExecuteChanged();
    }
}
