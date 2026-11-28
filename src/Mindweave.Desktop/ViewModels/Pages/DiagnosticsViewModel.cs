using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application;
using Mindweave.Application.Diagnostics;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// One recorded request on 诊断 (D2 §2.2): time, purpose, model, result and duration on one line; opened in place, a failed
/// one is the failure block with its details open, and every one shows its service host, its usage, its cache and its
/// timings. A number the service did not send reads "not reported", never 0.
/// </summary>
public sealed partial class RequestRowViewModel : ViewModelBase
{
    public RequestRowViewModel(RequestRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        Extra =
        [
            .. row.Host is { } host ? [new FailureLine(UiText.FailureHostLabel, host)] : Array.Empty<FailureLine>(),
            new FailureLine(UiText.RequestUsageLabel, UiText.RequestUsage(row.PromptTokens, row.CompletionTokens)),
            new FailureLine(UiText.RequestCacheLabel, UiText.RequestCache(row.CacheReadTokens, row.CacheWriteTokens)),
            new FailureLine(UiText.RequestTimingLabel, UiText.RequestTiming(row.TimeToResponse, row.TimeToFirstText)),
        ];
        Details = new FailureViewModel(
            UiText.RequestResult(row),
            IsFailed && row.ErrorKind is { } kind ? UiText.FailureAdvice(kind) : null,
            details: row.Details,
            extra: Extra)
        {
            IsOpen = true,
        };
    }

    public RequestRow Row { get; }

    public string TimeText => Row.StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string PurposeText => UiText.RequestPurpose(Row.Purpose);

    public string ModelText => Row.Model ?? string.Empty;

    public string ResultText => UiText.RequestResult(Row);

    public bool IsFailed => Row.Outcome == AiRequestOutcome.Failed;

    public string DurationText => UiText.FailureDuration(Row.Duration);

    /// <summary>The lines after the request's own: host, usage, cache, timings.</summary>
    public IReadOnlyList<FailureLine> Extra { get; }

    /// <summary>What the row opens into: the failure block, its details open.</summary>
    public FailureViewModel Details { get; }

    public string OpenName => $"{TimeText} {PurposeText} {ResultText}";

    [ObservableProperty]
    public partial bool IsOpen { get; set; }
}

/// <summary>
/// 设置 › 诊断 (D2 §2.1, §2.2; requirements §10.8): the requests of the last 14 days, failures only at first, each opening in
/// place; the log folder; and the diagnostic package. The list holds no content, because none is stored.
/// </summary>
public sealed partial class DiagnosticsViewModel : PageViewModel
{
    /// <summary>The rows shown: the newest this many of the last 14 days.</summary>
    public const int Shown = 200;

    private readonly IRequestList _requests;
    private readonly ISystemShell _shell;
    private readonly Action _export;
    private readonly TimeProvider _time;
    private MessageId? _pendingAnswer;
    private int _reads;

    public DiagnosticsViewModel(AppEnvironment environment, IRequestList requests, ISystemShell shell, Action export, TimeProvider time)
        : base(DiagnosticsRoute.Instance)
    {
        Environment = environment;
        _requests = requests;
        _shell = shell;
        _export = export;
        _time = time;
        Loaded = LoadAsync();
    }

    public AppEnvironment Environment { get; }

    public override string Title => UiText.DiagnosticsTitle;

    public ObservableCollection<RequestRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAll))]
    public partial bool FailuresOnly { get; set; } = true;

    /// <summary>The other side of the switch, for its second segment.</summary>
    public bool ShowsAll
    {
        get => !FailuresOnly;
        set => FailuresOnly = !value;
    }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? CountText { get; private set; }

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>The latest read (tests).</summary>
    internal Task Loaded { get; private set; }

    partial void OnFailuresOnlyChanged(bool value) => Loaded = LoadAsync();

    /// <summary>Reads the list again when the page is shown again.</summary>
    public void Refresh() => Loaded = LoadAsync();

    /// <summary>
    /// Opens the newest request of <paramref name="answer"/> (a failure block's link). When it is not among failures, the
    /// list shows all; when it is not in the list at all, the list stays as it is.
    /// </summary>
    public void ShowAnswer(MessageId answer)
    {
        _pendingAnswer = answer;
        Loaded = LoadAsync();
    }

    [RelayCommand]
    private void ToggleRow(RequestRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var open = !row.IsOpen;
        foreach (var other in Rows)
        {
            other.IsOpen = false;
        }

        row.IsOpen = open;
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Environment.LogDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DesktopLog.Swallowed(ex); // The system's own message about a missing folder says enough.
        }

        _shell.OpenFolder(Environment.LogDirectory);
    }

    [RelayCommand]
    private void Export() => _export();

    private async Task LoadAsync()
    {
        var read = ++_reads;
        IsLoading = true;
        Problem = null;
        try
        {
            var since = _time.GetUtcNow().AddDays(-DiagnosticPackageService.DaysKept);
            var rows = await _requests.RecentAsync(since, FailuresOnly, Shown + 1, CancellationToken.None);
            if (_pendingAnswer is { } answer && FailuresOnly && !rows.Any(r => r.Answer == answer))
            {
                // The answer's newest request did not fail (a retry that worked): the switch moves to all.
                var all = await _requests.RecentAsync(since, failuresOnly: false, Shown + 1, CancellationToken.None);
                if (all.Any(r => r.Answer == answer) && read == _reads)
                {
                    FailuresOnly = false;
                    return;
                }
            }

            if (read != _reads)
            {
                return;
            }

            Rows.Clear();
            foreach (var row in rows.Take(Shown))
            {
                Rows.Add(new RequestRowViewModel(row));
            }

            CountText = UiText.RequestsCount(Rows.Count, FailuresOnly, rows.Count > Shown);
            if (_pendingAnswer is { } wanted && Rows.FirstOrDefault(r => r.Row.Answer == wanted) is { } found)
            {
                found.IsOpen = true;
            }

            _pendingAnswer = null;
        }
#pragma warning disable CA1031 // The page says the records could not be read; the log folder and the package still work.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(ex);
            if (read == _reads)
            {
                Rows.Clear();
                CountText = null;
                Problem = UiText.RequestsReadFailed;
            }
        }
        finally
        {
            if (read == _reads)
            {
                IsLoading = false;
            }
        }
    }
}
