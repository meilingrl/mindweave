using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.Navigation;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>
/// A conversation page's summary part: the entry to its summary, the markers on it, summarizing and stopping, and
/// why the last attempt left nothing. The page owns loading and disposal; it calls <see cref="LoadAsync"/> on each
/// reload and <see cref="OnStoragePauseChanged"/> when the storage pause starts or ends.
/// </summary>
public sealed partial class ConversationSummaryPanel : ViewModelBase
{
    private readonly ConversationId _conversation;
    private readonly ISummaries? _summaries;
    private readonly IWorkbenchNavigator _navigator;
    private readonly Func<bool> _isStorageBlocked;
    private readonly Func<bool> _isDisposed;

    internal ConversationSummaryPanel(ConversationId conversation, ISummaries? summaries, IWorkbenchNavigator navigator, Func<bool> isStorageBlocked, Func<bool> isDisposed)
    {
        _conversation = conversation;
        _summaries = summaries;
        _navigator = navigator;
        _isStorageBlocked = isStorageBlocked;
        _isDisposed = isDisposed;
    }

    /// <summary>This conversation's summary node, if it has one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary), nameof(SummarizeLabel))]
    public partial SummaryNodeId? SummaryNode { get; private set; }

    public bool HasSummary => SummaryNode is not null;

    public string SummarizeLabel => HasSummary ? UiText.Resummarize : UiText.Summarize;

    [ObservableProperty]
    public partial string? SummaryEntryText { get; private set; }

    [ObservableProperty]
    public partial string? SummaryMarkersText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopSummaryCommand))]
    public partial GenerationTaskId? RunningSummary { get; private set; }

    /// <summary>Why the last summary attempt left nothing (failed, stopped or interrupted), shown with a retry.</summary>
    [ObservableProperty]
    public partial string? SummaryProblem { get; private set; }

    /// <summary>Why "summarize" was refused just now.</summary>
    [ObservableProperty]
    public partial string? SummaryNotice { get; private set; }

    private bool CanSummarize() => !_isStorageBlocked();

    /// <summary>Summarize the conversation as it is now; it can be done at any stage and chatting continues (§6.4).</summary>
    [RelayCommand(CanExecute = nameof(CanSummarize))]
    internal async Task SummarizeAsync()
    {
        if (_summaries is null)
        {
            return;
        }

        var outcome = await _summaries.SummarizeAsync(_conversation, CancellationToken.None);
        SummaryNotice = outcome.Rejection is { } rejection ? UiText.SummarizeRejected(rejection) : null;
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStopSummary))]
    private Task StopSummaryAsync() => RunningSummary is { } task && _summaries is not null ? _summaries.StopAsync(task) : Task.CompletedTask;

    private bool CanStopSummary() => RunningSummary is not null;

    [RelayCommand]
    private void OpenSummary()
    {
        if (SummaryNode is { } node)
        {
            _navigator.Navigate(new NavigationRequest(MainSection.Network, new SummaryRoute(node)));
        }
    }

    /// <summary>The storage pause started or ended; a refusal given because of a pause that just ended no longer applies.</summary>
    internal void OnStoragePauseChanged(bool paused)
    {
        if (!paused && SummaryNotice == UiText.SummarizeRejected(SummarizeRejection.StorageUnavailable))
        {
            SummaryNotice = null;
        }

        SummarizeCommand.NotifyCanExecuteChanged();
    }

    internal async Task LoadAsync()
    {
        if (_summaries is null || _isDisposed())
        {
            return;
        }

        var entry = await _summaries.ForConversationAsync(_conversation, CancellationToken.None);
        var task = await _summaries.LatestTaskAsync(_conversation, CancellationToken.None);
        if (_isDisposed())
        {
            return;
        }

        SummaryNode = entry?.Node.Id;
        SummaryEntryText = entry is null ? null : UiText.SummaryEntry(entry.Node.DisplayName);
        SummaryMarkersText = entry is null ? null : UiText.Markers(entry.Markers) is { Length: > 0 } markers ? markers : null;
        RunningSummary = task is { State: GenerationTaskState.Queued or GenerationTaskState.Running } ? task.Id : null;
        SummaryProblem = task is { State: GenerationTaskState.Failed or GenerationTaskState.Stopped or GenerationTaskState.Interrupted or GenerationTaskState.Cancelled }
            ? UiText.SummaryFailed(task)
            : null;
        if (RunningSummary is not null)
        {
            SummaryNotice = null;
        }
    }
}
