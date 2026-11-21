using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Preferences;
using Mindweave.Application.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.Services;
using Mindweave.Desktop.ViewModels;
using Mindweave.Desktop.ViewModels.Conversations;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// A view provider is replaced while the services live on (bilingual-ui.md §6.3): every view-layer singleton that
/// listens to a service stops listening when its view is disposed, so a rebuilt view leaves no handler behind and an
/// answer that ends afterwards is announced once.
/// </summary>
public class ViewRebuildTests
{
    [AvaloniaFact]
    public async Task Rebuild_leaves_service_handler_counts_unchanged()
    {
        await using var app = DesktopHarness.Create();
        await app.StartWithWorkspaceAsync("火车");
        var root = app.Session.Root;
        var before = HandlerCounts(root);

        var first = CompositionRoot.BuildView(root);
        Compose(first);
        var oneView = HandlerCounts(root);

        // A view listens to the services at all: otherwise this test would prove nothing.
        Assert.Contains(oneView, p => p.Value > before.GetValueOrDefault(p.Key));

        await first.DisposeAsync();
        AssertSame(before, HandlerCounts(root));

        var second = CompositionRoot.BuildView(root);
        Compose(second);
        AssertSame(oneView, HandlerCounts(root));
        await second.DisposeAsync();
        AssertSame(before, HandlerCounts(root));
    }

    [AvaloniaFact]
    public async Task One_toast_and_one_sound_after_a_rebuild()
    {
        await using var scene = await WorkspaceNewsHintTests.Scene.StartAsync();
        var app = scene.App;
        await app.Services.GetRequiredService<INewsAlerts>().SetSoundAsync(true, CancellationToken.None);
        var oldNews = scene.News;

        // The view is rebuilt on the same services, then opens the same workspace (the switch's replay stands in here).
        var workspaces = await app.Services.GetRequiredService<WorkspaceService>().ListAsync(CancellationToken.None);
        var old = app.Session.ReplaceView();
        old.GetRequiredService<Workbench>().ReleaseAll();
        app.Workbench.Initialize(workspaces, null);
        app.Services.GetRequiredService<IRootNavigator>().Show(app.ShellPage);
        app.ShellPage.SelectedWorkspace = scene.HighSpeed;
        await UiWait.UntilAsync(() => app.Workbench.CurrentWorkspace?.Id == scene.HighSpeed.Id);
        Assert.NotSame(oldNews, scene.News);
        await old.DisposeAsync();

        await scene.AnswerAsync(scene.Trains, "火车是怎么发明的？");
        await scene.SettledAsync();
        await oldNews.Loaded;
        await oldNews.Alerting;
        await scene.News.Alerting;

        Assert.Equal(scene.Trains.Id, Assert.Single(scene.News.Toasts).Workspace);
        Assert.Empty(oldNews.Toasts);
        Assert.Equal(1, app.Sound.Played);
        scene.Window.Close();
    }

    /// <summary>Makes every view-layer singleton, as showing the window and starting would.</summary>
    private static void Compose(IServiceProvider view)
    {
        view.GetRequiredService<AppStartup>();
        view.GetRequiredService<RootViewModel>();
        view.GetRequiredService<ShellViewModel>();
        view.GetRequiredService<ConversationListViewModel>();
        view.GetRequiredService<WorkspaceNewsViewModel>();
    }

    /// <summary>
    /// The handlers on every event (and every other delegate) each root service holds, by its type and field. Events that
    /// forward to another service (<c>ConversationService.ActivityChanged</c>) are counted on that service.
    /// </summary>
    private static Dictionary<string, int> HandlerCounts(IServiceProvider root)
    {
        var registrations = root.GetRequiredService<CompositionRoot.RootRegistrations>();
        var services = registrations.Descriptors
            .Where(d => d.Lifetime == ServiceLifetime.Singleton && !d.ServiceType.IsGenericTypeDefinition && !d.IsKeyedService)
            .Select(d => d.ServiceType)
            .Distinct()
            .SelectMany(root.GetServices)
            .OfType<object>()
            .Distinct(ReferenceEqualityComparer.Instance);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            for (var type = service.GetType(); type is not null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (typeof(Delegate).IsAssignableFrom(field.FieldType))
                    {
                        var key = $"{service.GetType().FullName}#{RuntimeHelpers.GetHashCode(service)}.{type.Name}.{field.Name}";
                        counts[key] = (field.GetValue(service) as Delegate)?.GetInvocationList().Length ?? 0;
                    }
                }
            }
        }

        return counts;
    }

    private static void AssertSame(Dictionary<string, int> expected, Dictionary<string, int> actual)
    {
        var differences = expected.Keys.Union(actual.Keys)
            .Where(k => expected.GetValueOrDefault(k) != actual.GetValueOrDefault(k))
            .Select(k => $"{k}: {expected.GetValueOrDefault(k)} -> {actual.GetValueOrDefault(k)}")
            .ToList();
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }
}
