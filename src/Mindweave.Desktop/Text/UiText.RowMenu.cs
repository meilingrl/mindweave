using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// The sidebar conversation row's context menu (2026-11-10): the page toolbar's actions, one right click away.
public static partial class UiText
{
    public static string RowOpen => T("Open", "打开");

    /// <summary>The menu's delete entry ends in "…": a confirmation that states the scope follows.</summary>
    public static string RowDelete => T("Delete…", "删除…");

    /// <summary>The accessible name of the row's inline rename box.</summary>
    public static string RowRenameBox => T("Conversation name", "会话名称");

    /// <summary>Why every entry that writes is disabled while storage is paused (the page says the long form at its top).</summary>
    public static string RowStoragePaused => T(
        "Local data can’t be saved right now. You can do this once saving resumes.",
        "本地数据暂时无法保存，恢复保存后才能操作。");

    public static string RowArchiveBusy => T(
        "This conversation or one of its branches still has answers or summaries generating or queued. You can archive it once they finish or are stopped.",
        "这个会话或它的分支还有回答或总结在生成或排队，完成或停止后才能归档。");

    public static string RowDeleteBusy => T(
        "This conversation or one of its branches still has answers or summaries generating or queued. You can delete it once they finish or are stopped.",
        "这个会话或它的分支还有回答或总结在生成或排队，完成或停止后才能删除。");

    /// <summary>A refusal from a row's menu, named after its conversation (the sidebar may be far from that page).</summary>
    public static string RowNotice(string title, string notice) => T($"“{title}”: {notice}", $"“{title}”：{notice}");

    public static string CloseRowNotice => T("Dismiss", "关闭提示");
}
