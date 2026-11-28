using System.Globalization;
using Mindweave.Application.Startup;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Local data: the storage pause, the recovery page, the data and storage page and backups.
public static partial class UiText
{
    /// <summary>Core storage failed (requirements §9.3): what is paused, what still works, and that it lifts by itself.</summary>
    /// <summary>Shown above the page when an operation failed in a way nothing on that page handles (health check 2026-11-12).</summary>
    public static string StartupFailedTitle => T("Mindweave can’t start", "Mindweave 无法启动");

    public static string StartupFailed(string detail) => T(
        string.Format(Culture, "Something went wrong while starting; your data was not changed. Check the permissions and free disk space of the data location, then try again.\n\n{0}", detail),
        string.Format(CultureInfo.CurrentCulture, "启动时出错，数据没有被修改。请检查数据位置的权限与磁盘空间后重试。\n\n{0}", detail));

    public static string AlreadyRunningNotResponding(string dataDirectory) => T(
        string.Format(Culture, "Mindweave is already using this data location but is not responding:\n{0}\n\nClose the running Mindweave (if needed, end it in Activity Monitor or Task Manager), then try again.", dataDirectory),
        string.Format(CultureInfo.CurrentCulture, "Mindweave 已在使用这个数据位置，但没有响应：\n{0}\n\n请关闭正在运行的 Mindweave（必要时在活动监视器或任务管理器中结束它）后重试。", dataDirectory));

    /// <summary>A page could not read what it shows (health check 2026-11-12); what it showed stays.</summary>
    public static string LoadFailed => T(
        "Some content couldn’t be read, so the page may not be up to date. You can leave the page and come back to try again; the details are in the log.",
        "有内容没有读取成功，页面显示的可能不是最新的。可以离开页面再回来重试；详情已记入日志。");

    public static string UnexpectedError => T(
        "The operation did not finish: an unexpected error occurred, and the details are in the log. You can try again.",
        "操作没有完成：发生了意外错误，详情已记入日志。可以重试。");

    public static string StoragePaused => T(
        "Local data can’t be saved right now, so asking, retrying, creating branches and making summaries are paused; you can still read and copy existing content. This lifts automatically once saving works again.",
        "本地数据暂时无法保存，已暂停提问、重试、创建分支和生成总结；已有内容仍可阅读和复制。恢复保存后会自动解除。");

    /// <summary>The reason on an entry disabled while storage is paused.</summary>
    public static string StoragePausedReason => T(
        "Local data can’t be saved right now; available again once saving works",
        "本地数据暂时无法保存，恢复后可用");

    public static string StorageTitle => T("Data and storage", "数据与存储");
    public static string StorageDetailsLink => T("Data and storage", "数据与存储");
    public static string DataDirectoryLabel => T("Data folder", "数据目录");
    public static string DatabaseLabel => T("Database", "数据库");
    public static string SchemaVersionLabel => T("Data format version", "数据结构版本");
    public static string BackupsLabel => T("Backups", "备份");
    public static string ChannelLabel => T("Data channel", "数据通道");
    public static string OpenDataFolder => T("Open data location", "打开数据位置");
    public static string LogFolderLabel => T("Log", "日志");
    public static string OpenLogFolder => T("Open log folder", "打开日志文件夹");
    public static string LogFolderHint => T(
        "One file per day, the last 14 days. It holds errors and timings, never conversation text or keys.",
        "每天一个文件，保留最近 14 天。记录错误与耗时，不含对话内容和密钥。");
    public static string Retry => T("Retry", "重试");
    public static string Exit => T("Quit", "退出");
    public static string RestoreThis => T("Restore this backup…", "恢复此备份…");
    public static string ConfirmRestore => T("Confirm restore", "确认恢复");
    public static string AvailableBackups => T("Available backups", "可用的备份");

    public static string RecoveryHeading(DatabaseOpenStatus status) => status switch
    {
        DatabaseOpenStatus.Unreadable => T("Can’t read the data", "无法读取数据"),
        DatabaseOpenStatus.ForeignDatabase => T("The file in the data folder doesn’t belong to Mindweave", "数据目录中的文件不属于 Mindweave"),
        DatabaseOpenStatus.TooNew => T("The data is from a newer version of Mindweave", "数据来自更新版本的 Mindweave"),
        DatabaseOpenStatus.BackupFailed => T("The backup before the upgrade failed", "升级前备份失败"),
        DatabaseOpenStatus.MigrationFailed => T("The data upgrade failed", "数据升级失败"),
        DatabaseOpenStatus.IoError => T("Can’t access the data folder", "无法访问数据目录"),
        DatabaseOpenStatus.InUse => T("The data is in use by another program", "数据正被其他程序使用"),
        _ => T("Can’t open the data", "无法打开数据"),
    };

    public static string RecoveryExplanation(DatabaseOpenResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            DatabaseOpenStatus.Unreadable => T(
                "The database file is damaged or is not a valid database. Mindweave did not change it. You can check the data location or restore from a backup.",
                "数据库文件损坏或不是有效的数据库。Mindweave 没有修改它。可以检查数据位置，或从备份恢复。"),
            DatabaseOpenStatus.ForeignDatabase => T(
                "The file mindweave.db in the data folder is another program’s database. Mindweave did not change it; move the file away, then try again.",
                "数据目录中的 mindweave.db 是其他程序的数据库。Mindweave 没有修改它；请移走该文件后重试。"),
            DatabaseOpenStatus.TooNew => T(
                string.Format(
                    Culture,
                    "The data format version is {0}, but this app supports only up to {1}. To avoid damaging the data, this app will not write to it. Open it with a newer Mindweave. If you restore an older backup here instead, the newer data is not deleted: it stays in the data folder as old data replaced by a restore, and the newer Mindweave can restore it from Settings › Data and storage.",
                    result.FoundVersion,
                    result.SupportedVersion),
                string.Format(
                    CultureInfo.CurrentCulture,
                    "数据结构版本为 {0}，本程序只支持到 {1}。为避免损坏数据，本程序不会写入。请使用更新的 Mindweave 打开。如果在这里改为恢复一份较早的备份，较新的数据不会删除：它作为“恢复时替换下的旧数据”留在数据目录中，更新的 Mindweave 可以在 设置 › 数据与存储 中把它恢复回来。",
                    result.FoundVersion,
                    result.SupportedVersion)),
            DatabaseOpenStatus.BackupFailed => T(
                "The data needs an upgrade, but the backup before the upgrade failed, so the upgrade did not start and the original data is unchanged. Check disk space and permissions, then try again.",
                "数据需要升级，但升级前的备份没有成功，因此没有开始升级，原数据未改动。请检查磁盘空间与权限后重试。"),
            DatabaseOpenStatus.MigrationFailed => T(
                "Something went wrong during the upgrade, so the whole upgrade was undone. The original data is as it was before the upgrade, and the backup made before the upgrade has been kept.",
                "升级过程中出错，已整体撤销，原数据保持升级前的状态，升级前的备份也已保存。"),
            DatabaseOpenStatus.IoError => T(
                "Can’t read or write the data folder. Check disk space and permissions, then try again.",
                "无法读写数据目录。请检查磁盘空间与权限后重试。"),
            DatabaseOpenStatus.InUse => T(
                "Another program (for example, a database viewer, or sync or backup software) is using the database file. The data is not damaged, and Mindweave did not change it. Close that program, then try again.",
                "另一个程序（例如数据库查看工具、同步或备份软件）正在使用数据库文件。数据没有损坏，Mindweave 也没有修改它。请关闭那个程序后重试。"),
            _ => string.Empty,
        };
    }

    public static string RestoreConsequence(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return T(
            string.Format(
                Culture,
                "The current database will be replaced with the backup from {0} (data format version {1}). The current file is renamed and kept in the data folder, not deleted; changes made after the backup are not in it.",
                FormatTime(backup.CreatedAt),
                backup.SchemaVersion),
            string.Format(
                CultureInfo.CurrentCulture,
                "将用 {0} 的备份（结构版本 {1}）替换当前数据库。当前文件会改名保留在数据目录中，不会删除；备份之后的改动不在备份里。",
                FormatTime(backup.CreatedAt),
                backup.SchemaVersion));
    }

    /// <summary>A backup row's second part beside its date: "结构版本 8 · 212 KB", and "· 删除工作区前" for one made before a delete.</summary>
    public static string BackupDetails(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var details = T(
            string.Format(Culture, "Schema version {0} · {1:N0} KB", backup.SchemaVersion, backup.SizeBytes / 1024),
            string.Format(CultureInfo.CurrentCulture, "结构版本 {0} · {1:N0} KB", backup.SchemaVersion, backup.SizeBytes / 1024));
        return BackupReasonLabel(backup.Reason) is { } reason ? details + " · " + reason : details;
    }

    public static string DataLocation => T("Data location", "数据位置");

    /// <summary>A backup's data format version as a short badge beside its date: "Format 17", "结构版本 17".</summary>
    public static string SchemaVersionBadge(int version) => T(
        string.Format(Culture, "Format {0}", version),
        string.Format(CultureInfo.CurrentCulture, "结构版本 {0}", version));

    public static string BackupBeforeDelete => T("Before deleting a workspace", "删除工作区前");
    public static string BackupBeforeConversationDelete => T("Before deleting a conversation", "删除会话前");

    /// <summary>The badge of a backup made before a delete; null for an upgrade backup.</summary>
    public static string? BackupReasonLabel(BackupReason reason) => reason switch
    {
        BackupReason.BeforeWorkspaceDelete => BackupBeforeDelete,
        BackupReason.BeforeConversationDelete => BackupBeforeConversationDelete,
        BackupReason.BeforeRestore => BackupBeforeRestore,
        BackupReason.Replaced => LeftoverDatabase,
        _ => null,
    };

    /// <summary>The start-up page while the data is upgraded (gap-schema-4, D-042).</summary>
    public static string StartupUpgrading(DataUpgradeStarting step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.BackupFirst
            ? T(
                string.Format(Culture, "Upgrading data (format version {0} to {1}); a backup is made first…", step.From, step.To),
                string.Format(CultureInfo.CurrentCulture, "正在升级数据（结构版本 {0} 到 {1}），会先做一份备份……", step.From, step.To))
            : T(
                string.Format(Culture, "Upgrading data (format version {0} to {1})…", step.From, step.To),
                string.Format(CultureInfo.CurrentCulture, "正在升级数据（结构版本 {0} 到 {1}）……", step.From, step.To));
    }

    /// <summary>The one notice after an upgrade: to which version, and where the copy from before it is.</summary>
    public static string UpgradedNotice(DataUpgrade upgrade)
    {
        ArgumentNullException.ThrowIfNull(upgrade);
        return upgrade.BackupMadeNow
            ? T(
                string.Format(Culture, "Your data was upgraded to format version {0}. A backup of it from before the upgrade (version {1}) is in Settings › Data and storage.", upgrade.To, upgrade.From),
                string.Format(CultureInfo.CurrentCulture, "数据已升级到结构版本 {0}。升级前（结构版本 {1}）的备份在 设置 › 数据与存储 中。", upgrade.To, upgrade.From))
            : T(
                string.Format(Culture, "Your data was upgraded to format version {0}. The backup it came from (version {1}) stays as it was in Settings › Data and storage.", upgrade.To, upgrade.From),
                string.Format(CultureInfo.CurrentCulture, "数据已升级到结构版本 {0}。恢复所用的备份（结构版本 {1}）保持原样，在 设置 › 数据与存储 中。", upgrade.To, upgrade.From));
    }
}
