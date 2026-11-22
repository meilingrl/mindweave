using System.Globalization;
using Mindweave.Application.Startup;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Restoring a backup from inside the application and removing backups (2026-11-12): 设置 › 数据与存储 › 备份.
public static partial class UiText
{
    public static string BackupBeforeRestore => T("Before restore", "恢复前");
    public static string RestoreBackup => T("Restore…", "恢复…");
    public static string DeleteBackup => T("Delete…", "删除…");
    public static string RestoreBackupConfirm => T("Restore", "恢复");
    public static string LeftoverDatabase => T("Old data replaced by a restore", "恢复时替换下的旧数据");
    public static string LeftoverHint => T(
        "A database file from before a backup was restored. It stays in the data folder and is not listed as a backup.",
        "某次恢复备份前的数据库文件，留在数据目录中，没有列为备份。");
    public static string CloseRestoreNotice => T("Dismiss", "关闭提示");

    public static string RestoreTaskTitle => T("Tasks need to stop before restoring", "恢复前需要停止任务");
    public static string RestoreTaskConfirm => T("Stop and restore", "停止并恢复");
    public static string RestoreSaving => T("Stopping tasks and saving…", "正在停止任务并保存……");
    public static string RestoreRunning => T("Backing up current data and restoring…", "正在备份当前数据并恢复……");
    public static string RestoreRetry => T("Retry", "重试");
    public static string RestoreSaveFailedBody => T(
        "A draft or the final state of an answer couldn’t be written; the content is still kept in the app. The backup won’t be restored until writing succeeds, and stopped tasks won’t resume on their own.",
        "草稿或回答的结束状态没能写入，内容仍保留在程序里。写入成功前不会恢复备份；已停止的任务不会自动恢复。");

    public static string RestoreAllWorkspaces => T(
        "Every workspace goes back to that moment: anything created, changed or deleted after it won’t be in the restored data.",
        "所有工作区都会回到那一刻：之后新建、修改或删除的内容都不在恢复后的数据里。");
    public static string RestoreKeepsCurrent => T(
        "Before restoring, your current data is first saved as a “Before restore” backup, which you can restore from this list later.",
        "恢复前，当前数据会先另存为一份“恢复前”备份，之后可以在这个列表里恢复回来。");
    public static string RestoreStopsWork => T(
        "Answers and summaries being generated or queued are stopped first.",
        "正在生成或排队的回答和总结会先停止。");
    public static string RestoreBringsBackDeleted => T(
        "Restoring brings back what was deleted then, but everything else also goes back to that moment; you can’t restore just one workspace.",
        "恢复会找回当时删除的内容，但其他所有内容也会一起回到那一刻；不能只恢复某一个工作区。");

    public static string BackupsDeleting => T("Deleting…", "正在删除……");

    /// <summary>"恢复到 2026年3月14日 09:30 的备份？"</summary>
    public static string RestoreTitle(DateTimeOffset at) => T(
        string.Format(Culture, "Restore the backup from {0}?", FormatTime(at)),
        string.Format(CultureInfo.CurrentCulture, "恢复到 {0} 的备份？", FormatTime(at)));

    /// <summary>The first line of the restore confirmation: when and why the backup was made.</summary>
    public static string RestoreMadeAt(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return T(
            string.Format(Culture, "This backup was saved automatically on {0}, {1}.", FormatTime(backup.CreatedAt), BackupMadeWhen(backup.Reason)),
            string.Format(CultureInfo.CurrentCulture, "这份备份是 {0} {1}自动保存的。", FormatTime(backup.CreatedAt), BackupMadeWhen(backup.Reason)));
    }

    /// <summary>An older schema is upgraded right after the restore, as on startup.</summary>
    public static string RestoreUpgrades(int version) => T(
        string.Format(Culture, "Its data format version is {0}. After restoring, it is upgraded automatically, just as at startup, and another backup is made before the upgrade.", version),
        string.Format(CultureInfo.CurrentCulture, "它的数据结构版本是 {0}，恢复后会像启动时一样自动升级，升级前另做一份备份。", version));

    private static string BackupMadeWhen(BackupReason reason) => reason switch
    {
        BackupReason.BeforeWorkspaceDelete => T("before deleting a workspace", "删除工作区前"),
        BackupReason.BeforeConversationDelete => T("before deleting conversations", "删除会话前"),
        BackupReason.BeforeRestore => T("before restoring another backup", "恢复另一份备份前"),
        _ => T("before a data upgrade", "数据升级前"),
    };

    /// <summary>Why a backup cannot be restored; shown in the dialog, nothing was changed.</summary>
    public static string BackupProblemText(BackupProblem problem) => problem switch
    {
        BackupProblem.Missing => T("This backup’s file no longer exists. Nothing was changed.", "这份备份的文件已不存在，没有做任何改动。"),
        BackupProblem.NotMindweave => T("This file isn’t a Mindweave backup. Nothing was changed.", "这个文件不是 Mindweave 的备份，没有做任何改动。"),
        BackupProblem.TooNew => T(
            "This backup comes from a newer version of Mindweave, and this version can’t restore it. Nothing was changed.",
            "这份备份来自更新版本的 Mindweave，本程序不能恢复它，没有做任何改动。"),
        _ => T("This backup is damaged or isn’t a valid database. Nothing was changed.", "这份备份已损坏或不是有效的数据库，没有做任何改动。"),
    };

    /// <summary>The disabled 恢复… of a backup written by a newer build, as its tooltip.</summary>
    public static string BackupTooNew(int version, int supported) => T(
        string.Format(Culture, "From a newer version of Mindweave (data format version {0}); this version supports up to {1} and can’t restore it", version, supported),
        string.Format(CultureInfo.CurrentCulture, "来自更新版本的 Mindweave（结构版本 {0}），本程序只支持到 {1}，不能恢复", version, supported));

    public static string RestoreKeptCopyFailed => T(
        "Your current data couldn’t be saved as a backup, so nothing was restored and your data is unchanged. Check the disk space and permissions, then try again.",
        "当前数据没能另存为备份，因此没有恢复，数据未改动。请检查磁盘空间与权限后重试。");

    /// <summary>The non-modal notice once the restored data is open.</summary>
    public static string RestoredNotice(DateTimeOffset at) => T(
        string.Format(Culture, "Restored the backup from {0}. Your data from before the restore was saved as a “Before restore” backup; you can find it in Settings › Data and storage.", FormatTime(at)),
        string.Format(CultureInfo.CurrentCulture, "已恢复到 {0} 的备份。恢复前的数据已另存为“恢复前”备份，可在 设置 › 数据与存储 中找回。", FormatTime(at)));

    /// <summary>The notice when the restore did not happen and the previous data was opened again.</summary>
    public static string RestoreFailedNotice(DateTimeOffset at, RestoreFailure failure) => T(
        string.Format(Culture, "Couldn’t restore the backup from {0}; your current data is unchanged. {1}", FormatTime(at), failure switch
        {
            RestoreFailure.BackupUnusable => "The backup didn’t pass the checks.",
            RestoreFailure.SwapFailed => "The database file couldn’t be replaced; it may be in use, or permission may be missing.",
            _ => "The backup couldn’t be opened or upgraded.",
        }),
        string.Format(CultureInfo.CurrentCulture, "没能恢复到 {0} 的备份，当前数据未改动。{1}", FormatTime(at), failure switch
        {
            RestoreFailure.BackupUnusable => "备份没有通过检查。",
            RestoreFailure.SwapFailed => "数据库文件没能替换，可能正被占用或没有权限。",
            _ => "备份无法打开或升级。",
        }));

    /// <summary>"3 个备份，共 2.8 MB", with the leftover files counted when there are any.</summary>
    public static string BackupsTotal(int backups, int leftovers, long bytes) => leftovers == 0
        ? T(
            Plural(backups, "backup", "backups") + ", " + FormatSize(bytes) + " in total",
            string.Format(CultureInfo.CurrentCulture, "{0} 个备份，共 {1}", backups, FormatSize(bytes)))
        : T(
            Plural(backups, "backup", "backups") + " and " + Plural(leftovers, "copy of old data replaced by a restore", "copies of old data replaced by a restore")
                + ", " + FormatSize(bytes) + " in total",
            string.Format(CultureInfo.CurrentCulture, "{0} 个备份和 {1} 份恢复时替换下的旧数据，共 {2}", backups, leftovers, FormatSize(bytes)));

    public static string DeleteSelectedBackups(int count, long bytes) => T(
        "Delete selected (" + Plural(count, "item", "items") + ", " + FormatSize(bytes) + ")…",
        string.Format(CultureInfo.CurrentCulture, "删除所选（{0} 项，{1}）…", count, FormatSize(bytes)));

    // A row's name starts with a space, so these read "Select backup from …" and "选择 2026年3月14日 09:30 的备份…".
    public static string SelectBackupName(string row) => T(
        string.Format(Culture, "Select{0}", row),
        string.Format(CultureInfo.CurrentCulture, "选择{0}", row));

    public static string RestoreBackupName(string row) => T(
        string.Format(Culture, "Restore{0}…", row),
        string.Format(CultureInfo.CurrentCulture, "恢复{0}…", row));

    public static string DeleteBackupName(string row) => T(
        string.Format(Culture, "Delete{0}…", row),
        string.Format(CultureInfo.CurrentCulture, "删除{0}…", row));

    /// <summary>How a row is named to a screen reader: "2026年3月14日 09:30 的备份（删除会话前，412 KB）".</summary>
    public static string BackupRowName(DateTimeOffset at, string? badge, long bytes) => badge is null
        ? T(
            string.Format(Culture, " backup from {0} ({1})", FormatTime(at), FormatSize(bytes)),
            string.Format(CultureInfo.CurrentCulture, " {0} 的备份（{1}）", FormatTime(at), FormatSize(bytes)))
        : T(
            string.Format(Culture, " backup from {0} ({1}, {2})", FormatTime(at), badge, FormatSize(bytes)),
            string.Format(CultureInfo.CurrentCulture, " {0} 的备份（{1}，{2}）", FormatTime(at), badge, FormatSize(bytes)));

    public static string LeftoverRowName(DateTimeOffset at, long bytes) => T(
        string.Format(Culture, " old data replaced by a restore on {0} ({1})", FormatTime(at), FormatSize(bytes)),
        string.Format(CultureInfo.CurrentCulture, " {0} {1}（{2}）", FormatTime(at), LeftoverDatabase, FormatSize(bytes)));

    public static string DeleteBackupsTitle(int count) => count == 1
        ? T("Delete this item?", "删除这一项？")
        : T(
            "Delete the " + Plural(count, "selected item", "selected items") + "?",
            string.Format(CultureInfo.CurrentCulture, "删除所选的 {0} 项？", count));

    public static string DeleteBackupsBody(int count, long bytes) => count == 1
        ? T(
            string.Format(Culture, "Deleting it frees {0}. This can’t be undone.", FormatSize(bytes)),
            string.Format(CultureInfo.CurrentCulture, "将释放 {0}。删除后无法撤销。", FormatSize(bytes)))
        : T(
            Plural(count, "item", "items") + " in all; deleting them frees " + FormatSize(bytes) + ". This can’t be undone.",
            string.Format(CultureInfo.CurrentCulture, "共 {0} 项，将释放 {1}。删除后无法撤销。", count, FormatSize(bytes)));

    public static string DeleteOnlyCopyOfWorkspace => T(
        "These include a backup made before deleting a workspace: it may be the only copy of the deleted workspace.",
        "其中有删除工作区前的备份：它可能是被删除工作区唯一的副本。");
    public static string DeleteOnlyCopyOfConversations => T(
        "These include a backup made before deleting conversations: it may be the only copy of the deleted conversations.",
        "其中有删除会话前的备份：它可能是被删除会话唯一的副本。");
    public static string DeleteWayBackFromRestore => T(
        "These include the backup made before the most recent restore: once it is deleted, you can’t go back to your data from before that restore.",
        "其中有最近一次恢复前的备份：删除后就无法再回到最近一次恢复之前的数据。");
    public static string DeleteLeftoverFiles => T(
        "These include an old database file replaced by a restore: it holds the data from before that restore. A restore made on the startup recovery page leaves no “Before restore” backup, so it may be the only copy of that data.",
        "其中有恢复时替换下的旧数据库文件：它是那次恢复之前的数据，在启动恢复页做的恢复没有“恢复前”备份，它可能是那份数据唯一的副本。");

    public static string BackupsDeleteFailed(int failed) => T(
        Plural(failed, "item", "items") + " couldn’t be deleted; the files may be in use, or permission may be missing. The rest were deleted.",
        string.Format(CultureInfo.CurrentCulture, "{0} 项没能删除，文件可能正被占用或没有权限；其余已删除。", failed));

    /// <summary>The restore prompt's body, naming user answers and summaries separately, as the exit prompt does.</summary>
    public static string RestoreWithUnfinished(int answers, int summaries)
    {
        var parts = new List<string>(2);
        if (answers > 0)
        {
            parts.Add(T(Plural(answers, "answer", "answers"), string.Format(CultureInfo.CurrentCulture, "{0} 个回答", answers)));
        }

        if (summaries > 0)
        {
            parts.Add(T(Plural(summaries, "summary", "summaries"), string.Format(CultureInfo.CurrentCulture, "{0} 个总结", summaries)));
        }

        var single = answers + summaries == 1;
        return T(
            string.Join(" and ", parts) + (single ? " isn’t" : " aren’t") + " finished yet. Restoring the backup stops "
                + (single ? "it" : "them") + " first: answers received so far are kept and marked “Stopped”, summaries keep no new version, and queued tasks are canceled.",
            string.Format(
                CultureInfo.CurrentCulture,
                "{0}还没有完成。恢复备份前会停止它们：已收到的回答会保留并标为“已停止”，总结不留新版本，排队的任务会取消。",
                string.Join('、', parts)));
    }
}
