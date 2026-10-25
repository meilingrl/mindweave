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
/// explicit actions.
/// </summary>
public sealed partial class ModelSettingsViewModel : ViewModelBase
{
    private readonly ModelSettingsService _settings;

    public ModelSettingsViewModel(ModelSettingsService settings)
    {
        _settings = settings;
        Loaded = LoadAsync();
    }

    internal Task Loaded { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Model { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyHint))]
    public partial bool HasSavedKey { get; private set; }

    public string KeyHint => HasSavedKey ? UiText.KeySavedHint : UiText.KeyMissingHint;

    public ObservableCollection<string> AvailableModels { get; } = [];

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
        await _settings.SaveAsync(new ModelSettingsInput(Name, BaseUrl, Model, ApiKey), CancellationToken.None);
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
    });

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
        Model = saved.Model;
        HasSavedKey = true;
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
