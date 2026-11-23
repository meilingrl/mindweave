using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Preferences;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views;
using Mindweave.Desktop.Views.Pages;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The summary network page's details and legend (the owner, 2026-11-23): the details are only as tall as their content,
/// resized at their edge, folded away and brought back, both remembered; the legend is an icon.
/// </summary>
public class SummaryNetworkDetailsTests
{
    public static TheoryData<string> Themes => new() { "Light", "Dark" };

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task The_details_fold_away_and_come_back_and_both_are_remembered(string theme)
    {
        await using var app = DesktopHarness.Create();
        var (window, network) = await OpenAsync(app, theme);
        Assert.True(Find<Grid>(window, "DetailsArea").IsEffectivelyVisible);
        Assert.False(Find<Button>(window, "ExpandDetailsButton").IsEffectivelyVisible);

        // As tall as what it says, not as the page.
        Assert.True(Find<Border>(window, "DetailsCard").Bounds.Height < CanvasOf(window).Bounds.Height / 2);
        Screenshots.Capture(window, $"network-details-rest-{theme.ToLowerInvariant()}");
        var width = CanvasOf(window).Bounds.Width;

        UiInput.Click(window, Find<Button>(window, "CollapseDetailsButton"));
        window.UpdateLayout();
        Assert.False(Find<Grid>(window, "DetailsArea").IsEffectivelyVisible);
        Assert.True(CanvasOf(window).Bounds.Width > width + SummaryNetworkDetailsLayout.MinWidth);
        Screenshots.Capture(window, $"network-details-collapsed-{theme.ToLowerInvariant()}");
        await network.Details.Saving;
        Assert.True((await app.Services.GetRequiredService<ISummaryNetworkDetailsLayout>().GetAsync(CancellationToken.None)).IsCollapsed);

        // Selecting a dot does not open them again.
        network.Select(network.Session.Projection.Nodes[0].Id);
        window.UpdateLayout();
        Assert.False(Find<Grid>(window, "DetailsArea").IsEffectivelyVisible);

        UiInput.Click(window, Find<Button>(window, "ExpandDetailsButton"));
        window.UpdateLayout();
        Assert.True(Find<Grid>(window, "DetailsArea").IsEffectivelyVisible);
        Screenshots.Capture(window, $"network-details-selected-{theme.ToLowerInvariant()}");
        await network.Details.Saving;
        Assert.False((await app.Services.GetRequiredService<ISummaryNetworkDetailsLayout>().GetAsync(CancellationToken.None)).IsCollapsed);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_edge_resizes_the_details_by_pointer_and_keyboard_within_bounds()
    {
        await using var app = DesktopHarness.Create();
        var (window, network) = await OpenAsync(app, "Light");
        var grip = Find<EdgeGrip>(window, "DetailsGrip");
        var from = grip.TranslatePoint(new Point(3, 200), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from - new Vector(60, 0));
        Assert.Equal(SummaryNetworkDetailsLayout.DefaultWidth + 60, network.Details.Width, 0);
        window.MouseMove(from + new Vector(500, 0));
        Assert.Equal(SummaryNetworkDetailsLayout.MinWidth, network.Details.Width);
        window.MouseMove(from - new Vector(30, 0));
        window.MouseUp(from - new Vector(30, 0), MouseButton.Left);
        Assert.Equal(SummaryNetworkDetailsLayout.DefaultWidth + 30, network.Details.Width, 0);
        await network.Details.Saving;
        Assert.Equal(network.Details.Width, (await app.Services.GetRequiredService<ISummaryNetworkDetailsLayout>().GetAsync(CancellationToken.None)).Width, 0);

        Assert.Equal(UiText.SummaryNetworkDetailsWidth, AutomationProperties.GetName(grip));
        Assert.True(grip.Focus());
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.Equal(SummaryNetworkDetailsLayout.DefaultWidth + 30 + EdgeGrip.KeyStep, network.Details.Width, 0);
        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Assert.Equal(SummaryNetworkDetailsLayout.DefaultWidth, network.Details.Width);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_legend_shows_on_hover_and_stays_on_a_press_until_escape()
    {
        await using var app = DesktopHarness.Create();
        var (window, _) = await OpenAsync(app, "Light");
        var button = Find<Button>(window, "LegendButton");
        var card = Find<Border>(window, "LegendCard");
        Assert.Equal(UiText.SummaryNetworkLegend, AutomationProperties.GetName(button));
        Assert.False(card.IsVisible);

        window.MouseMove(button.TranslatePoint(new Point(8, 8), window)!.Value);
        Assert.True(card.IsVisible);
        window.MouseMove(new Point(600, 20));
        await UiWait.UntilAsync(() => !card.IsVisible);

        // The keyboard opens it and Esc closes it.
        Assert.True(button.Focus(NavigationMethod.Tab));
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(card.IsVisible);
        Screenshots.Capture(window, "network-legend-open");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(card.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_layout_is_restored_and_a_bad_stored_value_means_the_default()
    {
        await using var app = DesktopHarness.Create();
        var store = app.Services.GetRequiredService<ISummaryNetworkDetailsLayout>();
        await app.StartAsync();
        await store.SetAsync(new SummaryNetworkDetailsLayout(9_999, IsCollapsed: true), CancellationToken.None);
        var details = new SummaryNetworkDetails(store);
        await details.RestoreAsync();
        Assert.Equal(SummaryNetworkDetailsLayout.MaxWidth, details.Width);
        Assert.True(details.IsCollapsed);
        Assert.Equal(SummaryNetworkDetailsLayout.Default, SummaryNetworkDetailsLayoutService.Parse("wide", null));
    }

    private static async Task<(Window Window, SummaryNetworkHomeViewModel Network)> OpenAsync(DesktopHarness app, string theme)
    {
        var window = app.ShowWindow(1100, 720, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
        await SummaryNetworkScenario.SeedSeenAsync(app, secondExploration: true);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        window.UpdateLayout();
        return (window, network);
    }
}
