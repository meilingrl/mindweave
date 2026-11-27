using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Diagnostics;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels;

/// <summary>One line of a failure's details: a label and its value.</summary>
public sealed record FailureLine(string Label, string Value);

/// <summary>
/// The one failure block (D2 §2.1, requirements §10.8), the same wherever a failed request is shown: what failed and its
/// kind, then what to do with a link to the setting that fixes it, then details, closed at first, that can be selected and
/// copied together with the app version and the system. The details may be read when first opened (an answer's request
/// record); what the failed message itself kept is shown when no record is found. Holds no conversation text and no key.
/// </summary>
public sealed partial class FailureViewModel : ViewModelBase
{
    private static readonly Lazy<string> InstallationLine = new(() =>
    {
        var stamp = BuildStamp.Read(typeof(FailureViewModel).Assembly);
        var app = stamp.Commit is { } commit ? $"Mindweave {stamp.Version} ({commit})" : $"Mindweave {stamp.Version}";
        return $"{app} · {Installation.System()}";
    });

    private readonly Action? _fix;
    private readonly Func<CancellationToken, Task<FailureDetails?>>? _load;
    private readonly IReadOnlyList<FailureLine> _extra;
    private FailureDetails? _details;
    private Task? _loading;

    /// <param name="headline">Line 1: what failed and the kind, as the page has always said it.</param>
    /// <param name="advice">Line 2: what to do, one sentence; null when the headline says it.</param>
    /// <param name="fixLabel">The link to the setting that fixes it; shown only with <paramref name="fix"/>.</param>
    /// <param name="details">What is known at once (the message's own text, a test's status).</param>
    /// <param name="load">Reads the request record when the details are first opened; its fields win over <paramref name="details"/>.</param>
    /// <param name="extra">Lines after the request's own (an MCP server's recent output).</param>
    public FailureViewModel(
        string headline,
        string? advice = null,
        string? fixLabel = null,
        Action? fix = null,
        FailureDetails? details = null,
        Func<CancellationToken, Task<FailureDetails?>>? load = null,
        IReadOnlyList<FailureLine>? extra = null)
    {
        Headline = headline;
        Advice = advice;
        FixLabel = fix is null ? null : fixLabel;
        _fix = fix;
        _details = details;
        _load = load;
        _extra = extra ?? [];
        Lines = Build();
    }

    public string Headline { get; }

    public string? Advice { get; }

    public bool HasAdvice => Advice is not null;

    public string? FixLabel { get; }

    public bool HasFix => FixLabel is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLines))]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLines))]
    public partial bool IsLoading { get; private set; }

    /// <summary>The details as label and value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsText), nameof(CopyText), nameof(HasLines), nameof(ShowsLines), nameof(ShowsNoRecord))]
    public partial IReadOnlyList<FailureLine> Lines { get; private set; }

    public bool HasLines => Lines.Count > 0;

    public bool ShowsLines => IsOpen && !IsLoading && HasLines;

    /// <summary>Opened, read, and nothing but the time was found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNoRecord))]
    public partial bool FoundNoRecord { get; private set; }

    public bool ShowsNoRecord => FoundNoRecord && !IsLoading;

    /// <summary>The details as one selectable text, a line each.</summary>
    public string DetailsText => string.Join(Environment.NewLine, Lines.Select(l => l.Label + UiText.FailureLabelSeparator + l.Value));

    /// <summary>What "Copy details" puts on the clipboard: the block, its details, then the app version and the system.</summary>
    public string CopyText
    {
        get
        {
            var text = new StringBuilder().AppendLine(Headline);
            if (Advice is not null)
            {
                text.AppendLine(Advice);
            }

            if (Lines.Count > 0)
            {
                text.AppendLine(DetailsText);
            }

            return text.Append(InstallationLine.Value).ToString();
        }
    }

    /// <summary>The latest read of the record (tests).</summary>
    internal Task Loaded => _loading ?? Task.CompletedTask;

    partial void OnIsOpenChanged(bool value)
    {
        if (value && _load is not null && _loading is null)
        {
            _loading = LoadAsync();
        }
    }

    [RelayCommand]
    private void ToggleDetails() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Fix() => _fix?.Invoke();

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var found = await _load!(CancellationToken.None);
            if (found is not null)
            {
                _details = Merge(found, _details);
            }

            FoundNoRecord = found is null || !found.HasAny;
        }
#pragma warning disable CA1031 // The record is a convenience: what the message kept is still shown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(ex);
            FoundNoRecord = true;
        }
        finally
        {
            Lines = Build();
            IsLoading = false;
        }
    }

    /// <summary>The record's fields, and what only the message kept where the record has none.</summary>
    private static FailureDetails Merge(FailureDetails record, FailureDetails? kept) =>
        kept is null
            ? record
            : new FailureDetails(
                record.At ?? kept.At,
                record.Model ?? kept.Model,
                record.HttpStatus ?? kept.HttpStatus,
                record.ProviderRequestId ?? kept.ProviderRequestId,
                record.Duration ?? kept.Duration,
                kept.ServiceText ?? record.ServiceText);

    private List<FailureLine> Build()
    {
        var lines = new List<FailureLine>();
        if (_details is { } d)
        {
            if (d.At is { } at)
            {
                lines.Add(new(UiText.FailureTimeLabel, at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            }

            Add(UiText.FailureModelLabel, d.Model);
            Add(UiText.FailureStatusLabel, d.HttpStatus?.ToString(CultureInfo.InvariantCulture));
            Add(UiText.FailureRequestIdLabel, d.ProviderRequestId);
            Add(UiText.FailureDurationLabel, d.Duration is { } duration ? UiText.FailureDuration(duration) : null);
            Add(UiText.FailureServiceTextLabel, d.ServiceText);
        }

        lines.AddRange(_extra);
        return lines;

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add(new(label, value));
            }
        }
    }
}
