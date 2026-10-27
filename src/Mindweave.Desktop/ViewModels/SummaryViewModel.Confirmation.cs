using System.Collections.Immutable;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels;

public sealed partial class SummaryViewModel
{
    private async Task OpenDeletionAsync()
    {
        var node = await store.LoadSummaryNodeAsync(NodeId);
        if (node is null) { ClearRemovedBody(); ErrorMessage = "总结已删除。"; return; }
        Snapshot = node; DeletionSelection.Clear(); DeletableVersions.Clear();
        foreach (var header in node.State.Versions.Where(v => v.Identity.Id != node.State.Node.CandidateVersionId).OrderByDescending(v => v.Identity.Ordinal)) DeletableVersions.Add(header);
        previewIntent = null; ResetConfirmation(); IsSelectingDeletion = true; IsConfirmationOpen = true;
        ConfirmationMessage = "选择要永久删除的已保存版本（可多选）。下一步会列出确切版本、当前版本回退和不可恢复后果。候选请单独丢弃。";
    }
    private static string Label(SummaryVersionState state, Guid? id) => state.Versions.FirstOrDefault(v => v.Identity.Id == id) is { } h
        ? $"版本 {h.Identity.Ordinal}「{h.Title}」" : "已不存在的版本";
    private async Task PreviewAsync(VersionOperation kind, Guid? target = null, ImmutableArray<Guid> selection = default, bool changed = false)
    {
        var node = await store.LoadSummaryNodeAsync(NodeId);
        ResetConfirmation(); IsConfirmationOpen = true;
        if (node is null) { previewIntent = null; ClearRemovedBody(); ConfirmationMessage = "该总结已删除，无法继续操作。"; return; }
        Snapshot = node; var state = node.State;
        var token = state.Token;
        SummaryDeletionPreview? deletion = null; SummaryReplacementConfirmation? replacement = null;
        bool affectsCurrent = kind is VersionOperation.Adopt or VersionOperation.Restore;
        string message;
        switch (kind)
        {
            case VersionOperation.Adopt:
                target ??= state.Node.CandidateVersionId;
                if (target is null || target != state.Node.CandidateVersionId) { ConfirmationMessage = "原候选已变化或已被处理，请取消后重新选择。"; previewIntent = null; return; }
                var candidate = state.Versions.Single(v => v.Identity.Id == target);
                var baselineChanged = candidate.BaselineVersionId != state.Node.CurrentVersionId;
                if (baselineChanged) replacement = new(token, target.Value, state.Node.CurrentVersionId);
                message = $"采用候选 {Label(state, target)}，替换当前 {Label(state, state.Node.CurrentVersionId)}。原当前版本保留为历史；候选的阅读状态保留。";
                if (baselineChanged) message = "候选生成后当前基线已改变，须再次确认替换。\n" + message;
                previewAllowed = true; break;
            case VersionOperation.DiscardCandidate:
                target ??= state.Node.CandidateVersionId;
                if (target is null || target != state.Node.CandidateVersionId) { ConfirmationMessage = "原候选已变化或已被处理，请取消后重新选择。"; previewIntent = null; return; }
                message = $"永久丢弃候选 {Label(state, target)}。正文、来源记录及阅读记录会实际删除，不可恢复。当前 {Label(state, state.Node.CurrentVersionId)} 和当前编辑草稿保留。";
                previewAllowed = true; break;
            case VersionOperation.Restore:
                if (!state.Versions.Any(v => v.Identity.Id == target) || target == state.Node.CandidateVersionId)
                { ConfirmationMessage = "所选历史版本已变化，请取消后重新选择。"; previewIntent = null; return; }
                message = $"将 {Label(state, target)} 设为当前，替换 {Label(state, state.Node.CurrentVersionId)}。所有版本正文和各自阅读状态保留。";
                previewAllowed = true; break;
            case VersionOperation.Delete:
                var result = await store.PreviewDeleteSummaryVersionsAsync(NodeId, selection);
                deletion = result.Value;
                if (deletion is null) { ConfirmationMessage = "请选择有效的已保存版本；候选须单独丢弃。"; previewIntent = null; return; }
                // The store preview is authoritative, including a possible race with the first read.
                if (deletion.Token != token) { await PreviewAsync(kind, target, selection, true); return; }
                affectsCurrent = deletion.Impact.ChangesCurrent;
                message = "永久删除：\n" + string.Join("\n", deletion.VersionIds.Select(id => "• " + Label(state, id))) + "\n正文、来源记录和阅读记录会实际删除，不可恢复。\n" +
                    (deletion.DeletesNode ? "这是最后的已保存版本：总结节点也将删除；来源会话、消息和分支仍然保留。" : deletion.Impact.ChangesCurrent
                        ? $"删除当前后自动回退到 {Label(state, deletion.FallbackVersionId)}，无需先手动切换当前版本。" : $"当前 {Label(state, state.Node.CurrentVersionId)} 保持不变。");
                previewAllowed = result.Kind == SummaryOutcomeKind.Applied; break;
            default:
                IsRenamingNode = true; DisplayNameInput = state.Node.DisplayName;
                message = $"修改节点显示名（原显示名「{state.Node.DisplayName}」）。版本内容标题「{state.Node.BaseTitle}」、正文与阅读状态不变。手动显示名受保护，不随采用、恢复或后台命名改变。";
                previewAllowed = true; break;
        }
        previewIntent = new(kind, token, target, selection, replacement, deletion, state.Node.NameRevision);
        if (affectsCurrent)
        {
            NeedsDraftResolution = Draft.IsDirty || Draft.HasMemoryChanges || Draft.HasPendingCommit || state.HasDirtyDraft;
            displayedActiveTasks = state.ActiveSummaryTaskIds;
            NeedsActiveSummaryResolution = !displayedActiveTasks.IsEmpty;
            NeedsCandidateResolution = kind != VersionOperation.Adopt && state.Node.CandidateVersionId is not null;
            if (NeedsDraftResolution)
            {
                Draft.StopDebounce(); draftResolution = await Draft.CaptureResolutionAsync();
                message += "\n当前有未解决草稿。请先保存，或明确丢弃下方草稿（不可恢复），或取消；保存失败会保留全部输入。";
            }
            if (NeedsActiveSummaryResolution) message += "\n当前仍有总结任务；须先明确停止并核对完成结果，再重新预览。";
            if (NeedsCandidateResolution) message += "\n当前存在候选版本；请先查看并采用或单独确认丢弃候选，再重试本操作。";
        }
        ConfirmationMessage = (changed ? "状态已变化，旧确认已拒绝。请核对新预览并再次确认。\n" : "") + message;
        RefreshAvailability();
    }
    public string ResolutionDraftTitle => draftResolution?.Title ?? "";
    public string ResolutionDraftBody => draftResolution?.Markdown ?? "";
    private void ResetConfirmation()
    {
        IsSelectingDeletion = IsRenamingNode = NeedsDraftResolution = NeedsActiveSummaryResolution = NeedsCandidateResolution = false;
        previewAllowed = false; draftResolution = null;
    }
    private void CloseConfirmation() { previewIntent = null; ResetConfirmation(); IsConfirmationOpen = false; ConfirmationMessage = ""; RefreshAvailability(); }
}
