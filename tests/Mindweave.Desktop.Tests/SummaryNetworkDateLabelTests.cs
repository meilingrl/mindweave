using Avalonia;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Core.Conversations;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Desktop.ViewModels.Pages;
using Mindweave.Desktop.Views.Pages;
using Mindweave.Infrastructure.Sqlite;
using static Mindweave.Desktop.Tests.UiTree;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Spec §4.4: in the spatial view a faint date label sits beside a root dot whose day differs from the previous root's,
/// and hides when zoomed out. It sits inside the root's own packing footprint, so it covers no other dot or label.
/// </summary>
public class SummaryNetworkDateLabelTests
{
    /// <summary>
    /// <paramref name="count"/> sessions: every sixth a root, the others branching from a recent earlier one. Session i
    /// started <paramref name="hoursApart"/> × i hours after a fixed local morning, so the roots fall on several days,
    /// some on the same one.
    /// </summary>
    private static async Task<(List<ConversationId> All, SummaryNetworkHomeViewModel Network)> OpenAsync(DesktopHarness app, Mindweave.Desktop.Views.MainWindow window, int count, double hoursApart)
    {
        await app.StartWithWorkspaceAsync("火车");
        await app.ConfigureModelAsync();
        var conversations = app.Services.GetRequiredService<ConversationService>();
        var workspace = app.Workbench.CurrentWorkspace!.Id;
        var all = new List<ConversationId>();
        for (var i = 0; i < count; i++)
        {
            var question = $"第{i}个问题：火车是怎么发明的？";
            all.Add(i % 6 == 0
                ? await SummaryNetworkScenario.AskAsync(app, conversations, null, workspace, question)
                : await SummaryNetworkScenario.BranchAsync(app, conversations, all[Math.Max(0, i - 1 - ((i * 7) % Math.Min(i, 4)))], question));
        }

        var first = new DateTime(DateTime.Now.Year, 3, 10, 8, 0, 0, DateTimeKind.Unspecified);
        await app.Services.GetRequiredService<SqliteDatabase>().WriteAsync(
            (c, t) => all.Select((id, i) => (id, i)).Sum(x =>
            {
                var local = first.AddHours(x.i * hoursApart);
                var at = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeMilliseconds();
                return c.Execute(t, "UPDATE conversation SET created_at = $at WHERE id = $id;", ("$at", at), ("$id", x.id.ToString()));
            }),
            CancellationToken.None);
        var network = await SummaryNetworkScenario.OpenSummaryNetworkAsync(app, window);
        await UiWait.UntilAsync(() => network.Nodes.Count == count);
        await UiWait.UntilAsync(() => network.Nodes.All(n => n.CreatedAt.Year == first.Year));
        window.UpdateLayout();
        return (all, network);
    }

    [AvaloniaFact]
    public async Task Roots_that_start_a_new_day_get_a_date_beside_them()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1400, 900);
        var (_, network) = await OpenAsync(app, window, 24, hoursApart: 3);
        var canvas = CanvasOf(window);
        network.Session.Viewport = canvas.Current with { Zoom = 1 };

        var roots = network.Nodes.Where(n => n.IsRoot).OrderBy(n => n.CreatedAt).ToList();
        var today = SummaryNetworkSession.LocalDay(DateTimeOffset.Now);
        var expected = roots.Where((r, i) => i == 0 || SummaryNetworkSession.LocalDay(r.CreatedAt) != SummaryNetworkSession.LocalDay(roots[i - 1].CreatedAt)).ToList();
        Assert.True(expected.Count > 1 && expected.Count < roots.Count, "the data should have both new-day and same-day roots");

        var captions = canvas.PaintCaptions();

        Assert.Equal(expected.Select(r => r.Id), captions.Select(c => c.Caption.Anchor));
        Assert.Equal(expected.Select(r => UiText.DayLabel(SummaryNetworkSession.LocalDay(r.CreatedAt), today)), captions.Select(c => c.Caption.Text));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_dates_hide_when_zoomed_out()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1400, 900);
        var (_, network) = await OpenAsync(app, window, 12, hoursApart: 10);
        var canvas = CanvasOf(window);

        network.Session.Viewport = canvas.Current with { Zoom = 1 };
        Assert.NotEmpty(canvas.PaintCaptions());
        network.Session.Viewport = canvas.Current with { Zoom = SummaryNetworkCanvas.LabelHideScale };
        Assert.Empty(canvas.PaintCaptions());
        window.Close();
    }

    [AvaloniaFact]
    public async Task In_a_dense_pack_no_date_covers_another_dot_or_label()
    {
        await using var app = DesktopHarness.Create();
        var window = app.ShowWindow(1400, 900);
        var (_, network) = await OpenAsync(app, window, 36, hoursApart: 7);
        var canvas = CanvasOf(window);
        network.Session.Viewport = canvas.Current with { Zoom = 1 };

        var captions = canvas.PaintCaptions();
        var paints = canvas.PaintNodes();
        Assert.True(captions.Count >= 4, $"only {captions.Count} dates");
        foreach (var caption in captions)
        {
            var box = caption.Bounds;
            foreach (var other in paints.Where(p => p.Node.Id != caption.Caption.Anchor))
            {
                var reach = other.Radius + SummaryNetworkCanvas.SummaryRingGap + 1;
                Assert.False(
                    box.Intersects(new Rect(other.Center.X - reach, other.Center.Y - reach, 2 * reach, 2 * reach)),
                    $"date '{caption.Caption.Text}' covers the dot of '{other.Label}'");
                Assert.False(other.Text is not null && box.Intersects(other.LabelBounds), $"date '{caption.Caption.Text}' covers the label '{other.Label}'");
            }

            var own = paints.Single(p => p.Node.Id == caption.Caption.Anchor);
            Assert.False(own.Text is not null && box.Intersects(own.LabelBounds), $"date '{caption.Caption.Text}' covers its own title");
        }

        window.Close();
    }
}
