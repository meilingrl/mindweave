using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>A conversation page. It only projects stored and live state; closing it never affects generation.</summary>
public sealed partial class ConversationViewModel : PageViewModel, IDisposable
{
    private readonly ConversationService _conversations;
    private readonly IMenuNavigator _menu;
    private readonly IUiDispatcher _dispatcher;
    private readonly SerialRefresh _load;
    private readonly UiThrottle _reload;
    private bool _disposed;

    public ConversationViewModel(ConversationId id, ConversationService conversations, IMenuNavigator menu, IUiDispatcher dispatcher)
        : base(new ConversationRoute(id))
    {
        Id = id;
        _conversations = conversations;
        _menu = menu;
        _dispatcher = dispatcher;
        _load = new SerialRefresh(LoadAsync);
        _reload = new UiThrottle(dispatcher, TimeSpan.FromMilliseconds(30), () => _ = _load.Request());
        Composer = new Composer(text => _conversations.AskAsync(Id, text, CancellationToken.None));
        _conversations.ConversationChanged += OnConversationChanged;
        Loaded = _load.Request();
    }

    public ConversationId Id { get; }

    public Composer Composer { get; }

    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];

    /// <summary>Completes when the first load finished; tests await it.</summary>
    internal Task Loaded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string DisplayTitle { get; private set; } = UiText.NewConversation;

    public override string Title => DisplayTitle;

    [ObservableProperty]
    public partial bool IsMissing { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial MessageItemViewModel? RunningAnswer { get; private set; }

    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunningAnswer is { } answer ? _conversations.StopAsync(answer.Id) : Task.CompletedTask;

    private bool CanStop() => RunningAnswer is not null;

    [RelayCommand]
    private async Task RetryAsync(MessageItemViewModel answer)
    {
        var outcome = await _conversations.RetryAsync(Id, answer.Id, CancellationToken.None);
        Composer.Show(outcome.Rejection);
    }

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
    private void OpenSettings() => _menu.Navigate(new NavigationRequest(MainSection.Settings));

    public void Dispose()
    {
        _disposed = true;
        _conversations.ConversationChanged -= OnConversationChanged;
        foreach (var message in Messages)
        {
            message.Dispose();
        }
    }

    private void OnConversationChanged(object? sender, ConversationId changed)
    {
        if (changed == Id && !_disposed)
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

        DisplayTitle = content.Conversation.Title
            ?? (content.Messages.FirstOrDefault(m => m.Role == MessageRole.User) is { } first ? ConversationRules.FallbackTitle(first.Text) : UiText.NewConversation);

        // Rebuild from storage; unfinished answers re-bind to their live state. Items are cheap and the
        // rebuild only happens on discrete events (send, finish, rename), never per streamed chunk.
        foreach (var old in Messages)
        {
            old.Dispose();
        }

        Messages.Clear();
        var last = ConversationRules.LastAnswer(content.Messages);
        var superseded = content.Messages.Where(m => m.Supersedes is not null).Select(m => m.Supersedes!.Value).ToHashSet();
        foreach (var message in content.Messages)
        {
            var item = new MessageItemViewModel(message, _conversations.Observe(message.Id), _dispatcher)
            {
                IsSuperseded = superseded.Contains(message.Id),
            };
            item.CanRetry = last?.Id == message.Id && message.IsFinished;
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MessageItemViewModel.State))
                {
                    UpdateRunning();
                }
            };
            Messages.Add(item);
        }

        UpdateRunning();
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
