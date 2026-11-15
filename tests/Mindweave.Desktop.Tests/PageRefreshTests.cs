using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Health check 2026-11-12, ui-vm-3 and ui-vm-4: pages kept in a section's stack read again what may have changed
/// while they were out of sight.
/// </summary>
public class PageRefreshTests
{
    [AvaloniaFact]
    public async Task The_workspaces_page_counts_again_when_shown_again()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        var workspace = await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        window.UpdateLayout();
        UiInput.Click(window, Find<Button>(window, "ManageWorkspacesButton"));
        var page = Assert.IsType<WorkspacesViewModel>(app.Workbench.CurrentPage);
        await page.Loaded;
        Assert.StartsWith("0 个会话", page.Rows[0].CountsText, StringComparison.Ordinal);

        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        app.Client.Reply("答");
        await SummaryNetworkScenario.AskAsync(app, app.Services.GetRequiredService<ConversationService>(), null, workspace.Id, "问");
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        window.UpdateLayout();

        Assert.Same(page, app.Workbench.CurrentPage);
        await UiWait.UntilAsync(() => page.Rows[0].CountsText.StartsWith("1 个会话", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_settings_page_shows_settings_changed_elsewhere_but_keeps_what_is_being_edited()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1100, 720);
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        window.UpdateLayout();
        var settings = Assert.IsType<SettingsHomeViewModel>(app.Workbench.CurrentPage);
        await settings.Loaded;
        await settings.Model.Loaded;

        // Changed by another workspace's settings page.
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        await app.Services.GetRequiredService<IAppearance>().SetAsync(Appearance.Dark, CancellationToken.None);
        var models = app.Services.GetRequiredService<IModelSettings>();
        await models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "model-2", string.Empty), CancellationToken.None);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        window.UpdateLayout();
        await settings.Loaded;
        await settings.Model.Loaded;

        Assert.Same(settings, app.Workbench.CurrentPage);
        Assert.Equal(Appearance.Dark, settings.Appearance);
        Assert.Equal("model-2", settings.Model.Model);

        // A field being edited is not replaced by a read.
        settings.Model.Model = "我正在改的";
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Explore);
        app.ShellPage.ShowSectionCommand.Execute(MainSection.Settings);
        window.UpdateLayout();
        await settings.Model.Loaded;
        Assert.Equal("我正在改的", settings.Model.Model);
        window.Close();
    }

    /// <summary>Health check 2026-11-12, gap-schema-2: settings pointing at a key no longer stored say so instead of "saved".</summary>
    [AvaloniaFact]
    public async Task Settings_whose_key_is_gone_from_the_credential_store_ask_for_it_again()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        app.Credentials.Items.Clear();

        var settings = new ModelSettingsViewModel(app.Services.GetRequiredService<IModelSettings>());
        await settings.Loaded;

        Assert.False(settings.HasSavedKey);
        Assert.False(settings.SavedKeyApplies);
        Assert.Equal(UiText.SavedKeyMissing, settings.Status);
    }
}
