using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application;
using Mindweave.Application.Languages;
using Mindweave.Application.Preferences;
using Mindweave.Application.Startup;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>Settings: the model connection, the appearance (D-022) and where data lives.</summary>
public sealed partial class SettingsHomeViewModel : PageViewModel, IDisposable, ICarriesViewState
{
    private readonly IContentNavigator _content;
    private readonly IAppearance _appearanceService;
    private readonly IThemeApplier _theme;
    private readonly IWorkspaceDirectory? _workspaces;
    private readonly INewsAlerts? _newsAlerts;
    private readonly IRecommendationSettings? _recommendationSettings;
    private readonly LanguageSwitch? _language;
    private readonly TextWidth? _textWidth;

    /// <summary>
    /// The choices the reader made since the page last loaded, by property name: a load still under way never
    /// overwrites them with what it read.
    /// </summary>
    private readonly HashSet<string> _chosen = [];
    private bool _loading;

    public SettingsHomeViewModel(
        AppEnvironment environment,
        ModelSettingsViewModel model,
        IContentNavigator content,
        IAppearance appearance,
        IThemeApplier theme,
        IWorkspaceDirectory? workspaces = null,
        INewsAlerts? newsAlerts = null,
        IRecommendationSettings? recommendationSettings = null,
        ToolSettingsPanel? tools = null,
        LanguageSwitch? language = null,
        TextWidth? textWidth = null,
        PersonalNotePanel? note = null)
        : base(SettingsHomeRoute.Instance)
    {
        Note = note;
        _textWidth = textWidth;
        _language = language;
        if (language is not null)
        {
            language.Changed += OnLanguageSwitchChanged;
        }

        Tools = tools;
        if (tools is not null)
        {
            // Declaring or undeclaring tool calls for the model changes whether 设置 › 工具 can be used.
            model.Saved += (_, _) => _ = tools.RefreshAvailabilityAsync();
        }

        _newsAlerts = newsAlerts;
        _recommendationSettings = recommendationSettings;
        _workspaces = workspaces;
        if (workspaces is not null)
        {
            workspaces.Workspaces.CollectionChanged += OnWorkspacesChanged;
        }

        Environment = environment;
        Model = model;
        _content = content;
        _appearanceService = appearance;
        _theme = theme;
        Loaded = LoadAsync();
    }

    public AppEnvironment Environment { get; }

    /// <summary>
    /// The version line under the settings: product and version; except in a release, also the build's commit and its
    /// date (when the build knows them) and the data channel, so a daily or dev build says which build it is.
    /// </summary>
    public string VersionText => VersionTextFor(Environment);

    public static string VersionTextFor(AppEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var version = $"{UiText.AppName} {environment.Version}";
        if (!environment.ShowsChannel)
            return version;
        var parts = new List<string> { version };
        if (!string.IsNullOrEmpty(environment.Commit))
            parts.Add(environment.Commit);
        if (!string.IsNullOrEmpty(environment.CommitDate))
            parts.Add(environment.CommitDate);
        parts.Add($"{UiText.ChannelLabel} {environment.Channel}");
        return string.Join(" · ", parts);
    }

    public ModelSettingsViewModel Model { get; }

    /// <summary>设置 › 工具 (requirements §10.4.4); null where tool settings are not wired.</summary>
    public ToolSettingsPanel? Tools { get; }

    public bool HasTools => Tools is not null;

    /// <summary>设置 › 个人说明 (requirements §10.9); null where the note is not wired.</summary>
    public PersonalNotePanel? Note { get; }

    public bool HasNote => Note is not null;

    /// <summary>The 工作区 section's entry to 设置 › 工作区 is offered (the page is built with the workbench's list).</summary>
    public bool CanManageWorkspaces => _workspaces is not null;

    /// <summary>How many workspaces there are, beside that entry (2026-11-10).</summary>
    public int WorkspaceCount => _workspaces?.Workspaces.Count ?? 0;

    public string WorkspaceCountText => UiText.WorkspacesCount(WorkspaceCount);

    public string ManageWorkspacesName => UiText.ManageWorkspacesName(WorkspaceCount);

    public override string Title => UiText.SettingsTitle;

    internal Task Loaded { get; private set; }

    private bool _shown;

    /// <summary>
    /// Shown again (health check 2026-11-12, ui-vm-3): each workspace has its own settings page, but the settings are the
    /// application's, so another workspace's page may have changed them. They are read again; the model form keeps what
    /// the reader is editing. The first showing is skipped (the constructor's read is under way).
    /// </summary>
    public void Refresh()
    {
        if (!_shown)
        {
            _shown = true;
            return;
        }

        _chosen.Clear();
        Loaded = LoadAsync();
        Model.ReloadIfUnchanged();
        Note?.Refresh();
    }

    /// <summary>The stored choice; changing it applies at once and is saved in the background.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFollowSystem), nameof(IsLight), nameof(IsDark))]
    public partial Appearance Appearance { get; set; }

    /// <summary>Shown only when saving the choice failed; the choice still applies until the application closes.</summary>
    [ObservableProperty]
    public partial string? AppearanceStatus { get; private set; }

    public bool IsFollowSystem
    {
        get => Appearance == Appearance.FollowSystem;
        set => Choose(value, Appearance.FollowSystem);
    }

    public bool IsLight
    {
        get => Appearance == Appearance.Light;
        set => Choose(value, Appearance.Light);
    }

    public bool IsDark
    {
        get => Appearance == Appearance.Dark;
        set => Choose(value, Appearance.Dark);
    }

    /// <summary>The text widths offered (the owner, 2026-11-23); any other is dragged on the page itself.</summary>
    public const double NarrowText = 640;

    public const double MediumText = TextWidthService.Default;

    public const double WideText = 960;

    /// <summary>The settings page can offer the text width (it is built with it).</summary>
    public bool CanChooseTextWidth => _textWidth is not null;

    public bool IsTextWhole
    {
        get => _textWidth?.Pixels is null;
        set => ChooseTextWidth(value, null);
    }

    public bool IsTextWide
    {
        get => _textWidth?.Pixels == WideText;
        set => ChooseTextWidth(value, WideText);
    }

    public bool IsTextMedium
    {
        get => _textWidth?.Pixels == MediumText;
        set => ChooseTextWidth(value, MediumText);
    }

    public bool IsTextNarrow
    {
        get => _textWidth?.Pixels == NarrowText;
        set => ChooseTextWidth(value, NarrowText);
    }

    /// <summary>The way to any other width; a dragged one is named here, as no choice above is it.</summary>
    public string TextWidthHint => _textWidth?.Pixels is { } width and not (WideText or MediumText or NarrowText)
        ? UiText.TextWidthCustom((int)Math.Round(width))
        : UiText.TextWidthHint;

    private void ChooseTextWidth(bool chosen, double? width)
    {
        if (!chosen || _textWidth is null || _textWidth.Pixels == width)
        {
            return;
        }

        _textWidth.Pixels = width;
        _textWidth.Save();
        OnPropertyChanged(nameof(IsTextWhole));
        OnPropertyChanged(nameof(IsTextWide));
        OnPropertyChanged(nameof(IsTextMedium));
        OnPropertyChanged(nameof(IsTextNarrow));
        OnPropertyChanged(nameof(TextWidthHint));
    }

    /// <summary>The colour of bold text in answers (D-027); changing it applies at once and is saved in the background.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBoldBrown), nameof(IsBoldSlate), nameof(IsBoldSage), nameof(IsBoldPlum), nameof(IsBoldSameAsText))]
    public partial BoldColour BoldColour { get; set; }

    /// <summary>Shown only when saving the bold colour failed; the choice still applies until the application closes.</summary>
    [ObservableProperty]
    public partial string? BoldColourStatus { get; private set; }

    public bool IsBoldBrown
    {
        get => BoldColour == BoldColour.Brown;
        set => ChooseBoldColour(value, BoldColour.Brown);
    }

    public bool IsBoldSlate
    {
        get => BoldColour == BoldColour.Slate;
        set => ChooseBoldColour(value, BoldColour.Slate);
    }

    public bool IsBoldSage
    {
        get => BoldColour == BoldColour.Sage;
        set => ChooseBoldColour(value, BoldColour.Sage);
    }

    public bool IsBoldPlum
    {
        get => BoldColour == BoldColour.Plum;
        set => ChooseBoldColour(value, BoldColour.Plum);
    }

    public bool IsBoldSameAsText
    {
        get => BoldColour == BoldColour.SameAsText;
        set => ChooseBoldColour(value, BoldColour.SameAsText);
    }

    private void ChooseBoldColour(bool chosen, BoldColour colour)
    {
        if (chosen)
        {
            _chosen.Add(nameof(BoldColour));
            BoldColour = colour;
        }
    }

    partial void OnBoldColourChanged(BoldColour value)
    {
        if (_loading)
        {
            return;
        }

        _chosen.Add(nameof(BoldColour));
        _theme.ApplyBoldColour(value);
        SaveInBackground(() => _appearanceService.SetBoldColourAsync(value, CancellationToken.None), failed => BoldColourStatus = failed ? UiText.BoldColourSaveFailed : null);
    }

    /// <summary>Completes when the last choice made on this page has been written (or failed).</summary>
    internal Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>The page can offer the interface language (it is built with the switch).</summary>
    public bool CanChooseLanguage => _language is not null;

    /// <summary>
    /// The interface language (bilingual-ui.md §7). Choosing saves the choice and switches in place: this page is
    /// rebuilt in the new language, on the same section, with the language row in view.
    /// </summary>
    public bool IsEnglish
    {
        get => CurrentLanguage.Value == UiLanguage.English;
        set => ChooseLanguage(value, UiLanguage.English);
    }

    public bool IsChinese
    {
        get => CurrentLanguage.Value == UiLanguage.Chinese;
        set => ChooseLanguage(value, UiLanguage.Chinese);
    }

    /// <summary>
    /// Beside the choice: the switch waits for something to finish, it could not be made, or saving the choice failed
    /// (the language still applies until the application closes); otherwise nothing.
    /// </summary>
    public string? LanguageStatus => _language?.Notice;

    /// <summary>The status says the switch waits: shown as progress, not as a problem.</summary>
    public bool IsLanguageWaiting => _language?.IsWaiting == true;

    /// <summary>The language was chosen on this page: the page built in its place shows the language row and focuses it.</summary>
    public bool FocusesLanguage { get; set; }

    private bool _languageChosen;

    private void ChooseLanguage(bool chosen, UiLanguage language)
    {
        if (chosen && _language is not null)
        {
            _languageChosen = true;

            // The switch never faults: what went wrong is in its notice. Leaving this page while it waits drops it.
            _ = _language.SwitchAsync(language, this);
        }
    }

    /// <summary>The switch began or stopped waiting, or ended: the status and, if it did not happen, the choice shown follow.</summary>
    private void OnLanguageSwitchChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LanguageStatus));
        OnPropertyChanged(nameof(IsLanguageWaiting));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsChinese));
    }

    /// <summary>The settings page can offer the sound (it is built with the preference).</summary>
    public bool CanChooseNewsSound => _newsAlerts is not null;

    /// <summary>
    /// The system's alert sound when a new-answer hint pops up (2026-11-10); off unless chosen. Changing it applies at
    /// once and is saved in the background.
    /// </summary>
    [ObservableProperty]
    public partial bool PlaysNewsSound { get; set; }

    /// <summary>Shown only when saving the sound choice failed; the choice still applies until the application closes.</summary>
    [ObservableProperty]
    public partial string? NewsSoundStatus { get; private set; }

    /// <summary>
    /// Conversations of the open workspace pop up a hint too, not only other workspaces (2026-11-10); on unless turned
    /// off. Changing it applies at once and is saved in the background.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowsInWorkspaceNews { get; set; } = true;

    /// <summary>Shown only when saving that choice failed; the choice still applies until the application closes.</summary>
    [ObservableProperty]
    public partial string? InWorkspaceNewsStatus { get; private set; }

    /// <summary>
    /// A workspace's names are prepared in the background once new conversations have titles (2026-11-11); on unless
    /// turned off. Changing it applies at once and is saved in the background.
    /// </summary>
    [ObservableProperty]
    public partial bool PreparesNames { get; set; } = true;

    /// <summary>Shown only when saving that choice failed; the choice still applies until the application closes.</summary>
    [ObservableProperty]
    public partial string? PrepareNamesStatus { get; private set; }

    /// <summary>The recommendation choices are offered (requirements §10.3; the page is built with them).</summary>
    public bool CanChooseRecommendations => _recommendationSettings is not null;

    /// <summary>新探索推荐问题 (on unless turned off).</summary>
    [ObservableProperty]
    public partial bool RecommendsQuestions { get; set; } = true;

    /// <summary>Mindweave 推荐节点 (on unless turned off).</summary>
    [ObservableProperty]
    public partial bool RecommendsNodes { get; set; } = true;

    /// <summary>自动准备推荐 (on unless turned off): off, recommendations are made only when asked for.</summary>
    [ObservableProperty]
    public partial bool AutoPreparesRecommendations { get; set; } = true;

    /// <summary>Shown only when saving one of those choices failed; the choice still applies until the application closes.</summary>
    [ObservableProperty]
    public partial string? RecommendationsStatus { get; private set; }

    partial void OnRecommendsQuestionsChanged(bool value) => ChooseRecommendation(nameof(RecommendsQuestions), value, (s, on) => s.SetQuestionsAsync(on, CancellationToken.None));

    partial void OnRecommendsNodesChanged(bool value) => ChooseRecommendation(nameof(RecommendsNodes), value, (s, on) => s.SetNodesAsync(on, CancellationToken.None));

    partial void OnAutoPreparesRecommendationsChanged(bool value) =>
        ChooseRecommendation(nameof(AutoPreparesRecommendations), value, (s, on) => s.SetAutoPrepareAsync(on, CancellationToken.None));

    private void ChooseRecommendation(string choice, bool value, Func<IRecommendationSettings, bool, Task> save)
    {
        if (_loading || _recommendationSettings is not { } settings)
        {
            return;
        }

        _chosen.Add(choice);
        SaveInBackground(() => save(settings, value), failed => RecommendationsStatus = failed ? UiText.RecommendationsSaveFailed : null);
    }

    /// <summary>How far the page is scrolled; the view keeps it current, so a language switch can carry it over.</summary>
    public double ScrollOffset { get; set; }

    /// <summary>A carried scroll position the view applies once it can, then clears.</summary>
    public double? PendingScroll { get; set; }

    /// <summary>A section a link asked for (a failure's fix, D2 §2.1); the view scrolls to it once laid out, then clears.</summary>
    public Navigation.SettingsSection? PendingSection { get; set; }

    /// <summary>What lives only in this page, carried over a language switch (bilingual-ui.md §6.6).</summary>
    private sealed record CarriedState(object? Model, object? WebSearch, object? Mcp, double Scroll, bool LanguageChosen);

    public object? CaptureViewState() =>
        new CarriedState(Model.CaptureViewState(), Tools?.Web?.CaptureViewState(), Tools?.Mcp?.CaptureViewState(), ScrollOffset, _languageChosen);

    public void RestoreViewState(object state)
    {
        if (state is not CarriedState carried)
        {
            return;
        }

        if (carried.Model is { } model)
        {
            Model.RestoreViewState(model);
        }

        if (carried.WebSearch is { } key)
        {
            Tools?.Web?.RestoreViewState(key);
        }

        if (carried.Mcp is { } form)
        {
            Tools?.Mcp?.RestoreViewState(form);
        }

        ScrollOffset = carried.Scroll;
        PendingScroll = carried.Scroll > 0 ? carried.Scroll : null;
        FocusesLanguage = carried.LanguageChosen;
    }

    [RelayCommand]
    private void ShowStorageDetails() => _content.Push(StorageDetailsRoute.Instance);

    /// <summary>Token use and activity facts (2026-11-12), a drill-in beside 数据与存储.</summary>
    [RelayCommand]
    private void ShowStatistics() => _content.Push(UsageStatisticsRoute.Instance);

    /// <summary>Every workspace in one place (2026-11-10): listed, switched to, renamed and deleted there.</summary>
    [RelayCommand]
    private void ShowWorkspaces() => _content.Push(WorkspacesRoute.Instance);

    public void Dispose()
    {
        if (_workspaces is not null)
        {
            _workspaces.Workspaces.CollectionChanged -= OnWorkspacesChanged;
        }

        // 设置 › 工具 › MCP 服务器 listens to the servers' state and settings (phase 4).
        Tools?.Mcp?.Dispose();
        if (_language is not null)
        {
            _language.Changed -= OnLanguageSwitchChanged;
        }
    }

    private void OnWorkspacesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(WorkspaceCount));
        OnPropertyChanged(nameof(WorkspaceCountText));
        OnPropertyChanged(nameof(ManageWorkspacesName));
    }

    partial void OnAppearanceChanged(Appearance value)
    {
        if (_loading)
        {
            return;
        }

        _chosen.Add(nameof(Appearance));
        _theme.Apply(value);
        SaveInBackground(() => _appearanceService.SetAsync(value, CancellationToken.None), failed => AppearanceStatus = failed ? UiText.AppearanceSaveFailed : null);
    }

    partial void OnPlaysNewsSoundChanged(bool value)
    {
        if (_loading || _newsAlerts is not { } alerts)
        {
            return;
        }

        _chosen.Add(nameof(PlaysNewsSound));
        SaveInBackground(() => alerts.SetSoundAsync(value, CancellationToken.None), failed => NewsSoundStatus = failed ? UiText.NewsSoundSaveFailed : null);
    }

    partial void OnShowsInWorkspaceNewsChanged(bool value)
    {
        if (_loading || _newsAlerts is not { } alerts)
        {
            return;
        }

        _chosen.Add(nameof(ShowsInWorkspaceNews));
        SaveInBackground(() => alerts.SetInWorkspaceAsync(value, CancellationToken.None), failed => InWorkspaceNewsStatus = failed ? UiText.NewsInWorkspaceSaveFailed : null);
    }

    partial void OnPreparesNamesChanged(bool value)
    {
        if (_loading || _newsAlerts is not { } alerts)
        {
            return;
        }

        _chosen.Add(nameof(PreparesNames));
        SaveInBackground(() => alerts.SetPrepareNamesAsync(value, CancellationToken.None), failed => PrepareNamesStatus = failed ? UiText.PrepareNamesSaveFailed : null);
    }

    /// <summary>
    /// Saves a choice in the background, one write after another, so the last choice made is the one that stays. A
    /// failed write is logged and reported beside the setting; the choice still applies until the application closes.
    /// </summary>
    private void SaveInBackground(Func<Task> write, Action<bool> reportFailed, [CallerMemberName] string choice = "") =>
        Saving = SaveAfterAsync(Saving, write, reportFailed, choice);

    private static async Task SaveAfterAsync(Task previous, Func<Task> write, Action<bool> reportFailed, string choice)
    {
        await previous;
        try
        {
            await write();
            reportFailed(false);
        }
#pragma warning disable CA1031 // A choice that could not be saved is reported beside the setting; it still applies for this run.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed, choice);
            reportFailed(true);
        }
    }

    /// <summary>A stored choice; unreadable means its default, as at startup.</summary>
    private static async Task<T> ReadAsync<T>(Func<Task<T>> read, T fallback, string choice)
    {
        try
        {
            return await read();
        }
#pragma warning disable CA1031 // Unreadable means the default, as at startup.
        catch (Exception swallowed)
#pragma warning restore CA1031
        {
            DesktopLog.Swallowed(swallowed, "Load" + choice);
            return fallback;
        }
    }

    private void Choose(bool chosen, Appearance appearance)
    {
        if (chosen)
        {
            _chosen.Add(nameof(Appearance));
            Appearance = appearance;
        }
    }

    private async Task LoadAsync()
    {
        var stored = await ReadAsync(() => _appearanceService.GetAsync(CancellationToken.None), Appearance.FollowSystem, nameof(Appearance));
        var boldColour = await ReadAsync(() => _appearanceService.GetBoldColourAsync(CancellationToken.None), BoldColour.Brown, nameof(BoldColour));

        // Unreadable means the defaults: the sound off, the other two on.
        var sound = false;
        var inWorkspace = true;
        var prepareNames = true;
        if (_newsAlerts is { } alerts)
        {
            sound = await ReadAsync(() => alerts.GetSoundAsync(CancellationToken.None), false, nameof(PlaysNewsSound));
            inWorkspace = await ReadAsync(() => alerts.GetInWorkspaceAsync(CancellationToken.None), true, nameof(ShowsInWorkspaceNews));
            prepareNames = await ReadAsync(() => alerts.GetPrepareNamesAsync(CancellationToken.None), true, nameof(PreparesNames));
        }

        bool? questions = null, nodes = null, auto = null;
        if (_recommendationSettings is { } recommendations)
        {
            questions = await ReadAsync(() => recommendations.GetQuestionsAsync(CancellationToken.None), true, nameof(RecommendsQuestions));
            nodes = await ReadAsync(() => recommendations.GetNodesAsync(CancellationToken.None), true, nameof(RecommendsNodes));
            auto = await ReadAsync(() => recommendations.GetAutoPrepareAsync(CancellationToken.None), true, nameof(AutoPreparesRecommendations));
        }

        // A choice the reader made while this was loading wins over the stored one it would otherwise overwrite.
        _loading = true;
        if (questions is { } q && Unchosen(nameof(RecommendsQuestions)))
        {
            RecommendsQuestions = q;
        }

        if (nodes is { } n && Unchosen(nameof(RecommendsNodes)))
        {
            RecommendsNodes = n;
        }

        if (auto is { } a && Unchosen(nameof(AutoPreparesRecommendations)))
        {
            AutoPreparesRecommendations = a;
        }

        if (Unchosen(nameof(Appearance)))
        {
            Appearance = stored;
        }

        if (Unchosen(nameof(BoldColour)))
        {
            BoldColour = boldColour;
        }

        if (Unchosen(nameof(PlaysNewsSound)))
        {
            PlaysNewsSound = sound;
        }

        if (Unchosen(nameof(ShowsInWorkspaceNews)))
        {
            ShowsInWorkspaceNews = inWorkspace;
        }

        if (Unchosen(nameof(PreparesNames)))
        {
            PreparesNames = prepareNames;
        }

        _loading = false;
    }

    private bool Unchosen(string choice) => !_chosen.Contains(choice);
}

