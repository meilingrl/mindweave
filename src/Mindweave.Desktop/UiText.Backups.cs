using System.Globalization;
using Mindweave.Application.Startup;

namespace Mindweave.Desktop;

// Restoring a backup from inside the application and removing backups (2026-11-12): 设置 › 数据与存储 › 备份.
public static partial class UiText
{
    public const string BackupBeforeRestore = "恢复前";
    public const string RestoreBackup = "恢复…";
    public const string DeleteBackup = "删除…";
    public const string RestoreBackupConfirm = "恢复";
    public const string LeftoverDatabase = "恢复时替换下的旧数据";
    public const string LeftoverHint = "某次恢复备份前的数据库文件，留在数据目录中，没有列为备份。";
    public const string CloseRestoreNotice = "关闭提示";

    public const string RestoreTaskTitle = "恢复前需要停止任务";
    public const string RestoreTaskConfirm = "停止并恢复";
    public const string RestoreSaving = "正在停止任务并保存……";
    public const string RestoreRunning = "正在备份当前数据并恢复……";
    public const string RestoreRetry = "重试";
    public const string RestoreSaveFailedBody = "草稿或回答的结束状态没能写入，内容仍保留在程序里。写入成功前不会恢复备份；已停止的任务不会自动恢复。";

    public const string RestoreAllWorkspaces = "所有工作区都会回到那一刻：之后新建、修改或删除的内容都不在恢复后的数据里。";
    public const string RestoreKeepsCurrent = "恢复前，当前数据会先另存为一份“恢复前”备份，之后可以在这个列表里恢复回来。";
    public const string RestoreStopsWork = "正在生成或排队的回答和总结会先停止。";
    public const string RestoreBringsBackDeleted = "恢复会找回当时删除的内容，但其他所有内容也会一起回到那一刻；不能只恢复某一个工作区。";

    public const string BackupsDeleting = "正在删除……";

    /// <summary>"恢复到 2026年3月14日 09:30 的备份？"</summary>
    public static string RestoreTitle(DateTimeOffset at) => string.Format(CultureInfo.CurrentCulture, "恢复到 {0} 的备份？", FormatTime(at));

    /// <summary>The first line of the restore confirmation: when and why the backup was made.</summary>
    public static string RestoreMadeAt(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return string.Format(CultureInfo.CurrentCulture, "这份备份创建于 {0}，{1}。", FormatTime(backup.CreatedAt), BackupMadeWhen(backup.Reason));
    }

    /// <summary>An older schema is upgraded right after the restore, as on startup.</summary>
    public static string RestoreUpgrades(int version) =>
        string.Format(CultureInfo.CurrentCulture, "它的数据结构版本是 {0}，恢复后会像启动时一样自动升级，升级前另做一份备份。", version);

    private static string BackupMadeWhen(BackupReason reason) => reason switch
    {
        BackupReason.BeforeWorkspaceDelete => "在删除工作区前",
        BackupReason.BeforeConversationDelete => "在删除会话前",
        BackupReason.BeforeRestore => "在恢复另一份备份前",
        _ => "在数据升级前",
    };

    /// <summary>Why a backup cannot be restored; shown in the dialog, nothing was changed.</summary>
    public static string BackupProblemText(BackupProblem problem) => problem switch
    {
        BackupProblem.Missing => "这份备份的文件已不存在，没有做任何改动。",
        BackupProblem.NotMindweave => "这个文件不是 Mindweave 的备份，没有做任何改动。",
        BackupProblem.TooNew => "这份备份来自更新版本的 Mindweave，本程序不能恢复它，没有做任何改动。",
        _ => "这份备份已损坏或不是有效的数据库，没有做任何改动。",
    };

    /// <summary>The disabled 恢复… of a backup written by a newer build, as its tooltip.</summary>
    public static string BackupTooNew(int version, int supported) =>
        string.Format(CultureInfo.CurrentCulture, "来自更新版本的 Mindweave（结构版本 {0}），本程序只支持到 {1}，不能恢复", version, supported);

    public static string RestoreKeptCopyFailed(string detail) =>
        string.Format(CultureInfo.CurrentCulture, "当前数据没能另存为备份（{0}），因此没有恢复，数据未改动。请检查磁盘空间与权限后重试。", detail);

    /// <summary>The non-modal notice once the restored data is open.</summary>
    public static string RestoredNotice(DateTimeOffset at) =>
        string.Format(CultureInfo.CurrentCulture, "已恢复到 {0} 的备份。恢复前的数据已另存为“恢复前”备份，可在 设置 › 数据与存储 中找回。", FormatTime(at));

    /// <summary>The notice when the restore did not happen and the previous data was opened again.</summary>
    public static string RestoreFailedNotice(DateTimeOffset at, RestoreFailure failure) =>
        string.Format(CultureInfo.CurrentCulture, "没能恢复到 {0} 的备份，当前数据未改动。{1}", FormatTime(at), failure switch
        {
            RestoreFailure.BackupUnusable => "备份没有通过检查。",
            RestoreFailure.SwapFailed => "数据库文件没能替换，可能正被占用或没有权限。",
            _ => "备份无法打开或升级。",
        });

    /// <summary>"3 个备份，共 2.8 MB", with the leftover files counted when there are any.</summary>
    public static string BackupsTotal(int backups, int leftovers, long bytes) => leftovers == 0
        ? string.Format(CultureInfo.CurrentCulture, "{0} 个备份，共 {1}", backups, FormatSize(bytes))
        : string.Format(CultureInfo.CurrentCulture, "{0} 个备份和 {1} 份恢复时替换下的旧数据，共 {2}", backups, leftovers, FormatSize(bytes));

    public static string DeleteSelectedBackups(int count, long bytes) =>
        string.Format(CultureInfo.CurrentCulture, "删除所选（{0} 项，{1}）…", count, FormatSize(bytes));

    public static string SelectBackupName(string row) => string.Format(CultureInfo.CurrentCulture, "选择{0}", row);

    public static string RestoreBackupName(string row) => string.Format(CultureInfo.CurrentCulture, "恢复{0}…", row);

    public static string DeleteBackupName(string row) => string.Format(CultureInfo.CurrentCulture, "删除{0}…", row);

    /// <summary>How a row is named to a screen reader: "2026年3月14日 09:30 的备份（删除会话前，412 KB）".</summary>
    public static string BackupRowName(DateTimeOffset at, string? badge, long bytes) => badge is null
        ? string.Format(CultureInfo.CurrentCulture, " {0} 的备份（{1}）", FormatTime(at), FormatSize(bytes))
        : string.Format(CultureInfo.CurrentCulture, " {0} 的备份（{1}，{2}）", FormatTime(at), badge, FormatSize(bytes));

    public static string LeftoverRowName(DateTimeOffset at, long bytes) =>
        string.Format(CultureInfo.CurrentCulture, " {0} {1}（{2}）", FormatTime(at), LeftoverDatabase, FormatSize(bytes));

    public static string DeleteBackupsTitle(int count) => count == 1
        ? "删除这一项？"
        : string.Format(CultureInfo.CurrentCulture, "删除所选的 {0} 项？", count);

    public static string DeleteBackupsBody(int count, long bytes) => count == 1
        ? string.Format(CultureInfo.CurrentCulture, "将释放 {0}。删除后无法撤销。", FormatSize(bytes))
        : string.Format(CultureInfo.CurrentCulture, "共 {0} 项，将释放 {1}。删除后无法撤销。", count, FormatSize(bytes));

    public const string DeleteOnlyCopyOfWorkspace = "其中有删除工作区前的备份：它可能是被删除工作区唯一的副本。";
    public const string DeleteOnlyCopyOfConversations = "其中有删除会话前的备份：它可能是被删除会话唯一的副本。";
    public const string DeleteWayBackFromRestore = "其中有最近一次恢复前的备份：删除后就无法再回到最近一次恢复之前的数据。";
    public const string DeleteLeftoverFiles = "其中有恢复时替换下的旧数据库文件。";

    public static string BackupsDeleteFailed(int failed) =>
        string.Format(CultureInfo.CurrentCulture, "{0} 项没能删除，文件可能正被占用或没有权限；其余已删除。", failed);

    /// <summary>The restore prompt's body, naming user answers and summaries separately, as the exit prompt does.</summary>
    public static string RestoreWithUnfinished(int answers, int summaries)
    {
        var parts = new List<string>(2);
        if (answers > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, "{0} 个回答", answers));
        }

        if (summaries > 0)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, "{0} 个总结", summaries));
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "{0}还没有完成。恢复备份前会停止它们：已收到的回答会保留并标为“已停止”，总结不留新版本，排队的任务会取消。",
            string.Join('、', parts));
    }
}
