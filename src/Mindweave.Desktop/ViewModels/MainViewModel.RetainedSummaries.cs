using System.Collections.ObjectModel;
using Mindweave.Core.Summaries;

namespace Mindweave.Desktop.ViewModels;

public sealed record RetainedSummaryResult(Guid TaskId, Guid WorkspaceId, Guid ConversationId,
    string Owner, SummaryContent Content)
{
    public string Title => Content.Title;
    public string Markdown => Content.Markdown;
    public string Notice => "完整总结响应，尚未确认保存。可选择并复制下方标题和正文；请重试保存，不要重新生成。";
}

public sealed partial class MainViewModel
{
    // Application-owned recovery memory, independent of business routes and saved-version reading.
    // Keep each card stable while the same exact completion is retained, preserving selection/copy.
    public ObservableCollection<RetainedSummaryResult> RetainedSummaries { get; } = [];
    public bool HasRetainedSummaries => RetainedSummaries.Count != 0;
    private void RefreshRetainedSummaries()
    {
        if (summaries is null || detached) return;
        var retained = summaries.Snapshots.Where(s => s.RetainedContent is not null).ToArray();
        foreach (var old in RetainedSummaries.Where(r => !retained.Any(s => s.TaskId == r.TaskId)).ToArray()) RetainedSummaries.Remove(old);
        foreach (var snapshot in retained)
        {
            if (RetainedSummaries.Any(r => r.TaskId == snapshot.TaskId)) continue;
            var workspace = Workspaces.FirstOrDefault(w => w.Id == snapshot.WorkspaceId)?.Name ?? snapshot.WorkspaceId.ToString();
            var conversation = snapshot.WorkspaceId == CurrentWorkspace?.Id
                ? Conversations.FirstOrDefault(c => c.Id == snapshot.ConversationId)?.DisplayTitle : null;
            RetainedSummaries.Add(new(snapshot.TaskId, snapshot.WorkspaceId, snapshot.ConversationId,
                $"主题：{workspace} · 会话：{conversation ?? snapshot.ConversationId.ToString()} · 总结任务：{snapshot.TaskId}", snapshot.RetainedContent!));
        }
        OnPropertyChanged(nameof(HasRetainedSummaries));
    }
}
