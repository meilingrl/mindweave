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

        var shots = Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR");
        Assert.False(string.IsNullOrEmpty(shots), "MINDWEAVE_SPIKE_DATA is set, so MINDWEAVE_SCREENSHOT_DIR must name the folder for the shots.");
        RefuseRealData(data);

        await using var app = DesktopHarness.Create(s => s.AddSingleton(new SessionNetworkGate(true)), directory: data);
        await app.StartAsync();
        var window = app.ShowWindow(1400, 900);
        try
        {
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
                    await UiWait.UntilAsync(() => network.Nodes.Count > 0);
                    window.UpdateLayout();
                    var name = $"{index}-{mode.ToString().ToLowerInvariant()}";
                    Screenshots.Capture(window, name);
                    Assert.True(File.Exists(Path.Combine(shots!, name + ".png")), $"{name}.png was not written");
                }

                index++;
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The folders that hold a person's real data, under any channel; the trial must only ever run on a copy.</summary>
    private static void RefuseRealData(string directory)
    {
        var target = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var real = Path.GetFullPath(Path.Combine(root, "Mindweave")).TrimEnd(Path.DirectorySeparatorChar);
            if (target.Equals(real, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to run on '{directory}': it is the real Mindweave data folder. Copy the database elsewhere first.");
            }
        }
    }

    [Fact]
    public void The_guard_refuses_the_real_data_folder()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mindweave");
        Assert.Throws<InvalidOperationException>(() => RefuseRealData(real));
        Assert.Throws<InvalidOperationException>(() => RefuseRealData(Path.Combine(real, "claude-auto")));
        RefuseRealData(Path.Combine(Path.GetTempPath(), "mindweave-copy"));
    }
}
