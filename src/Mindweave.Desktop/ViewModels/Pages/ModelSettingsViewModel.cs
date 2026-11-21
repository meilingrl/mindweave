using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mindweave.Application.Ai;
using Mindweave.Application.Credentials;
using Mindweave.Application.Models;
using Mindweave.Application.Storage;
using Mindweave.Core.Models;

namespace Mindweave.Desktop.ViewModels.Pages;

/// <summary>
/// The single active connection and model. The key field is write-only: a saved key is never shown again,
/// and leaving the field empty keeps it. Fetching models and the (possibly billed) test are separate,
/// explicit actions. A provider preset fills the address; the fetched models are offered as suggestions under the
/// model field, which still takes any typed name. The saved key belongs to the saved service: once the address points
/// at another host the key field asks for that service's key (the service refuses to send the old one there too).
/// </summary>
public sealed partial class ModelSettingsViewModel : ViewModelBase, Navigation.ICarriesViewState
{
    private readonly IModelSettings _settings;
    private bool _applyingPreset;
    private bool _settingModel;
    private bool _loading;
    private ModelSettings? _saved;

    /// <summary>
    /// The capabilities the form declares for the model as typed: the saved ones for the saved model, otherwise the
    /// preset's suggestion. The two flags shown (<see cref="DeclaresToolCalls"/>, <see cref="DeclaresImageInput"/>) are
    /// laid over it on save; the flags not shown ride along unchanged.
    /// </summary>
    private ModelCapabilities _capabilities = ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions);

    /// <summary>The address the fetched models came from; a different host drops them.</summary>
    private string? _modelsFrom;

    public ModelSettingsViewModel(IModelSettings settings)
    {
        _settings = settings;
        Loaded = LoadAsync();
    }

    internal Task Loaded { get; private set; }

    /// <summary>The settings were saved (first-run onboarding moves on to its next step).</summary>
    public event EventHandler? Saved;

    /// <summary>
    /// Shown again (health check 2026-11-12, ui-vm-3): the settings are the application's, and another workspace's
    /// settings page may have saved new ones. The form is read again unless the reader has changed it since it was read.
    /// </summary>
    public void ReloadIfUnchanged()
    {
        if (IsBusy || ApiKey.Length > 0 || (Name, BaseUrl, Model, ContextWindow, OutputLimit, DeclaresToolCalls, DeclaresImageInput) != _shownValues)
        {
            return;
        }

        Loaded = LoadAsync();
    }

    /// <summary>What the reader typed and has not saved, carried over a language switch (bilingual-ui.md §6.6).</summary>
    private sealed record Input(
        string Name,
        string BaseUrl,
        string Model,
        string ApiKey,
        string ContextWindow,
        string OutputLimit,
        bool ToolCalls,
        bool ImageInput,
        bool AdvancedOpen,
        ModelCapabilities Capabilities,
        string? ModelsFrom,
        IReadOnlyList<string> Models);

    /// <summary>The unsaved input, or null when the form shows what was last read and nothing more.</summary>
    public object? CaptureViewState() =>
        !IsAdvancedOpen && ApiKey.Length == 0 && AvailableModels.Count == 0
            && (Name, BaseUrl, Model, ContextWindow, OutputLimit, DeclaresToolCalls, DeclaresImageInput) == _shownValues
            ? null
            : new Input(Name, BaseUrl, Model, ApiKey, ContextWindow, OutputLimit, DeclaresToolCalls, DeclaresImageInput, IsAdvancedOpen, _capabilities, _modelsFrom, [.. AvailableModels]);

    /// <summary>Puts the carried input back once the stored settings have been read, so the read does not overwrite it.</summary>
    public void RestoreViewState(object state)
    {
        if (state is Input input)
        {
            Loaded = RestoreAsync(Loaded, input);
        }
    }

    private async Task RestoreAsync(Task loading, Input input)
    {
        await loading;
        _loading = true;
        _settingModel = true;
        Name = input.Name;
        BaseUrl = input.BaseUrl;
        Model = input.Model;
        _settingModel = false;
        _loading = false;
        ApiKey = input.ApiKey;
        ContextWindow = input.ContextWindow;
        OutputLimit = input.OutputLimit;
        _capabilities = input.Capabilities;
        DeclaresToolCalls = input.ToolCalls;
        DeclaresImageInput = input.ImageInput;
        IsAdvancedOpen = input.AdvancedOpen;
        AvailableModels.Clear();
        foreach (var model in input.Models)
        {
            AvailableModels.Add(model);
        }

        _modelsFrom = input.ModelsFrom;
    }

    /// <summary>The values the form was filled with by the last read; changed fields mean the reader is editing.</summary>
    private (string Name, string BaseUrl, string Model, string ContextWindow, string OutputLimit, bool ToolCalls, bool ImageInput) _shownValues =
        (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, false);

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyPlaceholder), nameof(SavedKeyApplies))]
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

    /// <summary>The answer output limit in tokens (高级设置); empty sends none and the service decides.</summary>
    [ObservableProperty]
    public partial string OutputLimit { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyPlaceholder), nameof(SavedKeyApplies))]
    public partial bool HasSavedKey { get; private set; }

    /// <summary>The advanced part of the form (the model's capabilities) is open; closed on every visit.</summary>
    [ObservableProperty]
    public partial bool IsAdvancedOpen { get; set; }

    /// <summary>
    /// 工具调用: the model may be offered tools. Filled from the preset for the service and model, and filled again
    /// whenever either changes; the user may override it for the model shown.
    /// </summary>
    [ObservableProperty]
    public partial bool DeclaresToolCalls { get; set; }

    /// <summary>图片输入: images may be sent to the model. Filled and overridden like <see cref="DeclaresToolCalls"/>.</summary>
    [ObservableProperty]
    public partial bool DeclaresImageInput { get; set; }

    /// <summary>A key is saved and the address is still the service it was saved for, so an empty key field keeps it.</summary>
    public bool SavedKeyApplies => HasSavedKey && ServiceAddress.SameService(_saved?.BaseUrl, BaseUrl);

    /// <summary>
    /// Shown inside the empty key field: a saved key that still applies (leaving it empty keeps it), the key another
    /// service needs, or what the preset needs.
    /// </summary>
    public string KeyPlaceholder =>
        SavedKeyApplies ? UiText.KeySavedPlaceholder
        : SelectedProvider?.KeyPlaceholder ?? (HasSavedKey ? UiText.KeyOtherServicePlaceholder : UiText.KeyMissingPlaceholder);

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
        var declared = _capabilities
            .With(ModelCapability.ToolCalls, DeclaresToolCalls)
            .With(ModelCapability.ImageInput, DeclaresImageInput);
        await _settings.SaveAsync(new ModelSettingsInput(
                Name,
                BaseUrl,
                Model,
                ApiKey,
                ParseTokens(ContextWindow, ModelSettingsProblem.InvalidContextWindow),
                declared,
                ParseTokens(OutputLimit, ModelSettingsProblem.InvalidOutputLimit)), CancellationToken.None);
        ApiKey = string.Empty;
        await LoadAsync();
        Report(UiText.SettingsSaved, isError: false);
        Saved?.Invoke(this, EventArgs.Empty);
    });

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DiscoverAsync() => RunAsync(async () =>
    {
        var models = await _settings.DiscoverModelsAsync(BaseUrl, ApiKey, CancellationToken.None);
        _modelsFrom = BaseUrl;
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
        FillCapabilities();
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
        // Models fetched from another host do not belong to this one, however the address changed.
        if (_modelsFrom is not null && !ServiceAddress.SameService(_modelsFrom, value))
        {
            DropModels();
        }

        if (!_applyingPreset)
        {
            SelectedProvider = value.Trim().Length == 0 ? null : ProviderPresets.Match(value) ?? ProviderPresets.Custom;
        }

        FillCapabilities();
    }

    /// <summary>
    /// Shows what the model as now typed can do: what was saved for it when it is the saved model on the saved service and
    /// was ever declared, otherwise the preset's suggestion for the service and model. Unknown is off. Any override the
    /// user made belonged to the model shown before and is dropped.
    /// </summary>
    private void FillCapabilities()
    {
        if (_loading)
        {
            return;
        }

        var model = Model.Trim();
        _capabilities = _saved is not null && _saved.Model == model && ServiceAddress.SameService(_saved.BaseUrl, BaseUrl)
                                            && WasDeclared(_saved.Capabilities)
            ? _saved.Capabilities
            : ModelCapabilityPresets.Suggest(BaseUrl, model);
        DeclaresToolCalls = _capabilities.ToolCalls;
        DeclaresImageInput = _capabilities.ImageInput;
    }

    /// <summary>
    /// Settings saved before capabilities could be declared hold the bare protocol defaults; those are not a statement
    /// about the model, so the preset's suggestion is shown instead (and saved with the next save).
    /// </summary>
    private static bool WasDeclared(ModelCapabilities saved) =>
        saved with { ContextWindowTokens = null } != ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions);

    private void DropModels()
    {
        _modelsFrom = null;
        AvailableModels.Clear();
        ModelSuggestions.Clear();
        IsModelListOpen = false;
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

        // Only a real address of another host counts; filling an empty field keeps whatever was typed beside it.
        var otherService = Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out _) && !ServiceAddress.SameService(BaseUrl, url);
        _applyingPreset = true;
        BaseUrl = url;
        _applyingPreset = false;

        DropModels();
        if (followsPreset)
        {
            Name = value.Name;
        }

        if (otherService)
        {
            // A typed key and a model name belong to the service they were typed for; the saved model comes back when
            // the preset returns to the saved service.
            ApiKey = string.Empty;
            _settingModel = true;
            Model = _saved is not null && ServiceAddress.SameService(_saved.BaseUrl, url) ? _saved.Model : string.Empty;
            _settingModel = false;
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

        _saved = saved;
        _loading = true;
        Name = saved.ConnectionName;
        BaseUrl = saved.BaseUrl;
        _settingModel = true;
        Model = saved.Model;
        _settingModel = false;
        _loading = false;
        FillCapabilities();
        ContextWindow = saved.Capabilities.ContextWindowTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        OutputLimit = saved.MaxOutputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        _shownValues = (Name, BaseUrl, Model, ContextWindow, OutputLimit, DeclaresToolCalls, DeclaresImageInput);
        HasSavedKey = await _settings.SavedKeyExistsAsync(CancellationToken.None);
        if (!HasSavedKey)
        {
            // A restored backup can point at a key replaced and deleted since: say so instead of "saved".
            Report(UiText.SavedKeyMissing, isError: true);
        }

        OnPropertyChanged(nameof(SavedKeyApplies));
        OnPropertyChanged(nameof(KeyPlaceholder));
    }

    private static int? ParseTokens(string text, ModelSettingsProblem invalid)
    {
        var digits = text.Replace(",", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).Trim();
        if (digits.Length == 0)
        {
            return null;
        }

        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var tokens)
            ? tokens
            : throw new ModelSettingsException(invalid);
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
        catch (StorageWriteException)
        {
            // Health check 2026-11-12: nothing was stored; what was typed stays on the page.
            Report(UiText.RowStoragePaused, isError: true);
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
