using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Models;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// S7 round 3, settings: the appearance is one segmented control; a provider preset fills the address; fetched
/// models show at once under the model field and narrow while typing, any typed name is kept; one primary per page.
/// </summary>
public class SettingsPolishTests
{
    [AvaloniaFact]
    public async Task Appearance_is_one_segmented_control_that_applies_on_click_and_with_the_arrow_keys()
    {
        await using var app = DesktopHarness.Create();
        var (window, settings) = await OpenSettingsAsync(app);

        var picker = Find<Border>(window, "AppearancePicker");
        Assert.Contains("segmented", picker.Classes);
        var segments = picker.GetVisualDescendants().OfType<RadioButton>().ToList();
        Assert.Equal(["AppearanceLight", "AppearanceDark", "AppearanceFollowSystem"], segments.Select(s => s.Name));
        Assert.All(segments, s => Assert.Contains("segment", s.Classes));
        Assert.All(segments, s => Assert.NotNull(s.GetVisualDescendants().OfType<PathIcon>().SingleOrDefault()));

        // A real click on 深色: applied at once; the chosen segment is filled, the others are not.
        UiInput.Click(window, segments[1]);
        Assert.Equal(Appearance.Dark, app.Theme.Applied.Last());
        Assert.True(settings.IsDark);
        await settings.Saving;
        Assert.Equal(Colour(window, "SegmentedSelected"), SegmentFill(segments[1]));
        Assert.NotEqual(Colour(window, "SegmentedSelected"), SegmentFill(segments[0]));
        Assert.Equal(Colors.Transparent, SegmentFill(segments[2]));

        // Arrow keys move the choice along the control (and stop at its ends).
        Assert.True(segments[1].IsFocused);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.True(settings.IsFollowSystem);
        Assert.True(segments[2].IsFocused);
        Assert.Equal(Appearance.FollowSystem, app.Theme.Applied.Last());
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.True(settings.IsFollowSystem);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.True(settings.IsLight);
        Assert.Equal(Appearance.Light, app.Theme.Applied.Last());
        await settings.Saving;
        window.Close();
    }

    [Fact]
    public void Every_preset_is_a_secure_chat_completions_base_that_matches_itself()
    {
        var addresses = ProviderPresets.All.Where(p => !p.IsCustom).Select(p => p.BaseUrl!).ToList();
        Assert.Equal(addresses.Count, addresses.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var preset in ProviderPresets.All.Where(p => !p.IsCustom))
        {
            // Saved exactly as filled (no trailing slash), https except a service on this machine.
            Assert.Equal(preset.BaseUrl, ModelSettingsService.NormalizeBaseUrl(preset.BaseUrl!));
            var uri = new Uri(preset.BaseUrl!);
            Assert.True(uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback, preset.Name);
            Assert.Same(preset, ProviderPresets.Match(preset.BaseUrl));
            Assert.Same(preset, ProviderPresets.Match(preset.BaseUrl + "/"));
            Assert.Same(preset, ProviderPresets.Match(" " + preset.BaseUrl!.Replace(uri.Host, uri.Host.ToUpperInvariant(), StringComparison.Ordinal)));
        }

        Assert.Same(ProviderPresets.DeepSeek, ProviderPresets.Match("https://api.deepseek.com/v1"));
        Assert.Null(ProviderPresets.Match("https://fake.example/v1"));
        Assert.Null(ProviderPresets.Match("https://api.openai.com/v2"));
        Assert.Null(ProviderPresets.Match(string.Empty));
        Assert.Same(ProviderPresets.Custom, ProviderPresets.All[^1]);
    }

    [AvaloniaFact]
    public async Task Choosing_a_provider_fills_the_address_and_editing_the_address_switches_to_custom()
    {
        await using var app = DesktopHarness.Create();
        var (window, settings) = await OpenSettingsAsync(app);
        var model = settings.Model;
        var picker = Find<ComboBox>(window, "ProviderPicker");
        var address = Find<TextBox>(window, "BaseUrl");
        Assert.Null(model.SelectedProvider);
        Assert.Equal(UiText.KeyMissingPlaceholder, Find<TextBox>(window, "ApiKey").PlaceholderText);

        picker.SelectedItem = ProviderPresets.DeepSeek;
        window.UpdateLayout();
        Assert.Equal("https://api.deepseek.com", model.BaseUrl);
        Assert.Equal("https://api.deepseek.com", address.Text);
        Assert.Equal("DeepSeek", model.Name);

        // Typing in the address: no longer the preset's.
        address.Focus();
        address.CaretIndex = address.Text!.Length;
        window.KeyTextInput("/beta");
        Assert.Equal("https://api.deepseek.com/beta", model.BaseUrl);
        Assert.Same(ProviderPresets.Custom, model.SelectedProvider);
        Assert.Same(ProviderPresets.Custom, picker.SelectedItem);

        // 自定义 keeps whatever is typed.
        picker.SelectedItem = ProviderPresets.Custom;
        Assert.Equal("https://api.deepseek.com/beta", model.BaseUrl);

        // Another preset replaces the address and the name it filled, and drops models fetched from the old service.
        model.ApiKey = "sk-1";
        await model.DiscoverCommand.ExecuteAsync(null);
        Assert.NotEmpty(model.AvailableModels);
        picker.SelectedItem = ProviderPresets.Ollama;
        Assert.Equal("http://localhost:11434/v1", model.BaseUrl);
        Assert.Equal(UiText.ProviderOllama, model.Name);
        Assert.Empty(model.AvailableModels);
        Assert.False(model.IsModelListOpen);
        Assert.Equal(UiText.OllamaKeyPlaceholder, Find<TextBox>(window, "ApiKey").PlaceholderText);

        // A name the reader typed is not replaced.
        model.Name = "我的服务";
        picker.SelectedItem = ProviderPresets.OpenRouter;
        Assert.Equal("我的服务", model.Name);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Saved_settings_load_into_the_matching_provider_or_custom()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        var service = app.Services.GetRequiredService<IModelSettings>();

        var empty = new ModelSettingsViewModel(service);
        await empty.Loaded;
        Assert.Null(empty.SelectedProvider);

        await service.SaveAsync(new ModelSettingsInput(string.Empty, "https://API.OpenAI.com/v1/", "m", "sk-1"), CancellationToken.None);
        var openai = new ModelSettingsViewModel(service);
        await openai.Loaded;
        Assert.Same(ProviderPresets.OpenAI, openai.SelectedProvider);
        Assert.Equal("m", openai.Model);
        Assert.Equal(UiText.KeySavedPlaceholder, openai.KeyPlaceholder);

        await app.ConfigureModelAsync();
        var custom = new ModelSettingsViewModel(service);
        await custom.Loaded;
        Assert.Same(ProviderPresets.Custom, custom.SelectedProvider);
        Assert.Equal("https://fake.example/v1", custom.BaseUrl);
    }

    [AvaloniaFact]
    public async Task Fetched_models_show_at_once_typing_narrows_them_and_a_typed_name_is_kept()
    {
        await using var app = DesktopHarness.Create();
        app.Client.Models = ["deepseek-chat", "deepseek-reasoner", "deepseek-coder"];
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var (window, settings) = await OpenSettingsAsync(app, started: true);
        var model = settings.Model;
        var popup = Find<Popup>(window, "ModelPopup");
        Assert.False(popup.IsOpen);

        // Without typing anything — the field still says fake-model, which matches none of them.
        UiInput.Click(window, Find<Button>(window, "FetchModels"));
        await UiWait.UntilAsync(() => !model.IsBusy);
        window.UpdateLayout();
        Assert.True(popup.IsOpen);
        Assert.Equal(["deepseek-chat", "deepseek-reasoner", "deepseek-coder"], Suggestions(popup));
        Assert.Equal("fake-model", model.Model);

        // Typing narrows the list; arrow down and Enter choose.
        var box = Find<TextBox>(window, "ModelBox");
        box.Focus();
        box.SelectAll();
        window.KeyTextInput("reason");
        window.UpdateLayout();
        Assert.True(popup.IsOpen);
        Assert.Equal(["deepseek-reasoner"], Suggestions(popup));
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal("deepseek-reasoner", model.Model);
        Assert.False(popup.IsOpen);

        // A name that is not in the list is kept as typed, and saved.
        box.SelectAll();
        window.KeyTextInput("my-own-model");
        window.UpdateLayout();
        Assert.False(popup.IsOpen);
        Assert.Equal("my-own-model", model.Model);
        await model.SaveCommand.ExecuteAsync(null);
        Assert.False(model.StatusIsError);
        Assert.Equal("my-own-model", (await app.Services.GetRequiredService<IModelSettings>().GetAsync(CancellationToken.None))!.Model);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_the_model_field_shows_every_fetched_model_and_clicking_one_chooses_it()
    {
        await using var app = DesktopHarness.Create();
        app.Client.Models = ["alpha", "beta", "gamma"];
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var (window, settings) = await OpenSettingsAsync(app, started: true);
        var model = settings.Model;
        var popup = Find<Popup>(window, "ModelPopup");
        await model.DiscoverCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Assert.True(popup.IsOpen);

        // Escape closes; a click on the field opens the whole list again, whatever it says.
        var box = Find<TextBox>(window, "ModelBox");
        box.Focus();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(popup.IsOpen);
        UiInput.Click(window, box);
        Assert.True(popup.IsOpen);
        Assert.Equal(["alpha", "beta", "gamma"], Suggestions(popup));

        var item = popup.Child!.GetLogicalDescendants().OfType<ListBoxItem>().Single(i => (string?)i.Content == "gamma");
        var root = TopLevel.GetTopLevel(item)!;
        var centre = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), root)!.Value;
        root.MouseDown(centre, MouseButton.Left);
        root.MouseUp(centre, MouseButton.Left);
        Assert.Equal("gamma", model.Model);
        Assert.False(popup.IsOpen);

        // The arrow inside the field opens it too.
        UiInput.Click(window, Find<Button>(window, "ModelListToggle"));
        Assert.True(popup.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Switching_to_another_provider_never_sends_the_saved_key_to_the_new_host()
    {
        await using var app = DesktopHarness.Create();
        app.Client.Models = ["deepseek-chat"];
        await app.StartWithWorkspaceAsync("火车");
        var service = app.Services.GetRequiredService<IModelSettings>();
        await service.SaveAsync(new ModelSettingsInput("DeepSeek", "https://api.deepseek.com", "deepseek-chat", "sk-deepseek"), CancellationToken.None);
        var (window, settings) = await OpenSettingsAsync(app, started: true);
        var model = settings.Model;
        var keyBox = Find<TextBox>(window, "ApiKey");
        Assert.Same(ProviderPresets.DeepSeek, model.SelectedProvider);
        Assert.Equal(UiText.KeySavedPlaceholder, keyBox.PlaceholderText);

        // A key typed for DeepSeek, then the picker moves to OpenAI: the typed key, the model and the saved key stay behind.
        model.ApiKey = "sk-typed-for-deepseek";
        Find<ComboBox>(window, "ProviderPicker").SelectedItem = ProviderPresets.OpenAI;
        window.UpdateLayout();
        Assert.Equal("https://api.openai.com/v1", model.BaseUrl);
        Assert.Equal(string.Empty, model.ApiKey);
        Assert.Equal(string.Empty, model.Model);
        Assert.False(model.SavedKeyApplies);
        Assert.Equal(UiText.KeyOtherServicePlaceholder, keyBox.PlaceholderText);

        // 获取列表, 测试连接 and 保存 with the key field empty: refused, and nothing is sent anywhere.
        var missingKey = UiText.SettingsProblem(ModelSettingsProblem.MissingKey);
        UiInput.Click(window, Find<Button>(window, "FetchModels"));
        await UiWait.UntilAsync(() => !model.IsBusy);
        Assert.Equal(missingKey, model.Status);
        model.Model = "gpt-4o";
        await model.TestCommand.ExecuteAsync(null);
        Assert.Equal(missingKey, model.Status);
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Equal(missingKey, model.Status);
        Assert.True(model.StatusIsError);
        Assert.Empty(app.Client.KeysSent);
        Assert.Equal("https://api.deepseek.com", (await service.GetAsync(CancellationToken.None))!.BaseUrl);

        // An address typed by hand to another host is refused the same way.
        model.BaseUrl = "https://evil.example/v1";
        Assert.Equal(UiText.KeyOtherServicePlaceholder, keyBox.PlaceholderText);
        await model.DiscoverCommand.ExecuteAsync(null);
        Assert.Equal(missingKey, model.Status);
        Assert.Empty(app.Client.KeysSent);

        // Back on the saved service: its key and model apply again, and fetching uses them there only.
        Find<ComboBox>(window, "ProviderPicker").SelectedItem = ProviderPresets.DeepSeek;
        window.UpdateLayout();
        Assert.Equal("deepseek-chat", model.Model);
        Assert.Equal(UiText.KeySavedPlaceholder, keyBox.PlaceholderText);
        await model.DiscoverCommand.ExecuteAsync(null);
        Assert.False(model.StatusIsError);
        Assert.Equal(("https://api.deepseek.com", "sk-deepseek"), Assert.Single(app.Client.KeysSent));

        // Models fetched there are dropped when the address is edited to another host by hand.
        model.IsModelListOpen = false;
        Assert.NotEmpty(model.AvailableModels);
        model.BaseUrl = "https://api.deepseek.com/v1";
        Assert.NotEmpty(model.AvailableModels);
        model.BaseUrl = "https://other.example/v1";
        Assert.Empty(model.AvailableModels);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_arrow_in_the_model_field_closes_the_list_it_opened()
    {
        var (app, window, model, popup) = await FetchedAsync();
        await using var _ = app;
        var toggle = Find<Button>(window, "ModelListToggle");
        model.IsModelListOpen = false;
        window.UpdateLayout();

        UiInput.Click(window, toggle);
        Assert.True(popup.IsOpen);
        UiInput.Click(window, toggle);
        Assert.False(popup.IsOpen);
        Assert.False(model.IsModelListOpen);

        // Long enough for the arrow's tooltip to open under the resting pointer (as on a slow CI runner): still a click.
        await Task.Delay(1500);
        UiInput.Click(window, toggle);
        Assert.True(popup.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task After_fetching_Escape_closes_the_list_and_leaving_the_field_closes_it()
    {
        var (app, window, model, popup) = await FetchedAsync();
        await using var _ = app;
        var box = Find<TextBox>(window, "ModelBox");

        // Straight after 获取列表 (the click put focus on that button), Escape closes the list.
        Assert.True(popup.IsOpen);
        Assert.True(box.IsFocused);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(popup.IsOpen);

        // Open again, then Tab away to 名称: the list closes instead of covering it.
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.True(popup.IsOpen);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        window.UpdateLayout();
        Assert.False(box.IsFocused);
        Assert.False(popup.IsOpen);

        // Tabbing back into the field shows the list again.
        window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        window.UpdateLayout();
        Assert.True(box.IsFocused);
        Assert.True(popup.IsOpen);

        // A click elsewhere (another field) closes it too.
        UiInput.Click(window, Find<TextBox>(window, "ConnectionName"));
        Assert.False(popup.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_provider_picker_is_framed_like_the_text_fields()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            await using var app = DesktopHarness.Create();
            var (window, _) = await OpenSettingsAsync(app);
            window.RequestedThemeVariant = theme;
            window.UpdateLayout();
            var picker = Find<ComboBox>(window, "ProviderPicker");
            var frame = picker.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Background");
            var field = Find<TextBox>(window, "BaseUrl").GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
            Assert.Equal(Brush(field.BorderBrush), Brush(frame.BorderBrush));
            Assert.Equal(Colour(window, "TextControlBorderBrush"), Brush(frame.BorderBrush));
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Settings_storage_and_onboarding_each_have_one_primary_action()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartAsync();
        window.UpdateLayout();
        // The guide's first step (2026-11-17); every step's own primary is checked in OnboardingTests.
        var onboarding = window.GetVisualDescendants().OfType<OnboardingView>().Single();
        Assert.Equal(["NextButton"], VisiblePrimaries(onboarding));

        var onboardingModel = Assert.IsType<ViewModels.OnboardingViewModel>(app.Root.Current);
        onboardingModel.Form.Name = "火车";
        await onboardingModel.Form.SubmitCommand.ExecuteAsync(null);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        await Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage).Loaded;
        window.UpdateLayout();
        var page = window.GetVisualDescendants().OfType<SettingsHomeView>().Single();
        Assert.Equal(["SaveModel"], VisiblePrimaries(page));
        Assert.Contains("secondary", Find<Button>(page, "TestModel").Classes);

        // The copy that only restated the controls is gone.
        var texts = page.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
        Assert.DoesNotContain(texts, t => t.Contains("立即生效", StringComparison.Ordinal) || t.Contains("操作系统", StringComparison.Ordinal) || t.Contains("/v1 结尾", StringComparison.Ordinal));

        await SettingsInput.OpenAsync(window, "StorageDetailsButton");
        var storage = window.GetVisualDescendants().OfType<StorageDetailsView>().Single();
        Assert.Empty(VisiblePrimaries(storage));
        var open = Find<Button>(storage, "OpenDataFolder");
        Assert.Contains("quiet", open.Classes);
        UiInput.Click(window, open);
        Assert.Equal([app.Paths.DataDirectory], app.Shell.OpenedFolders);
        window.Close();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Settings open on a configured model, with three models fetched by a real click on 获取列表.</summary>
    private static async Task<(DesktopHarness App, MainWindow Window, ModelSettingsViewModel Model, Popup Popup)> FetchedAsync()
    {
        var app = DesktopHarness.Create();
        app.Client.Models = ["alpha", "beta", "gamma"];
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var (window, settings) = await OpenSettingsAsync(app, started: true);
        UiInput.Click(window, Find<Button>(window, "FetchModels"));
        await UiWait.UntilAsync(() => !settings.Model.IsBusy);
        window.UpdateLayout();
        return (app, window, settings.Model, Find<Popup>(window, "ModelPopup"));
    }

    private static Color Brush(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static async Task<(MainWindow Window, SettingsHomeViewModel Settings)> OpenSettingsAsync(DesktopHarness app, bool started = false)
    {
        var window = app.ShowWindow(1100, 720);
        if (!started)
        {
            await app.StartWithWorkspaceAsync("火车");
        }

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        await settings.Model.Loaded;
        window.UpdateLayout();
        return (window, settings);
    }

    private static List<string> Suggestions(Popup popup) =>
        popup.Child!.GetLogicalDescendants().OfType<ListBox>().Single().Items.Cast<string>().ToList();

    private static List<string?> VisiblePrimaries(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("primary") && b.IsEffectivelyVisible).Select(b => b.Name).ToList();

    private static Color SegmentFill(RadioButton segment)
    {
        var part = segment.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Segment");
        return part.Background is ISolidColorBrush brush ? brush.Color : Colors.Transparent;
    }

    private static Color Colour(Visual control, string key) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(((StyledElement)control).FindResource(((Control)control).ActualThemeVariant, key)).Color;
}
