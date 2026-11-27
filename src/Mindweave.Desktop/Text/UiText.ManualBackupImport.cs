using System.Globalization;
using Mindweave.Application.Import;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Importing a manual backup (docs/design/sharing-backup.md §2, §5) through the same "Import…" and drop as a share. It only
// adds; it is never called sync, nor restoring one of the automatic backups.
public static partial class UiText
{
    /// <summary>The file panel's title: it takes either kind of file.</summary>
    public static string ImportPanelTitle => T("Import a shared file or a manual backup", "导入分享文件或手动备份");

    /// <summary>The file panel's name for both kinds.</summary>
    public static string ImportFileType => T("Shared file or manual backup", "分享文件或手动备份");

    /// <summary>The dialog's title for a manual backup.</summary>
    public static string ImportBackupTitle => T("Import a manual backup", "导入手动备份");

    /// <summary>Under the file's name: when it was saved, and what importing it does.</summary>
    public static string ImportBackupKind(DateTimeOffset madeAt, DateOnly? today = null)
    {
        var day = DayLabel(DateOnly.FromDateTime(madeAt.ToLocalTime().DateTime), today ?? DateOnly.FromDateTime(DateTime.Now));
        return T(
            "Saved on " + day + ". Everything in it is your own. Importing only adds what is missing here: nothing here is changed or deleted, and where the two differ, what is here stays.",
            day + "保存。其中的内容都是你自己的。导入只补上这里缺少的：不改动、不删除这里已有的；两边不一致时，保留这里的。");
    }

    /// <summary>A workspace of the backup, heading its lines.</summary>
    public static string ImportBackupWorkspace(string name, bool isNew) => isNew
        ? T("“" + name + "”, a new workspace", "“" + name + "”，新工作区")
        : T("“" + name + "”", "“" + name + "”");

    public static string ImportBackupJoined(int count) => T(
        Plural(count, "conversation", "conversations") + " went on both here and in the backup: the questions will be joined into one line, in the order they were asked",
        string.Format(CultureInfo.InvariantCulture, "{0} 个会话在这里和备份中都继续过：两边的提问会按提问的先后合成一条", count));

    public static string ImportBackupElsewhere(int count) => T(
        Plural(count, "conversation is", "conversations are") + " in another workspace here and will stay there",
        string.Format(CultureInfo.InvariantCulture, "{0} 个会话在这里的其他工作区中，留在原处", count));

    public static string ImportBackupMismatched(int count) => T(
        Plural(count, "attachment original in the file is", "attachment originals in the file are") + " damaged. " + (count == 1 ? "That attachment arrives" : "Those attachments arrive") + " without " + (count == 1 ? "its original" : "their originals") + ".",
        string.Format(CultureInfo.InvariantCulture, "文件中有 {0} 个附件原件已损坏，这些附件导入后没有原件。", count));

    public static string ImportBackupAllHere => T("Everything in this backup is already here. Nothing to import.", "这份备份中的内容都已在这里，没有可导入的内容。");

    public static string ImportBackupRunning => T(
        "An answer is being written in a conversation this backup adds to. Let it finish or stop it, then import.",
        "有个回答正在生成，而这份备份会给那个会话增加内容。请等它完成或停止后再导入。");

    /// <summary>After the import, the heading of what was done.</summary>
    public static string ImportBackupDoneHeading => T("Imported", "已导入");

    public static string ImportBackupDoneNew(int count) => T(
        Plural(count, "conversation was", "conversations were") + " added",
        string.Format(CultureInfo.InvariantCulture, "新增了 {0} 个会话", count));

    public static string ImportBackupDoneGained(int count) => T(
        Plural(count, "conversation", "conversations") + " gained messages",
        string.Format(CultureInfo.InvariantCulture, "{0} 个会话增加了消息", count));

    public static string ImportBackupDoneJoined(int count) => T(
        Plural(count, "conversation was", "conversations were") + " joined into one line by when the questions were asked",
        string.Format(CultureInfo.InvariantCulture, "{0} 个会话按提问的先后合成了一条", count));

    public static string ImportBackupDoneMore(int count) => T(
        Plural(count, "conversation", "conversations") + " gained summary versions or the sentence " + (count == 1 ? "it" : "they") + " left from",
        string.Format(CultureInfo.InvariantCulture, "{0} 个会话增加了总结版本或分出的原句", count));

    public static string ImportBackupDoneLinks(int count) => T(
        Plural(count, "link between summaries was", "links between summaries were") + " added",
        string.Format(CultureInfo.InvariantCulture, "新增了 {0} 条总结之间的关联", count));

    public static string ImportBackupDoneMissing(int count) => T(
        Plural(count, "attachment arrived", "attachments arrived") + " without " + (count == 1 ? "its original" : "their originals"),
        string.Format(CultureInfo.InvariantCulture, "{0} 个附件导入后没有原件", count));

    /// <summary>Why a package is not imported (§6).</summary>
    public static string ImportBackupRefused(ImportRefusal refusal) => refusal switch
    {
        ImportRefusal.NotAShare => T("This isn’t a manual backup from Mindweave.", "这不是 Mindweave 的手动备份。"),
        ImportRefusal.Damaged => T("This backup is damaged and can’t be imported. Nothing was changed.", "这份备份已损坏，无法导入，没有改动任何内容。"),
        ImportRefusal.NewerFormat => T("This backup was made by a newer Mindweave. Update Mindweave to import it.", "这份备份由更新版本的 Mindweave 生成。请更新 Mindweave 后再导入。"),
        ImportRefusal.TooLarge => T("This backup holds more than Mindweave can import at once.", "这份备份的内容超出了 Mindweave 一次能导入的范围。"),
        _ => T("Couldn’t read this file.", "无法读取这个文件。"),
    };
}
