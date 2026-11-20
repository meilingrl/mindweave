using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Storage;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Trial helper: renders every workspace of a COPY of real data in both views of the session network. Returns at once
/// unless <c>MINDWEAVE_SPIKE_DATA</c> names the copy's folder; shots go to <c>MINDWEAVE_SCREENSHOT_DIR</c>.
/// </summary>
[Trait("Category", "Trial")]
public class SessionNetworkShots
{
    [AvaloniaFact]
    public async Task Render_each_workspace_in_both_views()
    {
        var data = Environment.GetEnvironmentVariable("MINDWEAVE_SPIKE_DATA");
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        await using var app = DesktopHarness.Create(s => s.AddSingleton(new SessionNetworkGate(true)), directory: data);
        await app.StartAsync();
        var window = app.ShowWindow(1400, 900);
        var workspaces = await app.Services.GetRequiredService<IWorkspaceStore>().ListAsync(CancellationToken.None);
        var index = 0;
        foreach (var workspace in workspaces)
        {
            app.Workbench.SwitchWorkspace(workspace.Id);
            app.Workbench.Navigate(new NavigationRequest(MainSection.SummaryNetwork));
            await UiWait.UntilAsync(() => app.Workbench.CurrentPage is SummaryNetworkHomeViewModel);
            var network = (SummaryNetworkHomeViewModel)app.Workbench.CurrentPage!;
            await network.Loaded;
            foreach (var mode in new[] { SummaryNetworkViewMode.Spatial, SummaryNetworkViewMode.Timeline })
            {
                network.SetViewModeCommand.Execute(mode);
                await Task.Delay(300);
                window.UpdateLayout();
                Screenshots.Capture(window, $"{index}-{mode.ToString().ToLowerInvariant()}");
            }

            index++;
        }

        window.Close();
    }
}
