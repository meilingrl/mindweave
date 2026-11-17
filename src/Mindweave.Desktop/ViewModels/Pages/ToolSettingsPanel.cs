using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Models;
using Mindweave.Application.Preferences;
using Mindweave.Core.Models;
using Mindweave.Core.Runs;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// 设置 › 工具 (requirements §10.4.4): the 使用工具 switch and a run's four limits. Greyed out with the reason while the model
/// does not declare tool calls; a change applies to questions asked afterwards (a run keeps what it was accepted with).
/// </summary>
public sealed partial class ToolSettingsPanel : ViewModelBase
{
    private readonly IToolSettings _settings;
    private readonly IModelSettings _models;
    private bool _loading = true;

    public ToolSettingsPanel(IToolSettings settings, IModelSettings models)
    {
        _settings = settings;
        _models = models;
        Loaded = LoadAsync();
    }

    /// <summary>Completes when the stored values are shown.</summary>
    public Task Loaded { get; private set; }

    /// <summary>The model declares tool calls: the section can be used.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnavailable), nameof(CanEditLimits))]
    public partial bool IsAvailable { get; private set; }

    public bool IsUnavailable => !IsAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditLimits))]
    public partial bool UsesTools { get; set; } = true;

    public bool CanEditLimits => IsAvailable && UsesTools;

    [ObservableProperty]
    public partial decimal? ModelRequests { get; set; } = RunLimits.Default.ModelRequests;

    [ObservableProperty]
    public partial decimal? ToolCalls { get; set; } = RunLimits.Default.ToolCalls;

    [ObservableProperty]
    public partial decimal? Minutes { get; set; } = (decimal)RunLimits.Default.Duration.TotalMinutes;

    [ObservableProperty]
    public partial decimal? Tokens { get; set; } = RunLimits.Default.Tokens;

    /// <summary>After a change: saved, or why not.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    public static decimal MinModelRequests => RunLimits.MinModelRequests;

    public static decimal MaxModelRequests => RunLimits.MaxModelRequests;

    public static decimal MinToolCalls => RunLimits.MinToolCalls;

    public static decimal MaxToolCalls => RunLimits.MaxToolCalls;

    public static decimal MinMinutes => RunLimits.MinMinutes;

    public static decimal MaxMinutes => RunLimits.MaxMinutes;

    public static decimal MinTokens => RunLimits.MinTokens;

    public static decimal MaxTokens => RunLimits.MaxTokens;

    /// <summary>Reads the model's capability again (after the model settings were saved).</summary>
    public async Task RefreshAvailabilityAsync() =>
        IsAvailable = await _models.ActiveModelHasAsync(ModelCapability.ToolCalls, CancellationToken.None);

    [RelayCommand]
    private Task RestoreDefaultsAsync()
    {
        _loading = true;
        Show(RunLimits.Default);
        _loading = false;
        return SaveLimitsAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            await RefreshAvailabilityAsync();
            UsesTools = await _settings.GetEnabledAsync(CancellationToken.None);
            Show(await _settings.GetLimitsAsync(CancellationToken.None));
        }
        finally
        {
            _loading = false;
        }
    }

    private void Show(RunLimits limits)
    {
        ModelRequests = limits.ModelRequests;
        ToolCalls = limits.ToolCalls;
        Minutes = (decimal)limits.Duration.TotalMinutes;
        Tokens = limits.Tokens;
    }

    partial void OnUsesToolsChanged(bool value)
    {
        if (!_loading)
        {
            _ = SaveAsync(() => _settings.SetEnabledAsync(value, CancellationToken.None));
        }
    }

    partial void OnModelRequestsChanged(decimal? value) => LimitChanged();

    partial void OnToolCallsChanged(decimal? value) => LimitChanged();

    partial void OnMinutesChanged(decimal? value) => LimitChanged();

    partial void OnTokensChanged(decimal? value) => LimitChanged();

    private void LimitChanged()
    {
        if (!_loading && ModelRequests is not null && ToolCalls is not null && Minutes is not null && Tokens is not null)
        {
            _ = SaveLimitsAsync();
        }
    }

    private Task SaveLimitsAsync() => SaveAsync(async () =>
    {
        var limits = new RunLimits((int)ModelRequests!.Value, (int)ToolCalls!.Value, TimeSpan.FromMinutes((double)Minutes!.Value), (int)Tokens!.Value);
        await _settings.SetLimitsAsync(limits, CancellationToken.None);
    });

    private async Task SaveAsync(Func<Task> save)
    {
        try
        {
            await save();
            (Status, StatusIsError) = (UiText.LimitsSaved, false);
        }
#pragma warning disable CA1031 // A preference that could not be written is said so on the page; the app goes on.
        catch (Exception)
#pragma warning restore CA1031
        {
            (Status, StatusIsError) = (UiText.ToolSettingsSaveFailed, true);
        }
    }
}
