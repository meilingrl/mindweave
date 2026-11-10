namespace Mindweave.Desktop;

// The sidebar conversation row's context menu (2026-11-10): the page toolbar's actions, one right click away.
public static partial class UiText
{
    public const string RowOpen = "打开";

    /// <summary>The menu's delete entry ends in "…": a confirmation that states the scope follows.</summary>
    public const string RowDelete = "删除…";

    /// <summary>The accessible name of the row's inline rename box.</summary>
    public const string RowRenameBox = "会话名称";

    /// <summary>Why every entry that writes is disabled while storage is paused (the page says the long form at its top).</summary>
    public const string RowStoragePaused = "本地数据暂时无法保存，恢复保存后才能操作。";

    public const string RowArchiveBusy = "这个会话或它的分支还有回答或总结在生成或排队，完成或停止后才能归档。";

    public const string RowDeleteBusy = "这个会话或它的分支还有回答或总结在生成或排队，完成或停止后才能删除。";

    /// <summary>A refusal from a row's menu, named after its conversation (the sidebar may be far from that page).</summary>
    public static string RowNotice(string title, string notice) => $"“{title}”：{notice}";

    public const string CloseRowNotice = "关闭提示";
}
