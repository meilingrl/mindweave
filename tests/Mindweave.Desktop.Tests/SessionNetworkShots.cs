using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Languages;
using Mindweave.Application.Storage;
using Mindweave.Application.SummaryNetwork;
using Mindweave.Core.Summaries;
using Mindweave.Desktop.Navigation;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.Text;
using Mindweave.Desktop.ViewModels.Pages;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Trial helper: renders every workspace of a COPY of real data in both views of the session network, at the opening
/// viewport and after "Show all", with one summarised dot selected, in English and in Chinese (light), and in one
/// workspace dark. Writes <c>metrics.txt</c> (the §6.4 checks of the spatial arrangement) next to the shots. Returns at
/// once unless <c>MINDWEAVE_TRIAL_DATA</c> names the copy's folder; shots go to <c>MINDWEAVE_SCREENSHOT_DIR</c>.
/// </summary>
[Trait("Category", "Trial")]
[Collection(Text.LanguageTests.Name)]
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
        Directory.CreateDirectory(shots!);
        var metrics = new System.Text.StringBuilder();
        try
        {
            var sizes = await RenderAsync(data, shots!, UiLanguage.Chinese, "zh", null, null, metrics);
            await RenderAsync(data, shots!, UiLanguage.English, "en", null, null, metrics);
            var biggest = sizes.IndexOf(sizes.Max());
            await RenderAsync(data, shots!, UiLanguage.Chinese, "zh-dark", ThemeVariant.Dark, biggest, metrics);
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
            File.WriteAllText(Path.Combine(shots!, "metrics.txt"), metrics.ToString());
        }
    }

    /// <summary>One pass over the workspaces (or just <paramref name="only"/>) on a fresh app, so no pan or zoom is left over.</summary>
    private static async Task<List<int>> RenderAsync(string data, string shots, UiLanguage language, string tag, ThemeVariant? theme, int? only, System.Text.StringBuilder metrics)
    {
        var sizes = new List<int>();
        await using var app = DesktopHarness.Create(s => s.AddSingleton(new SessionNetworkGate(true)), directory: data);
        await app.StartAsync();
        var window = app.ShowWindow(1400, 900, theme);
        try
        {
            if (CurrentLanguage.Value != language)
            {
                await LanguageSwitchTests.SwitchAsync(app, language);
            }

            var workspaces = await app.Services.GetRequiredService<IWorkspaceStore>().ListAsync(CancellationToken.None);
            for (var index = 0; index < workspaces.Count; index++)
            {
                if (only is not null && only != index)
                {
                    continue;
                }

                app.Workbench.SwitchWorkspace(workspaces[index].Id);
                app.Workbench.Navigate(new NavigationRequest(MainSection.SummaryNetwork));
                await UiWait.UntilAsync(() => app.Workbench.CurrentPage is SummaryNetworkHomeViewModel);
                var network = (SummaryNetworkHomeViewModel)app.Workbench.CurrentPage!;
                await network.Loaded;
                sizes.Add(network.Session.Projection.Nodes.Count);
                var prefix = $"{tag}-{index}";
                await ShowAsync(network, window, SummaryNetworkViewMode.Spatial);
                if (theme is null)
                {
                    metrics.AppendLine(Describe(tag, index, workspaces[index].Name.Value, network.Session));
                }

                Capture(window, shots, $"{prefix}-spatial-open");
                ShowAll(network, window);
                Capture(window, shots, $"{prefix}-spatial-all");
                await ShowAsync(network, window, SummaryNetworkViewMode.Timeline);
                Capture(window, shots, $"{prefix}-timeline-open");
                ShowAll(network, window);
                Capture(window, shots, $"{prefix}-timeline-all");
                if (network.Session.Projection.Nodes.Where(n => n.HasSummary).MaxBy(n => n.Weight) is { } summarised)
                {
                    await ShowAsync(network, window, SummaryNetworkViewMode.Spatial);
                    network.Select(summarised.Id);
                    window.UpdateLayout();
                    Capture(window, shots, $"{prefix}-spatial-selected");
                }
            }
        }
        finally
        {
            window.Close();
        }

        return sizes;
    }

    private static async Task ShowAsync(SummaryNetworkHomeViewModel network, Window window, SummaryNetworkViewMode mode)
    {
        network.SetViewModeCommand.Execute(mode);
        // The requested view is the one shown (an empty workspace has no nodes and gets its empty-state shot).
        await UiWait.UntilAsync(() => network.IsTimeline == (mode == SummaryNetworkViewMode.Timeline));
        await network.Loaded;
        window.UpdateLayout();
        window.UpdateLayout();
    }

    private static void ShowAll(SummaryNetworkHomeViewModel network, Window window)
    {
        network.ShowAllCommand.Execute(null);
        window.UpdateLayout();
        window.UpdateLayout();
    }

    private static void Capture(Window window, string shots, string name)
    {
        Screenshots.Capture(window, name);
        Assert.True(File.Exists(Path.Combine(shots, name + ".png")), $"{name}.png was not written");
    }

    /// <summary>The §6.4 checks of the spatial arrangement of one workspace, whole and per sub-branch.</summary>
    private static string Describe(string tag, int index, string name, SummaryNetworkSession session)
    {
        var projection = session.Projection;
        var nodes = SummaryNetworkSession.PackNodes(projection, SummaryNetworkSession.BranchParents(projection), session.DayStarts);
        var positions = session.Positions;
        var whole = SpatialPacking.Measure(nodes, positions);
        var text = new System.Text.StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"[{tag}] workspace {index} '{name}': {nodes.Count} dots");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  enclosing {whole.EnclosingRadius:F0} / ideal {whole.IdealRadius:F0} = {(whole.IdealRadius > 0 ? whole.EnclosingRadius / whole.IdealRadius : 0):F3}; " +
            $"centroid offset {whole.CentroidOffset:F3}; root time-rank correlation {whole.RootTimeRankCorrelation:F3}; overlaps {whole.Overlaps}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  branch lines {whole.BranchEdges}, crossings {whole.BranchCrossings} ({(whole.BranchEdges > 0 ? (double)whole.BranchCrossings / whole.BranchEdges : 0):F3} per line), lines through dots {whole.EdgesThroughDots}");
        var children = nodes.Where(n => n.Parent is not null).GroupBy(n => n.Parent!.Value).ToDictionary(g => g.Key, g => g.Select(n => n.Id).ToList());
        var ratios = new List<(string Name, int Size, double Ratio, double Offset)>();
        foreach (var node in nodes.Where(n => children.ContainsKey(n.Id)))
        {
            var members = new HashSet<SummaryNodeId>();
            var queue = new Queue<SummaryNodeId>([node.Id]);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (members.Add(id) && children.TryGetValue(id, out var below))
                {
                    below.ForEach(queue.Enqueue);
                }
            }

            if (members.Count < 3)
            {
                continue;
            }

            var report = SpatialPacking.Measure([.. nodes.Where(n => members.Contains(n.Id))], positions);
            var title = projection.Nodes.First(n => n.Id == node.Id).ShortTitle;
            ratios.Add((title, members.Count, report.IdealRadius > 0 ? report.EnclosingRadius / report.IdealRadius : 0, report.CentroidOffset));
        }

        if (ratios.Count > 0)
        {
            var worst = ratios.MaxBy(r => r.Ratio);
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  sub-branches (>= 3 dots): {ratios.Count}; enclosing/ideal worst {worst.Ratio:F3} ('{worst.Name}', {worst.Size} dots), mean {ratios.Average(r => r.Ratio):F3}; worst centroid offset {ratios.Max(r => r.Offset):F3}");
        }

        return text.ToString();
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
        var real = Path.GetFullPath(Directory.GetParent(AppPaths.Resolve("release", n => null).DataDirectory)!.FullName);
        Assert.Contains(real, RealDataRoots().Select(r => r.TrimEnd(Path.DirectorySeparatorChar)));
        Assert.Contains(RealDataRoots(), r => Path.GetFullPath(r).Equals(real, StringComparison.OrdinalIgnoreCase));
        Assert.Throws<InvalidOperationException>(() => RefuseRealData(real));
        Assert.Throws<InvalidOperationException>(() => RefuseRealData(Path.Combine(real, "release")));
        RefuseRealData(Path.Combine(Path.GetTempPath(), "mindweave-copy"));
    }
}
