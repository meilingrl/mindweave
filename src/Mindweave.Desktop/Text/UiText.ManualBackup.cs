using System.Globalization;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// The manual backup (docs/design/sharing-backup.md §5): everything in the user's workspaces as one file they keep. It is
// never called sync, and it is not one of the automatic backups in Settings › Data and storage.
public static partial class UiText
{
    /// <summary>The entry in the workspace menu.</summary>
    public static string ManualBackupEntry => T("Back up to a file…", "备份到文件…");

    /// <summary>The dialog's title, and the save panel's.</summary>
    public static string ManualBackupTitle => T("Back up to a file", "备份到文件");

    public static string ManualBackupIntro => T(
        "A manual backup is one file with everything in the ticked workspaces, attachments included. It is meant for your own keeping and your own devices, so keep it somewhere safe.",
        "手动备份是一个文件，包含所勾选工作区的全部内容，附件也在其中。它供你自己保存，或带到你自己的其他设备上，请妥善保管。");

    public static string ManualBackupLeavesOut => T(
        "Not in the file: keys, connections and their settings, drafts, reading positions, and answers still being written.",
        "文件中不含：密钥、连接及其设置、草稿、阅读位置，以及仍在生成的回答。");

    public static string ManualBackupNotAutomatic => T(
        "This is not one of the automatic backups the app keeps for itself in Settings › Data and storage.",
        "它不同于应用自己在 设置 › 数据与存储 中保留的自动备份。");

    public static string ManualBackupWorkspacesHeading => T("Workspaces", "工作区");

    public static string ManualBackupConversations(int count) => T(
        Plural(count, "conversation", "conversations"),
        string.Format(CultureInfo.CurrentCulture, "{0} 个会话", count));

    /// <summary>The size of the ticked workspaces before compression: the file is usually smaller.</summary>
    public static string ManualBackupSize(long bytes) => T("Up to about " + FormatSize(bytes), "最多约 " + FormatSize(bytes));

    public static string ManualBackupNothingTicked => T("Tick at least one workspace.", "请至少勾选一个工作区。");

    public static string ManualBackupSave => T("Save…", "保存…");

    public static string ManualBackupReading => T("Reading the workspaces…", "正在读取工作区……");

    public static string ManualBackupProgress(int done, int total) => T(
        string.Format(EnglishCulture, "Saving… {0} of {1} workspaces", done, total),
        string.Format(CultureInfo.CurrentCulture, "正在保存……已完成 {0}/{1} 个工作区", done, total));

    public static string ManualBackupSaved(string file) => T("Saved to “" + file + "”.", "已保存到“" + file + "”。");

    public static string ManualBackupMissingOriginals(int count) => T(
        " " + Plural(count, "attachment original was", "attachment originals were") + " missing on this device; the file says so.",
        string.Format(CultureInfo.CurrentCulture, "有 {0} 个附件原件在本机已缺失，文件中记录为缺失。", count));

    public static string ManualBackupCancelled => T("Cancelled. No file was saved.", "已取消，没有保存文件。");

    public static string ManualBackupReadFailed => T("Couldn’t read the workspaces. Close and try again.", "无法读取工作区。请关闭后重试。");

    /// <summary>The file panel's name for the package.</summary>
    public static string ManualBackupFileType => T("Mindweave manual backup", "Mindweave 手动备份");
}
