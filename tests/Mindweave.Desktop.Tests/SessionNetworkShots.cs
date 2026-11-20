using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Storage;
using Mindweave.Desktop.Navigation;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Trial helper: renders every workspace of a COPY of real data in both views of the session network. Returns at once
/// unless <c>MINDWEAVE_TRIAL_DATA</c> names the copy's folder; shots go to <c>MINDWEAVE_SCREENSHOT_DIR</c>.
/// </summary>
[Trait("Category", "Trial")]
public class SessionNetworkShots
{
    [AvaloniaFact]
    public async Task Render_each_workspace_in_both_views()
    {
        var data = Environment.GetEnvironmentVariable("MINDWEAVE_TRIAL_DATA");
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        var shots = Environment.GetEnvironmentVariable("MINDWEAVE_SCREENSHOT_DIR");
        Assert.False(string.IsNullOrEmpty(shots), "MINDWEAVE_TRIAL_DATA is set, so MINDWEAVE_SCREENSHOT_DIR must name the folder for the shots.");
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
                    // The requested view is the one shown (an empty workspace has no nodes and gets its empty-state shot).
                    await UiWait.UntilAsync(() => network.IsTimeline == (mode == SummaryNetworkViewMode.Timeline));
                    await network.Loaded;
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

    /// <summary>
    /// The folders that hold a person's real data, under any channel: the parent "Mindweave" folder of where the app itself
    /// resolves its default data directory, plus the per-user roots as extra coverage.
    /// </summary>
    private static IEnumerable<string> RealDataRoots()
    {
        // The app's own resolver with the data-directory override ignored; the channel only names the last folder.
        var resolved = AppPaths.Resolve("release", name => name == AppPaths.DataDirectoryVariable ? null : Environment.GetEnvironmentVariable(name));
        yield return Path.GetFullPath(Directory.GetParent(resolved.DataDirectory)!.FullName);
        foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.GetFullPath(Path.Combine(root, "Mindweave"));
            }
        }
    }

    private static void RefuseRealData(string directory)
    {
        var target = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var root in RealDataRoots().Select(r => r.TrimEnd(Path.DirectorySeparatorChar)))
        {
            if (target.Equals(root, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to run on '{directory}': it is the real Mindweave data folder. Copy the database elsewhere first.");
            }
        }
    }

    [Fact]
    public void The_guard_refuses_the_real_data_folder()
    {
        var real = Path.GetFullPath(Directory.GetParent(AppPaths.Resolve("claude-auto", n => null).DataDirectory)!.FullName);
        Assert.Contains(real, RealDataRoots().Select(r => r.TrimEnd(Path.DirectorySeparatorChar)));
        Assert.Contains(RealDataRoots(), r => Path.GetFullPath(r).Equals(real, StringComparison.OrdinalIgnoreCase));
        Assert.Throws<InvalidOperationException>(() => RefuseRealData(real));
        Assert.Throws<InvalidOperationException>(() => RefuseRealData(Path.Combine(real, "claude-auto")));
        RefuseRealData(Path.Combine(Path.GetTempPath(), "mindweave-copy"));
    }
}
