using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Ai;
using Mindweave.Application.Credentials;
using Mindweave.Application.Models;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// The single active connection and model. The key field is write-only: a saved key is never shown again,
/// and leaving the field empty keeps it. Fetching models and the (possibly billed) test are separate,
/// explicit actions. A provider preset fills the address; the fetched models are offered as suggestions under the
/// model field, which still takes any typed name.
/// </summary>
public sealed partial class ModelSettingsViewModel : ViewModelBase
{
    private readonly IModelSettings _settings;
    private bool _applyingPreset;
    private bool _settingModel;

    public ModelSettingsViewModel(IModelSettings settings)
    {
        _settings = settings;
        Loaded = LoadAsync();
    }

    internal Task Loaded { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BaseUrl { get; set; } = string.Empty;

    public IReadOnlyList<ProviderPreset> Providers { get; } = ProviderPresets.All;

    /// <summary>
    /// The preset matching the address, <see cref="ProviderPresets.Custom"/> for any other address, null while the
    /// address is empty. Choosing a preset fills its address; editing the address re-matches it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyPlaceholder))]
    public partial ProviderPreset? SelectedProvider { get; set; }

    [ObservableProperty]
    public partial string ApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Model { get; set; } = string.Empty;

    /// <summary>Declared context capacity in tokens; empty means unknown and the default applies.</summary>
    [ObservableProperty]
    public partial string ContextWindow { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyPlaceholder))]
    public partial bool HasSavedKey { get; private set; }

    /// <summary>Shown inside the empty key field: whether a key is saved (leaving it empty keeps it), or what the preset needs.</summary>
    public string KeyPlaceholder =>
        HasSavedKey ? UiText.KeySavedPlaceholder : SelectedProvider?.KeyPlaceholder ?? UiText.KeyMissingPlaceholder;

    /// <summary>Every model the service listed at the last fetch.</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

    /// <summary>The fetched models shown under the model field: all of them when the list opens, narrowed while typing.</summary>
    public ObservableCollection<string> ModelSuggestions { get; } = [];

    [ObservableProperty]
    public partial bool IsModelListOpen { get; set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscoverCommand), nameof(TestCommand))]
    public partial bool IsBusy { get; private set; }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task SaveAsync() => RunAsync(async () =>
    {
        await _settings.SaveAsync(new ModelSettingsInput(Name, BaseUrl, Model, ApiKey, ParseContextWindow(ContextWindow)), CancellationToken.None);
        ApiKey = string.Empty;
        await LoadAsync();
        Report(UiText.SettingsSaved, isError: false);
    });

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DiscoverAsync() => RunAsync(async () =>
    {
        var models = await _settings.DiscoverModelsAsync(BaseUrl, ApiKey, CancellationToken.None);
        AvailableModels.Clear();
        foreach (var model in models)
        {
            AvailableModels.Add(model);
        }

        Report(models.Count == 0 ? UiText.NoModelsFound : UiText.ModelsFound(models.Count), isError: models.Count == 0);

        // Show what was found at once, whatever is already typed; typing narrows it from here.
        OpenModelList();
    });

    /// <summary>Opens the suggestions with every fetched model (on fetch, focus, click or the field's arrow).</summary>
    public void OpenModelList()
    {
        Suggest(AvailableModels);
        IsModelListOpen = ModelSuggestions.Count > 0;
    }

    [RelayCommand]
    private void ChooseModel(string? model)
    {
        if (model is null)
        {
            return;
        }

        _settingModel = true;
        Model = model;
        _settingModel = false;
        IsModelListOpen = false;
    }

    partial void OnModelChanged(string value)
    {
        if (_settingModel || AvailableModels.Count == 0)
        {
            return;
        }

        // Typed text narrows the suggestions; a name that matches none is kept as typed and the list closes.
        var typed = value.Trim();
        Suggest(typed.Length == 0 ? AvailableModels : AvailableModels.Where(m => m.Contains(typed, StringComparison.OrdinalIgnoreCase)));
        IsModelListOpen = ModelSuggestions.Count > 0 && !(ModelSuggestions.Count == 1 && ModelSuggestions[0] == typed);
    }

    partial void OnBaseUrlChanged(string value)
    {
        if (!_applyingPreset)
        {
            SelectedProvider = value.Trim().Length == 0 ? null : ProviderPresets.Match(value) ?? ProviderPresets.Custom;
        }
    }

    partial void OnSelectedProviderChanged(ProviderPreset? value)
    {
        if (value?.BaseUrl is not { } url || ProviderPresets.Match(BaseUrl) == value)
        {
            return;
        }

        // A name that was empty, another preset's name or the old address's host (the saved default) follows the preset.
        var trimmed = Name.Trim();
        var followsPreset = trimmed.Length == 0 || ProviderPresets.All.Any(p => p.Name == trimmed)
            || (Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var old) && string.Equals(old.Host, trimmed, StringComparison.OrdinalIgnoreCase));

        _applyingPreset = true;
        BaseUrl = url;
        _applyingPreset = false;

        // Models fetched from another service do not belong to this one.
        AvailableModels.Clear();
        ModelSuggestions.Clear();
        IsModelListOpen = false;
        if (followsPreset)
        {
            Name = value.Name;
        }
    }

    private void Suggest(IEnumerable<string> models)
    {
        var list = models.ToList();
        if (list.SequenceEqual(ModelSuggestions))
        {
            return;
        }

        ModelSuggestions.Clear();
        foreach (var model in list)
        {
            ModelSuggestions.Add(model);
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task TestAsync() => RunAsync(async () =>
    {
        var result = await _settings.TestAsync(BaseUrl, Model, ApiKey, CancellationToken.None);
        Report(result.Succeeded ? UiText.TestSucceeded : UiText.TestFailed(result.Error, result.Detail), isError: !result.Succeeded);
    });

    private bool IsIdle() => !IsBusy;

    private async Task LoadAsync()
    {
        var saved = await _settings.GetAsync(CancellationToken.None);
        if (saved is null)
        {
            return;
        }

        Name = saved.ConnectionName;
        BaseUrl = saved.BaseUrl;
        _settingModel = true;
        Model = saved.Model;
        _settingModel = false;
        ContextWindow = saved.Capabilities.ContextWindowTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        HasSavedKey = true;
    }

    private static int? ParseContextWindow(string text)
    {
        var digits = text.Replace(",", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).Trim();
        if (digits.Length == 0)
        {
            return null;
        }

        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var tokens)
            ? tokens
            : throw new ModelSettingsException(ModelSettingsProblem.InvalidContextWindow);
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Status = null;
        try
        {
            await action();
        }
        catch (ModelSettingsException ex)
        {
            Report(UiText.SettingsProblem(ex.Problem), isError: true);
        }
        catch (ChatClientException ex)
        {
            Report(UiText.TestFailed(ex.Kind, ex.Message), isError: true);
        }
        catch (CredentialUnavailableException ex)
        {
            Report(UiText.CredentialUnavailable(ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Report(string message, bool isError)
    {
        Status = message;
        StatusIsError = isError;
    }
}
