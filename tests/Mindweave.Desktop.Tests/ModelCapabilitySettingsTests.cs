using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Models;
using Mindweave.Core.Models;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// 设置 › 模型连接 › 高级设置: 工具调用 and 图片输入 are filled from the preset for the service and model, filled again
/// when either changes, can be overridden, and are saved; unknown is off (docs/design/model-capabilities.md).
/// </summary>
public class ModelCapabilitySettingsTests
{
    [Theory]
    [InlineData("https://api.deepseek.com", "deepseek-flash", true, true)]
    [InlineData("https://api.deepseek.com/v1", "deepseek-v4-pro", true, false)]
    [InlineData("https://api.deepseek.com", "deepseek-chat", true, false)]
    [InlineData("https://api.deepseek.com", "deepseek-something-new", false, false)]
    [InlineData("https://api.moonshot.cn/v1", "kimi-k3.1-pro", true, true)]
    [InlineData("https://api.moonshot.cn/v1", "kimi-k2.6", true, false)]
    [InlineData("https://api.moonshot.cn/v1", "moonshot-v1-8k-vision-preview", false, false)]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "glm-4.6v", true, true)]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "glm-5v-turbo", false, true)]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "glm-4v-flash", false, false)]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "GLM-5.3", true, false)]
    [InlineData("https://api.openai.com/v1", "gpt-4.1-mini", true, true)]
    [InlineData("https://api.openai.com/v1", "gpt-6.1-terra", false, true)]
    [InlineData("https://api.anthropic.com/v1/", "claude-sonnet-5-5", true, true)]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen3-vl-plus", true, true)]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", true, false)]
    [InlineData("https://openrouter.ai/api/v1", "anthropic/claude-opus-5-5", true, true)]
    [InlineData("https://api.siliconflow.cn/v1", "Pro/deepseek-ai/DeepSeek-V4-Pro", true, false)]
    [InlineData("https://api.siliconflow.cn/v1", "some-lab/unknown-model", false, false)]
    [InlineData("http://localhost:11434/v1", "llava", false, false)]
    [InlineData("https://relay.example/v1", "claude-sonnet-5-5", false, false)]
    [InlineData("https://api.deepseek.com", "", false, false)]
    public void Presets_suggest_by_service_and_model_and_unknown_is_off(string baseUrl, string model, bool tools, bool images)
    {
        var suggested = ModelCapabilityPresets.Suggest(baseUrl, model);
        Assert.Equal(tools, suggested.Has(ModelCapability.ToolCalls));
        Assert.Equal(images, suggested.Has(ModelCapability.ImageInput));
        Assert.True(suggested.Has(ModelCapability.Streaming));
        Assert.Null(suggested.ContextWindowTokens);
    }

    [AvaloniaFact]
    public async Task The_advanced_section_opens_on_request_and_follows_the_service_and_model()
    {
        await using var app = DesktopHarness.Create();
        var (window, settings) = await OpenSettingsAsync(app);
        var model = settings.Model;
        var section = Find<StackPanel>(window, "ModelCapabilities");
        Assert.False(section.IsVisible);

        Find<ToggleButton>(window, "AdvancedToggle").IsChecked = true;
        window.UpdateLayout();
        Assert.True(section.IsVisible);
        var tools = Find<CheckBox>(window, "ToolCallsToggle");
        var images = Find<CheckBox>(window, "ImageInputToggle");
        Assert.False(tools.IsChecked);
        Assert.False(images.IsChecked);

        Find<ComboBox>(window, "ProviderPicker").SelectedItem = ProviderPresets.Zhipu;
        model.Model = "glm-4.6v";
        Assert.True(tools.IsChecked);
        Assert.True(images.IsChecked);

        // An override holds for the model shown; another model is filled afresh.
        images.IsChecked = false;
        Assert.False(model.DeclaresImageInput);
        model.Model = "glm-5.3";
        Assert.True(model.DeclaresToolCalls);
        Assert.False(model.DeclaresImageInput);
        model.Model = "glm-5v-turbo";
        Assert.False(model.DeclaresToolCalls);
        Assert.True(model.DeclaresImageInput);

        // Any other address is unknown: off.
        model.BaseUrl = "https://relay.example/v1";
        Assert.False(model.DeclaresToolCalls);
        Assert.False(model.DeclaresImageInput);
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_override_is_saved_shown_again_and_answered_by_the_application_query()
    {
        await using var app = DesktopHarness.Create();
        var (window, settings) = await OpenSettingsAsync(app);
        var model = settings.Model;
        var service = app.Services.GetRequiredService<IModelSettings>();
        model.BaseUrl = "https://api.deepseek.com";
        model.ApiKey = "sk-capabilities-test";
        model.Model = "deepseek-v4-pro";
        Assert.True(model.DeclaresToolCalls);
        Assert.False(model.DeclaresImageInput);

        // The reader knows better: no tools through this account, images yes.
        model.DeclaresToolCalls = false;
        model.DeclaresImageInput = true;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.False(model.StatusIsError);

        var saved = (await service.GetAsync(CancellationToken.None))!.Capabilities;
        Assert.False(saved.ToolCalls);
        Assert.True(saved.ImageInput);
        Assert.True(saved.ReasoningOutput); // a flag not shown keeps the preset's value
        Assert.False(await service.ActiveModelHasAsync(ModelCapability.ToolCalls, CancellationToken.None));
        Assert.True(await service.ActiveModelHasAsync(ModelCapability.ImageInput, CancellationToken.None));

        var again = new ModelSettingsViewModel(service);
        await again.Loaded;
        Assert.False(again.DeclaresToolCalls);
        Assert.True(again.DeclaresImageInput);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_saved_before_capabilities_existed_show_the_preset_suggestion()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        var service = app.Services.GetRequiredService<IModelSettings>();
        // No declared capabilities: what every save stored before this form had the section.
        await service.SaveAsync(new ModelSettingsInput("DeepSeek", "https://api.deepseek.com", "deepseek-flash", "sk-capabilities-test"), CancellationToken.None);
        Assert.False(await service.ActiveModelHasAsync(ModelCapability.ImageInput, CancellationToken.None));

        var form = new ModelSettingsViewModel(service);
        await form.Loaded;
        Assert.True(form.DeclaresToolCalls);
        Assert.True(form.DeclaresImageInput);

        // Saving the form stores what it shows.
        await form.SaveCommand.ExecuteAsync(null);
        Assert.True(await service.ActiveModelHasAsync(ModelCapability.ImageInput, CancellationToken.None));
    }

    private static async Task<(MainWindow Window, SettingsHomeViewModel Settings)> OpenSettingsAsync(DesktopHarness app)
    {
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        await settings.Model.Loaded;
        window.UpdateLayout();
        return (window, settings);
    }
}
