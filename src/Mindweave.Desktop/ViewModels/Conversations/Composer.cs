using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Conversations;
using Mindweave.Core.Resources;

namespace Mindweave.Desktop.ViewModels.Conversations;

/// <summary>Question input shared by the new-conversation and conversation pages.</summary>
public sealed partial class Composer : ViewModelBase
{
    private readonly Func<string, IReadOnlyList<ResourceId>, Task<SendOutcome>> _send;

    public Composer(Func<string, Task<SendOutcome>> send)
        : this((text, _) => send(text), null)
    {
    }

    /// <param name="attachments">Where attachments are stored and which draft they belong to; null offers none.</param>
    public Composer(Func<string, IReadOnlyList<ResourceId>, Task<SendOutcome>> send, ComposerAttachmentServices? attachments)
    {
        _send = send;
        _attachments = attachments;
        Viewer = new AttachmentViewer(attachments?.Resources);
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            SendCommand.NotifyCanExecuteChanged();
        };
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsBlocked { get; set; }

    /// <summary>Core storage is failing (§9.3): sending is paused and says why; the text stays editable.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(Notice), nameof(ShowNotice))]
    public partial bool IsStoragePaused { get; set; }

    /// <summary>An answer is being written: the composer offers stop where send is (the conversation page sets it).</summary>
    [ObservableProperty]
    public partial bool IsAnswerRunning { get; set; }

    /// <summary>
    /// Recommended questions offered in the empty input as a placeholder (新探索 only, requirements §10.3.1): the first is
    /// drawn dimly, Tab puts it in. Null elsewhere.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<string>? GhostSuggestions { get; set; }

    /// <summary>
    /// How much of the model's context the conversation takes, for the ring beside send (2026-11-17); the conversation
    /// page keeps it current, a new conversation leaves it null and shows no ring.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsContextRing), nameof(ContextUsageTip), nameof(ContextUsageName), nameof(ContextUsageDetails))]
    public partial ContextUsage? ContextUsage { get; set; }

    public bool ShowsContextRing => ContextUsage is not null;

    /// <summary>On hover: the share and the figures.</summary>
    public string? ContextUsageTip => ContextUsage is { } usage ? UiText.ContextUsageShort(usage) : null;

    /// <summary>For a screen reader: the whole sentence.</summary>
    public string ContextUsageName => ContextUsage is { } usage ? UiText.ContextUsage(usage) : UiText.ContextRingName;

    /// <summary>On a press: what the context is made of.</summary>
    public ContextUsageDetails? ContextUsageDetails => ContextUsage is { } usage ? new ContextUsageDetails(usage) : null;

    /// <summary>The view puts the focus in the input, the caret at the end (after <see cref="Fill"/>).</summary>
    public event EventHandler? FillRequested;

    /// <summary>Puts <paramref name="text"/> in the input, not sent, and has the view focus it.</summary>
    public void Fill(string text)
    {
        Text = text;
        FillRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The web search switch (requirements §10.4.5); null when the page offers none.</summary>
    public WebSearchSwitch? WebSearch { get; init; }

    public bool HasWebSearch => WebSearch is not null;

    /// <summary>What goes with the next question beside its text (the switch's position); null without a switch.</summary>
    public SendOptions? Options => WebSearch?.Options;

    /// <summary>Stops the running answer; null on a page that never runs one (a new conversation).</summary>
    public ICommand? StopCommand { get; init; }

    /// <summary>
    /// The hosting page already explains a storage pause in its own notice, so the composer does not repeat it
    /// (S7: the same notice only once). A page without its own notice (a new conversation) leaves this false.
    /// </summary>
    public bool HostShowsStoragePause { get; init; }

    /// <summary>Why the last send was refused; cleared when the next one is accepted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSettingsLink), nameof(ShowNotice))]
    public partial SendRejection? Rejection { get; private set; }

    public string? Notice => IsStoragePaused ? UiText.SendRejected(SendRejection.StorageUnavailable) : Rejection is { } r ? UiText.SendRejected(r) : null;

    /// <summary>Whether the composer shows <see cref="Notice"/>: not when it only repeats the page's storage notice.</summary>
    public bool ShowNotice => Notice is not null && !(HostShowsStoragePause && IsStoragePaused);

    /// <summary>Refusals the reader can fix in settings: no model yet, or a model whose context is too small.</summary>
    public bool ShowSettingsLink => Rejection is SendRejection.NotConfigured or SendRejection.QuestionTooLargeForModel or SendRejection.HistoryTooLongForModel;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var sent = Text;
        var chips = Attachments.ToList();
        var outcome = await _send(sent, [.. chips.Select(c => c.Resource!.Value)]);
        Rejection = outcome.Rejection;
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(ShowNotice));
        if (outcome.Accepted)
        {
            // The question is stored; only now is the input cleared. A refused question stays editable. Text typed while
            // the send was under way is the next question, and stays (low finding ui-vm-10).
            if (Text == sent)
            {
                Text = string.Empty;
            }

            // The attachments left the draft with the question (they are part of it now).
            foreach (var chip in chips)
            {
                Attachments.Remove(chip);
            }

            _lastConversion = null;
        }
    }

    /// <summary>Shows the outcome of an action taken elsewhere on the page (e.g. retry).</summary>
    public void Show(SendRejection? rejection)
    {
        Rejection = rejection;
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(ShowNotice));
    }

    /// <summary>
    /// Text, attachments or both; every attachment ready (one still being read, or one that could not be added, keeps the
    /// question from being sent until it is removed, requirements §10.4.3).
    /// </summary>
    private bool CanSend() =>
        !IsBlocked && !IsStoragePaused && (!string.IsNullOrWhiteSpace(Text) || Attachments.Count > 0) && Attachments.All(c => c.IsReady);

    partial void OnIsStoragePausedChanged(bool value)
    {
        // A refusal for the pause that just ended is stale.
        if (!value && Rejection == SendRejection.StorageUnavailable)
        {
            Rejection = null;
        }
    }
}
