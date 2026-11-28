using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Languages;
using Mindweave.Application.Models;
using Mindweave.Core.Models;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Conversations;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>设置 › 模型连接 › 移除密钥 (D2 §2.3, §2.6, §2.7 scenario 6).</summary>
[Collection(Text.LanguageTests.Name)] // The gallery switches the language.
public class ModelKeyRemovalTests
{
    [AvaloniaFact]
    public async Task Removing_the_key_says_the_connection_is_not_set_up_here_and_on_a_new_exploration()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var settings = new ModelSettingsViewModel(app.Services.GetRequiredService<IModelSettings>());
        await settings.Loaded;
        Assert.True(settings.CanRemoveKey);
        Assert.Null(settings.Status);

        await settings.RemoveKeyCommand.ExecuteAsync(null);

        Assert.Equal(UiText.ModelKeyRemoved, settings.Status);
        Assert.False(settings.StatusIsError);
        Assert.False(settings.CanRemoveKey);
        Assert.False(settings.HasSavedKey);
        Assert.Equal(UiText.KeyMissingPlaceholder, settings.KeyPlaceholder);
        Assert.Empty(app.Credentials.Items);
        Assert.Equal("fake-model", settings.Model);

        // Read again later, the page says why nothing works.
        var later = new ModelSettingsViewModel(app.Services.GetRequiredService<IModelSettings>());
        await later.Loaded;
        Assert.Equal(UiText.ModelKeyNotSet, later.Status);
        Assert.False(later.StatusIsError);

        // 新探索 says so before a question is typed, and a question is refused with the way to settings.
        app.ShellPage.Conversations.NewCommand.Execute(null);
        var draft = Assert.IsType<NewConversationViewModel>(app.Workbench.CurrentPage);
        await draft.ModelNote!.Checked;
        Assert.True(draft.ModelNote.IsShown);
        draft.Composer.Text = "还在吗？";
        await draft.Composer.SendCommand.ExecuteAsync(null);
        Assert.Equal(SendRejection.NotConfigured, draft.Composer.Rejection);
        Assert.True(draft.Composer.ShowSettingsLink);

        // A key saved again clears both.
        settings.ApiKey = "sk-again";
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.True(settings.CanRemoveKey);
        await UiWait.UntilAsync(() => !draft.ModelNote.IsShown);
    }

    [AvaloniaFact]
    public async Task A_key_in_use_is_removed_only_after_the_user_agrees_to_stop_its_requests()
    {
        await using var app = DesktopHarness.Create();
        var workspace = await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var held = app.Client.Hold("蒸汽");
        var sent = await app.Services.GetRequiredService<IConversations>().StartAsync(workspace.Id, "火车是怎么来的？", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var settings = new ModelSettingsViewModel(app.Services.GetRequiredService<IModelSettings>());
        await settings.Loaded;

        await settings.RemoveKeyCommand.ExecuteAsync(null);

        Assert.True(settings.IsConfirmingKeyRemoval);
        Assert.Equal(UiText.ModelKeyInUse(1), settings.KeyRemovalQuestion);
        Assert.Single(app.Credentials.Items);
        Assert.True(settings.CanRemoveKey);

        settings.CancelKeyRemovalCommand.Execute(null);
        Assert.False(settings.IsConfirmingKeyRemoval);
        Assert.Single(app.Credentials.Items);
        Assert.True(app.Services.GetRequiredService<IConversations>().HasUnfinishedAnswer(sent.Conversation));

        await settings.RemoveKeyCommand.ExecuteAsync(null);
        await settings.ConfirmKeyRemovalCommand.ExecuteAsync(null);

        Assert.False(settings.IsConfirmingKeyRemoval);
        Assert.Equal(UiText.ModelKeyRemoved, settings.Status);
        Assert.Empty(app.Credentials.Items);
        Assert.False(app.Services.GetRequiredService<IConversations>().HasUnfinishedAnswer(sent.Conversation));
        var store = app.Services.GetRequiredService<Application.Storage.IConversationStore>();
        var answer = (await store.GetMessagesAsync(sent.Conversation, CancellationToken.None)).Single(m => m.Id == sent.Answer);
        Assert.Equal(Core.Conversations.MessageState.Stopped, answer.State);
    }

    /// <summary>§2.6: a locked store keeps the key; the page says to unlock and retry, not that it will not save in plain text.</summary>
    [AvaloniaFact]
    public async Task A_locked_key_store_keeps_the_key_and_says_so()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var settings = new ModelSettingsViewModel(app.Services.GetRequiredService<IModelSettings>());
        await settings.Loaded;
        app.Credentials.Unavailable = true;

        await settings.RemoveKeyCommand.ExecuteAsync(null);

        Assert.Equal(UiText.ModelKeyRemoveFailed("simulated: keychain locked"), settings.Status);
        Assert.True(settings.StatusIsError);
        Assert.True(settings.CanRemoveKey);
        app.Credentials.Unavailable = false;
        Assert.Single(app.Credentials.Items);
        Assert.NotNull(await app.Services.GetRequiredService<ModelSettingsService>().GetActiveSnapshotAsync(CancellationToken.None));
    }

    /// <summary>The button stands beside 测试连接 only while a key is saved; the question shows only while it is asked.</summary>
    [AvaloniaFact]
    public async Task The_settings_page_shows_the_button_and_the_question_when_they_apply()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var workspace = await app.StartWithWorkspaceAsync("火车");
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Model.Loaded;
        window.UpdateLayout();
        var view = window.GetVisualDescendants().OfType<SettingsHomeView>().Single();
        Assert.False(Find<Button>(view, "RemoveModelKey").IsVisible);

        await app.ConfigureModelAsync();
        settings.Model.ReloadIfUnchanged();
        await settings.Model.Loaded;
        window.UpdateLayout();
        Assert.True(Find<Button>(view, "RemoveModelKey").IsEffectivelyVisible);
        Assert.False(Find<Border>(view, "ModelKeyRemovalConfirm").IsVisible);

        var held = app.Client.Hold("蒸汽");
        await app.Services.GetRequiredService<IConversations>().StartAsync(workspace.Id, "问", CancellationToken.None);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SettingsInput.Click(window, Find<Button>(view, "RemoveModelKey"));
        await UiWait.UntilAsync(() => settings.Model.IsConfirmingKeyRemoval);
        window.UpdateLayout();
        Assert.True(Find<Border>(view, "ModelKeyRemovalConfirm").IsEffectivelyVisible);

        SettingsInput.Click(window, Find<Button>(view, "ModelKeyRemovalYes"));
        await UiWait.UntilAsync(() => settings.Model.Status == UiText.ModelKeyRemoved);
        window.UpdateLayout();
        Assert.False(Find<Button>(view, "RemoveModelKey").IsVisible);
        Assert.False(Find<Border>(view, "ModelKeyRemovalConfirm").IsVisible);
        window.Close();
    }

    /// <summary>The MCP form says the arguments are plain text and where a secret goes (D2 §2.3).</summary>
    [AvaloniaFact]
    public async Task The_mcp_form_says_arguments_are_plain_text()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await SaveToolModelAsync(app);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        var mcp = settings.Tools!.Mcp!;
        await mcp.Loaded;

        mcp.StartAddCommand.Execute(null);
        window.UpdateLayout();

        var line = Find<TextBlock>(window.GetVisualDescendants().OfType<SettingsHomeView>().Single(), "McpArgumentsPlain");
        Assert.True(line.IsEffectivelyVisible);
        Assert.Equal(UiText.McpArgumentsPlain, line.Text);
        window.Close();
    }

    /// <summary>
    /// For looking at (MINDWEAVE_SCREENSHOT_DIR, <c>--filter FullyQualifiedName~ModelKeyRemovalTests.Gallery</c>):
    /// key-1-button, key-2-asked, key-3-removed and key-4-mcp, in each theme and language.
    /// </summary>
    [AvaloniaTheory]
    [Trait("Category", "Gallery")]
    [InlineData("Light", UiLanguage.English)]
    [InlineData("Dark", UiLanguage.English)]
    [InlineData("Light", UiLanguage.Chinese)]
    [InlineData("Dark", UiLanguage.Chinese)]
    public async Task Gallery(string theme, UiLanguage language)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR")), "Screenshots only.");
        var before = CurrentLanguage.Value;
        CurrentLanguage.Set(language);
        try
        {
            await using var app = DesktopHarness.Create();
            var window = app.ShowWindow(1100, 720, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
            var look = $"{theme.ToLowerInvariant()}-{(language == UiLanguage.English ? "en" : "zh")}";
            var workspace = await app.StartWithWorkspaceAsync(language == UiLanguage.English ? "Railways" : "火车");
            await SaveToolModelAsync(app);
            app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
            var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
            await settings.Loaded;
            await settings.Model.Loaded;
            var view = window.GetVisualDescendants().OfType<SettingsHomeView>().Single();
            var scroller = Find<ScrollViewer>(view, "Scroller");

            void ShowModel()
            {
                window.UpdateLayout();
                var section = Find<Control>(view, "ModelSection");
                scroller.Offset = new Vector(0, Math.Max(0, scroller.Offset.Y + section.TranslatePoint(default, scroller)!.Value.Y + 120));
                window.UpdateLayout();
            }

            ShowModel();
            Screenshots.Capture(window, $"key-1-button-{look}");

            var held = app.Client.Hold("…");
            await app.Services.GetRequiredService<IConversations>().StartAsync(workspace.Id, "?", CancellationToken.None);
            await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await settings.Model.RemoveKeyCommand.ExecuteAsync(null);
            ShowModel();
            Screenshots.Capture(window, $"key-2-asked-{look}");

            await settings.Model.ConfirmKeyRemovalCommand.ExecuteAsync(null);
            ShowModel();
            Screenshots.Capture(window, $"key-3-removed-{look}");

            var mcp = settings.Tools!.Mcp!;
            await mcp.Loaded;
            mcp.StartAddCommand.Execute(null);
            window.UpdateLayout();
            var arguments = Find<Control>(view, "McpNewArguments");
            scroller.Offset = new Vector(0, Math.Max(0, scroller.Offset.Y + arguments.TranslatePoint(default, scroller)!.Value.Y - 200));
            window.UpdateLayout();
            Screenshots.Capture(window, $"key-4-mcp-{look}");
            window.Close();
        }
        finally
        {
            CurrentLanguage.Set(before);
        }
    }

    /// <summary>A model that can call tools, so the tools' settings and the MCP form can be used.</summary>
    private static Task SaveToolModelAsync(DesktopHarness app) =>
        app.Services.GetRequiredService<ModelSettingsService>().SaveAsync(
            new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", "sk-test", null, ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true }),
            CancellationToken.None);
}
