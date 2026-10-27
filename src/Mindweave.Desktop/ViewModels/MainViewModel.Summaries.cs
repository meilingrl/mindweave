using Avalonia.Threading;
using Mindweave.Application.Navigation;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels;
public sealed partial class MainViewModel
{
    private KnowledgeSummaryCoordinator? summaries;
    private GroupNamingCoordinator? naming;
    private int observedTerminalCount;
    private ISummaryVersionStore? summaryStore;
    private readonly Dictionary<Guid, SummaryDraftSession> summaryDrafts = [];
    private readonly HashSet<SummaryViewModel> summaryPages = [];
    private (Guid Workspace, NavigationRequest Request)? pendingSummarySelection;
    public void ConfigureSummaries(KnowledgeSummaryCoordinator summaryCoordinator, GroupNamingCoordinator namingCoordinator)
    {
        summaries = summaryCoordinator; naming = namingCoordinator;
        summaryStore = store as ISummaryVersionStore ?? throw new ArgumentException("总结存储不可用。");
        summaries.Changed += SummaryChanged; naming.Changed += SummaryChanged;
    }
    private void SummaryChanged() => Dispatcher.UIThread.Post(() => TrackBackgroundRefresh(RefreshSummaryDisplayAsync));
    private async Task RefreshSummaryDisplayAsync()
    {
        if (detached || closing) return;
        try
        {
            if (ActiveContent is SummaryListViewModel list) await list.ReloadAsync();
            if (ActiveContent is SummaryViewModel detail) await detail.RefreshMetadataAsync();
            if (ActiveContent is ChatViewModel chat) await chat.RefreshSummaryAsync();
            // Do not reload body/draft or create a fresh read epoch on background completion.
            NotifyAvailability();
        }
        catch { ErrorMessage = "总结状态更新失败，可重新打开查看。"; }
    }
    private async Task RefreshSummaryAfterReplyAsync()
    {
        if (coordinator is null || summaryStore is null) return;
        var terminalCount = coordinator.Snapshots.Count(s => s.Status is not (Mindweave.Core.Chat.GenerationStatus.Queued or Mindweave.Core.Chat.GenerationStatus.Running));
        if (terminalCount == observedTerminalCount) return;
        observedTerminalCount = terminalCount;
        try
        {
            if (ActiveContent is SummaryViewModel detail) await detail.RefreshMetadataAsync();
            if (ActiveContent is SummaryListViewModel list) await list.ReloadAsync();
        }
        catch { /* Optional metadata refresh must not block saved-body reading. */ }
    }
    private async Task<NavigationRequest?> ValidateSummaryRequestAsync(Guid workspace, NavigationRequest request)
    {
        if (summaryStore is null || request.Kind != ContentKind.Summary) return request;
        var node = await summaryStore.LoadSummaryNodeAsync(request.TargetId!.Value);
        if (node?.State.Node.WorkspaceId != workspace) return null;
        var version = request.SummaryVersionId ?? node.State.Node.CurrentVersionId;
        if (!node.State.Versions.Any(v => v.Identity.Id == version)) return null;
        return request with { SummaryVersionId = version };
    }
    private async Task<bool> SaveSummarySelectionAsync(Guid workspace, NavigationRequest request, bool strict)
    {
        if (summaryStore is null || request.Kind != ContentKind.Summary) return false;
        try
        {
            var valid = await ValidateSummaryRequestAsync(workspace, request) ?? throw new ArgumentException("总结不存在或不属于当前主题。");
            var state = await summaryStore.LoadSummarySelectionAsync();
            var result = await summaryStore.SaveSummarySelectionAsync(new(workspace, valid.TargetId!.Value, valid.SummaryVersionId!.Value), state.Revision);
            if (result.Kind is not (SummaryOutcomeKind.Applied or SummaryOutcomeKind.NoChange)) throw new InvalidOperationException("总结阅读位置已经变化。");
            pendingSelection = null; pendingSummarySelection = null;
        }
        catch (ArgumentException) { throw; }
        catch { pendingSelection = null; pendingSummarySelection = (workspace, request); MarkPersistenceFailure(); ErrorMessage = "总结阅读位置尚未保存，请重试保存。"; if (strict) throw; }
        return true;
    }
    private async Task RepairSummarySelectionAsync()
    {
        if (pendingSummarySelection is not { } pending) return;
        if (await ValidateSummaryRequestAsync(pending.Workspace, pending.Request) is null) { pendingSummarySelection = null; return; }
        await SaveSummarySelectionAsync(pending.Workspace, pending.Request, true);
    }
    private async Task<SummaryViewModel> CreateSummaryAsync(NavigationRequest request)
    {
        var id = request.TargetId!.Value;
        if (!summaryDrafts.TryGetValue(id, out var draft)) summaryDrafts[id] = draft = new(summaryStore!, id, MarkPersistenceFailure);
        var page = new SummaryViewModel(CurrentWorkspace!.Id, id, summaryStore!, draft,
            () => HasPersistenceFailure || closing || IsBusy, MarkPersistenceFailure,
            conversation => NavigateAsync(new(MainArea.Explore, ContentKind.Conversation, conversation)),
            version => naming!.NotifyCurrentChangedAsync(id, version));
        page.ConfigureSummaryStop(summaries!.StopAsync);
        page.NodeDeleted += () => Dispatcher.UIThread.Post(() => TrackBackgroundRefresh(() => RunAsync(async () =>
        {
            if (ActiveContent != page) return;
            DeactivatePage(); ContentStack.Clear();
            ContentStack.Add(await CreateContentAsync(new(MainArea.Network)));
            await SaveSelectionAsync(page.WorkspaceId, new(MainArea.Network)); NotifyContent();
        })));
        await page.LoadAsync(request.SummaryVersionId); summaryPages.Add(page);
        page.VersionSelected += version =>
        {
            if (ActiveContent != page || detached) return;
            page.SetParameter(page.Request with { SummaryVersionId = version });
            _ = PersistSummaryVersionSelectionAsync(page);
        };
        return page;
    }
    private Task PersistSummaryVersionSelectionAsync(SummaryViewModel page) => RunAsync(async () =>
    {
        if (ActiveContent == page) await SaveSelectionAsync(page.WorkspaceId, page.Request);
    });
    private async Task FlushSummariesAsync()
    {
        foreach (var page in summaryPages) await page.RepairVersionOperationAsync();
        foreach (var draft in summaryDrafts.Values) await draft.FlushAsync();
        foreach (var page in summaryPages) await page.FlushReadingAsync();
    }
}
