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
    private readonly Application.Diagnostics.IFailureDetailsSource? _failures;
    private (GenerationTaskId Id, GenerationTaskState State)? _failedTask;

    internal ConversationSummaryPanel(
        ConversationId conversation,
        ISummaries? summaries,
        IWorkbenchNavigator navigator,
        Func<bool> isStorageBlocked,
        Func<bool> isDisposed,
        Application.Diagnostics.IFailureDetailsSource? failures = null)
    {
        _failures = failures;
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

    /// <summary>Why the last summary attempt left nothing (failed, stopped or interrupted), as a failure block (D2 §2.1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummaryFailure))]
    public partial FailureViewModel? SummaryFailure { get; private set; }

    public bool HasSummaryFailure => SummaryFailure is not null;

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
            _navigator.Navigate(new NavigationRequest(MainSection.SummaryNetwork, new SummaryRoute(node)));
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
        ShowFailure(task is { State: GenerationTaskState.Failed or GenerationTaskState.Stopped or GenerationTaskState.Interrupted or GenerationTaskState.Cancelled } ? task : null);
        if (RunningSummary is not null)
        {
            SummaryNotice = null;
        }
    }

    /// <summary>The same block stays while the same attempt is shown, so details opened stay open across reloads.</summary>
    private void ShowFailure(SummaryTaskStatus? task)
    {
        var key = task is null ? ((GenerationTaskId, GenerationTaskState)?)null : (task.Id, task.State);
        if (key == _failedTask && (task is null) == (SummaryFailure is null))
        {
            return;
        }

        _failedTask = key;
        if (task is null)
        {
            SummaryFailure = null;
            return;
        }

        var failed = task.State == GenerationTaskState.Failed;
        var fixable = failed && task.Error is { } kind && UiText.FixedInModelConnection(kind);
        var failures = _failures;
        SummaryFailure = new FailureViewModel(
            UiText.SummaryFailed(task),
            failed && task.Error is { } advised ? UiText.FailureAdvice(advised) : null,
            UiText.OpenModelConnection,
            fixable ? () => _navigator.OpenSettings(SettingsSection.ModelConnection) : null,
            Application.Diagnostics.FailureDetails.Kept(task.CreatedAt, null, task.Detail),
            failures is null || !failed ? null : ct => failures.ForTaskAsync(task.Id, ct));
    }
}
