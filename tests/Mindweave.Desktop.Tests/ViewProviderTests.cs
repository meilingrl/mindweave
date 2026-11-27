using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Application.Workspaces;
using Mindweave.Desktop.Navigation;
using Mindweave.Desktop.ViewModels;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Composition in two parts (bilingual-ui.md §6.2): a root provider with every service, and a view provider built on it
/// that a language switch replaces. Views share the root's services, and disposing a view leaves them alone.
/// </summary>
public class ViewProviderTests
{
    [AvaloniaFact]
    public async Task Two_views_share_root_services()
    {
        await using var app = DesktopHarness.Create();
        var root = app.Session.Root;

        await using var first = CompositionRoot.BuildView(root);
        await using var second = CompositionRoot.BuildView(root);

        Assert.Same(root.GetRequiredService<GenerationCoordinator>(), first.GetRequiredService<GenerationCoordinator>());
        Assert.Same(first.GetRequiredService<GenerationCoordinator>(), second.GetRequiredService<GenerationCoordinator>());
        Assert.Same(first.GetRequiredService<IConversations>(), second.GetRequiredService<IConversations>());
        Assert.Equal(
            root.GetServices<Application.Tools.ITool>(),
            second.GetServices<Application.Tools.ITool>());
        Assert.NotSame(first.GetRequiredService<ShellViewModel>(), second.GetRequiredService<ShellViewModel>());
        Assert.NotSame(first.GetRequiredService<Workbench>(), second.GetRequiredService<Workbench>());
    }

    [AvaloniaFact]
    public async Task Disposing_a_view_keeps_root_services()
    {
        var probe = new DisposalProbe();
        await using var app = DesktopHarness.Create(s => s.AddSingleton(_ => probe));
        await app.StartWithWorkspaceAsync("火车");
        var root = app.Session.Root;
        var view = CompositionRoot.BuildView(root);
        Assert.Same(probe, view.GetRequiredService<DisposalProbe>());
        var generation = view.GetRequiredService<GenerationCoordinator>();
        var database = view.GetRequiredService<SqliteDatabase>();

        await view.DisposeAsync();

        Assert.False(probe.Disposed);
        Assert.False(generation.IsClosed);
        Assert.True(database.IsOpen);
        Assert.Single(await root.GetRequiredService<WorkspaceService>().ListAsync(CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task A_view_type_override_reaches_the_view_and_a_root_type_override_the_root()
    {
        var sound = new DesktopHarness.RecordingAlertSound();
        ConversationDeletionViewModel? made = null;
        await using var app = DesktopHarness.Create(s =>
        {
            s.AddSingleton<Application.Notifications.IAlertSound>(sound);
            s.AddSingleton(sp => made = new ConversationDeletionViewModel(
                sp.GetRequiredService<Application.Conversations.IConversationDeletion>(), sp.GetRequiredService<IConversations>(), sp.GetRequiredService<Workbench>(),
                sp.GetRequiredService<ConversationDeletionRequests>(), sp.GetRequiredService<Services.IUiDispatcher>(), sp.GetRequiredService<TimeProvider>()));
        });

        Assert.Same(sound, app.Session.Root.GetRequiredService<Application.Notifications.IAlertSound>());
        Assert.Same(sound, app.Session.View.GetRequiredService<Application.Notifications.IAlertSound>());
        Assert.Null(app.Session.Root.GetService<ConversationDeletionViewModel>());
        Assert.Same(app.Session.View.GetRequiredService<ConversationDeletionViewModel>(), made);
        Assert.NotNull(made);
    }

    /// <summary>A root service that says whether it was disposed.</summary>
    private sealed class DisposalProbe : IDisposable, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
