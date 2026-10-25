using System.Globalization;
using Mindweave.Application.Startup;
using Mindweave.Core.Workspaces;

namespace Mindweave.Desktop;

/// <summary>All user-facing text in one place, so wording stays consistent and can be localized later.</summary>
public static class UiText
{
    public const string AppName = "Mindweave";
    public const string StartupOpening = "正在打开数据…";
    public const string OnboardingHeading = "新建一个主题工作区";
    public const string OnboardingBody = "工作区按主题组织一次或一类探索中的会话与总结。之后可以随时新建或切换。";
    public const string WorkspaceNameWatermark = "例如：火车历史";
    public const string Create = "创建";
    public const string Cancel = "取消";
    public const string NewWorkspace = "新建工作区";
    public const string SectionExplore = "探索";
    public const string SectionNetwork = "总结网络";
    public const string SectionSettings = "设置";
    public const string Back = "后退";
    public const string ExploreEmpty = "这个工作区还没有会话。";
    public const string NetworkTitle = "总结网络";
    public const string NetworkEmpty = "还没有总结。保存总结后，它们会按探索来源出现在这里。";
    public const string SettingsTitle = "设置";
    public const string StorageTitle = "数据与存储";
    public const string StorageDetailsLink = "数据与存储…";
    public const string DataDirectoryLabel = "数据目录";
    public const string DatabaseLabel = "数据库";
    public const string SchemaVersionLabel = "数据结构版本";
    public const string BackupsLabel = "升级前备份";
    public const string NoBackups = "暂无备份。只有在数据需要升级时才会自动备份。";
    public const string AppVersionLabel = "版本";
    public const string ChannelLabel = "数据通道";
    public const string OpenDataFolder = "打开数据位置";
    public const string Retry = "重试";
    public const string Exit = "退出";
    public const string RestoreThis = "恢复此备份…";
    public const string ConfirmRestore = "确认恢复";
    public const string AvailableBackups = "可用的备份";

    public static string WorkspaceNameError(WorkspaceNameError error) => error switch
    {
        Core.Workspaces.WorkspaceNameError.Empty => "名称不能为空。",
        Core.Workspaces.WorkspaceNameError.TooLong => string.Format(CultureInfo.CurrentCulture, "名称最多 {0} 个字符。", WorkspaceName.MaxLength),
        _ => "名称无效。",
    };

    public static string SaveFailed(string detail) => $"保存失败，输入内容已保留：{detail}";

    public static string RecoveryHeading(DatabaseOpenStatus status) => status switch
    {
        DatabaseOpenStatus.Unreadable => "无法读取数据",
        DatabaseOpenStatus.ForeignDatabase => "数据目录中的文件不属于 Mindweave",
        DatabaseOpenStatus.TooNew => "数据来自更新版本的 Mindweave",
        DatabaseOpenStatus.BackupFailed => "升级前备份失败",
        DatabaseOpenStatus.MigrationFailed => "数据升级失败",
        DatabaseOpenStatus.IoError => "无法访问数据目录",
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
            DatabaseOpenStatus.MigrationFailed => "升级过程中出错，已整体撤销，原数据保持升级前的状态。升级前备份已保存，可在下方查看。",
            DatabaseOpenStatus.IoError => "无法读写数据目录。请检查磁盘空间与权限后重试。",
            _ => string.Empty,
        };
    }

    public static string RestoreConsequence(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return string.Format(
            CultureInfo.CurrentCulture,
            "将用 {0:yyyy-MM-dd HH:mm} 的备份（结构版本 {1}）替换当前数据库。当前文件会改名保留在数据目录中，不会删除；备份之后的改动不在备份里。",
            backup.CreatedAt.ToLocalTime(),
            backup.SchemaVersion);
    }

    public static string FormatBackup(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return string.Format(
            CultureInfo.CurrentCulture,
            "{0:yyyy-MM-dd HH:mm}　结构版本 {1}　{2:N0} KB",
            backup.CreatedAt.ToLocalTime(),
            backup.SchemaVersion,
            backup.SizeBytes / 1024);
    }
}
