using System.Globalization;
using Mindweave.Application.Startup;

namespace Mindweave.Desktop;

// Local data: the storage pause, the recovery page, the data and storage page and backups.
public static partial class UiText
{
    /// <summary>Core storage failed (requirements §9.3): what is paused, what still works, and that it lifts by itself.</summary>
    /// <summary>Shown above the page when an operation failed in a way nothing on that page handles (health check 2026-11-12).</summary>
    /// <summary>A page could not read what it shows (health check 2026-11-12); what it showed stays.</summary>
    public const string LoadFailed = "有内容没有读取成功，页面显示的可能不是最新的。可以离开页面再回来重试；详情已记入日志。";

    public const string UnexpectedError = "操作没有完成：发生了意外错误，详情已记入日志。可以重试。";

    public const string StoragePaused = "本地数据暂时无法保存，已暂停提问、重试、创建分支和生成总结；已有内容仍可阅读和复制。恢复保存后会自动解除。";

    /// <summary>The reason on an entry disabled while storage is paused.</summary>
    public const string StoragePausedReason = "本地数据暂时无法保存，恢复后可用";

    public const string StorageTitle = "数据与存储";
    public const string StorageDetailsLink = "数据与存储";
    public const string DataDirectoryLabel = "数据目录";
    public const string DatabaseLabel = "数据库";
    public const string SchemaVersionLabel = "数据结构版本";
    public const string BackupsLabel = "备份";
    public const string ChannelLabel = "数据通道";
    public const string OpenDataFolder = "打开数据位置";
    public const string Retry = "重试";
    public const string Exit = "退出";
    public const string RestoreThis = "恢复此备份…";
    public const string ConfirmRestore = "确认恢复";
    public const string AvailableBackups = "可用的备份";

    public static string RecoveryHeading(DatabaseOpenStatus status) => status switch
    {
        DatabaseOpenStatus.Unreadable => "无法读取数据",
        DatabaseOpenStatus.ForeignDatabase => "数据目录中的文件不属于 Mindweave",
        DatabaseOpenStatus.TooNew => "数据来自更新版本的 Mindweave",
        DatabaseOpenStatus.BackupFailed => "升级前备份失败",
        DatabaseOpenStatus.MigrationFailed => "数据升级失败",
        DatabaseOpenStatus.IoError => "无法访问数据目录",
        DatabaseOpenStatus.InUse => "数据正被其他程序使用",
        _ => "无法打开数据",
    };

    public static string RecoveryExplanation(DatabaseOpenResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            DatabaseOpenStatus.Unreadable => "数据库文件损坏或不是有效的数据库。Mindweave 没有修改它。可以检查数据位置，或从备份恢复。",
            DatabaseOpenStatus.ForeignDatabase => "数据目录中的 mindweave.db 是其他程序的数据库。Mindweave 没有修改它；请移走该文件后重试。",
            DatabaseOpenStatus.TooNew => string.Format(
                CultureInfo.CurrentCulture,
                "数据结构版本为 {0}，本程序只支持到 {1}。为避免损坏数据，本程序不会写入。请使用更新的 Mindweave 打开。",
                result.FoundVersion,
                result.SupportedVersion),
            DatabaseOpenStatus.BackupFailed => "数据需要升级，但升级前的备份没有成功，因此没有开始升级，原数据未改动。请检查磁盘空间与权限后重试。",
            DatabaseOpenStatus.MigrationFailed => "升级过程中出错，已整体撤销，原数据保持升级前的状态，升级前的备份也已保存。",
            DatabaseOpenStatus.IoError => "无法读写数据目录。请检查磁盘空间与权限后重试。",
            DatabaseOpenStatus.InUse => "另一个程序（例如数据库查看工具、同步或备份软件）正在使用数据库文件。数据没有损坏，Mindweave 也没有修改它。请关闭那个程序后重试。",
            _ => string.Empty,
        };
    }

    public static string RestoreConsequence(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return string.Format(
            CultureInfo.CurrentCulture,
            "将用 {0} 的备份（结构版本 {1}）替换当前数据库。当前文件会改名保留在数据目录中，不会删除；备份之后的改动不在备份里。",
            FormatTime(backup.CreatedAt),
            backup.SchemaVersion);
    }

    /// <summary>A backup row's second part beside its date: "结构版本 8 · 212 KB", and "· 删除工作区前" for one made before a delete.</summary>
    public static string BackupDetails(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var details = string.Format(CultureInfo.CurrentCulture, "结构版本 {0} · {1:N0} KB", backup.SchemaVersion, backup.SizeBytes / 1024);
        return BackupReasonLabel(backup.Reason) is { } reason ? details + " · " + reason : details;
    }

    public const string DataLocation = "数据位置";

    public static string SchemaVersionBadge(int version) => string.Format(CultureInfo.CurrentCulture, "结构版本 {0}", version);

    public static string FormatBackup(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var label = string.Format(
            CultureInfo.CurrentCulture,
            "{0}　结构版本 {1}　{2:N0} KB",
            FormatTime(backup.CreatedAt),
            backup.SchemaVersion,
            backup.SizeBytes / 1024);
        return BackupReasonLabel(backup.Reason) is { } reason ? label + "　" + reason : label;
    }

    public const string BackupBeforeDelete = "删除工作区前";
    public const string BackupBeforeConversationDelete = "删除会话前";

    /// <summary>The badge of a backup made before a delete; null for an upgrade backup.</summary>
    public static string? BackupReasonLabel(BackupReason reason) => reason switch
    {
        BackupReason.BeforeWorkspaceDelete => BackupBeforeDelete,
        BackupReason.BeforeConversationDelete => BackupBeforeConversationDelete,
        BackupReason.BeforeRestore => BackupBeforeRestore,
        _ => null,
    };
}
