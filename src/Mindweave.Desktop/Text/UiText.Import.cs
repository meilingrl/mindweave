using System.Globalization;
using Mindweave.Application.Import;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Bringing a shared file in (docs/design/sharing-import.md §5): the entries, the dialog, and what came from a share.
public static partial class UiText
{
    /// <summary>The entry in the workspace menu.</summary>
    public static string ImportEntry => T("Import…", "导入…");

    /// <summary>The dialog's title, and the file panel's.</summary>
    public static string ImportTitle => T("Import a shared file", "导入分享文件");

    /// <summary>What kind of share it is.</summary>
    public static string ImportKindStructure => T(
        "Structure only: titles and branch lines, no questions or answers. The conversations arrive empty, not explored yet.",
        "只有结构：标题和分支连线，没有提问和回答。会话导入后是空的，尚未探索。");

    public static string ImportIntoHeading => T("Import into", "导入到");

    public static string ImportIntoNew(string name) => T("A new workspace, “" + name + "”", "新工作区“" + name + "”");

    public static string ImportIntoCurrent(string name) => T("The current workspace, “" + name + "”", "当前工作区“" + name + "”");

    public static string ImportIntoExisting(string name) => T("The workspace “" + name + "”, which already has most of it", "工作区“" + name + "”，其中已有大部分会话");

    /// <summary>The new workspace's name when the share's title cannot be one.</summary>
    public static string ImportedWorkspace => T("Shared path", "分享的思考路径");

    public static string ImportCountsHeading => T("What will happen", "将会怎样");

    public static string ImportNew(int count) => T(
        Plural(count, "conversation is", "conversations are") + " new and will be added",
        string.Format(CultureInfo.InvariantCulture, "新增 {0} 个会话", count));

    public static string ImportHere(int count) => T(
        Plural(count, "conversation is", "conversations are") + " already here and will stay as " + (count == 1 ? "it is" : "they are"),
        string.Format(CultureInfo.InvariantCulture, "{0} 个会话已在这里，保持不变", count));

    public static string ImportSkipped(int count, string workspace) => T(
        Plural(count, "conversation", "conversations") + " will be skipped: already in the workspace “" + workspace + "”, or a branch of one that is",
        string.Format(CultureInfo.InvariantCulture, "跳过 {0} 个会话：已在工作区“{1}”中，或是其中会话的分支", count, workspace));

    /// <summary>A workspace that is not listed any more.</summary>
    public static string ImportOtherWorkspace => T("another workspace", "其他工作区");

    public static string ImportAllHere => T("Everything in this file is already here. Nothing to import.", "这个文件里的会话都已存在，没有可导入的内容。");

    public static string ImportAllHereIn(string workspace, int count) => T(
        Plural(count, "conversation", "conversations") + " in the workspace “" + workspace + "”",
        string.Format(CultureInfo.InvariantCulture, "工作区“{1}”中有 {0} 个会话", count, workspace));

    public static string ImportConfirm => T("Import", "导入");

    public static string ImportBusy => T("Importing…", "正在导入……");

    /// <summary>Why a file is not imported (§7).</summary>
    public static string ImportRefused(ImportRefusal refusal) => refusal switch
    {
        ImportRefusal.NotAShare => T("This isn’t a file shared from Mindweave.", "这不是从 Mindweave 分享的文件。"),
        ImportRefusal.Damaged => T("This file is damaged and can’t be imported.", "这个文件已损坏，无法导入。"),
        ImportRefusal.NewerFormat => T("This file was made by a newer Mindweave. Update Mindweave to open this.", "这个文件由更新版本的 Mindweave 生成。请更新 Mindweave 后再打开。"),
        ImportRefusal.DeviceCopy => T("This is a copy of a workspace for another device. A newer Mindweave is needed to import it.", "这是给另一台设备的工作区副本，需要更新版本的 Mindweave 才能导入。"),
        ImportRefusal.WithContent => T("This share includes conversation content. This version imports structure-only shares.", "这个分享包含会话内容。当前版本只能导入只有结构的分享。"),
        ImportRefusal.TooLarge => T("This file is too large to import.", "这个文件太大，无法导入。"),
        _ => T("Couldn’t read this file.", "无法读取这个文件。"),
    };

    public static string ImportWorkspaceMissing => T("That workspace was deleted meanwhile. Nothing was imported.", "这个工作区已被删除，没有导入任何内容。");

    public static string ImportDoesNotFit => T(
        "This file continues a conversation that isn’t here, so nothing was imported.",
        "这个文件里有会话的上级不在这里，没有导入任何内容。");

    public static string ImportFailed => T("Nothing was imported. Close this and try again.", "没有导入任何内容。请关闭后重试。");

    public static string ImportStoragePaused => T("Saving is paused, so nothing was imported.", "保存已暂停，没有导入任何内容。");

    /// <summary>A received conversation without messages, in the sidebar, the list and the details.</summary>
    public static string NotExplored => T("Not explored yet", "尚未探索");

    /// <summary>Where a received conversation came from, in its details.</summary>
    public static string ReceivedFrom(string title, DateTimeOffset at, DateOnly? today = null)
    {
        var day = DayLabel(DateOnly.FromDateTime(at.ToLocalTime().DateTime), today ?? DateOnly.FromDateTime(DateTime.Now));
        return T("From a shared file “" + title + "”, imported on " + day, "来自分享文件“" + title + "”，" + day + "导入");
    }

    /// <summary>In the empty input of a received conversation (decision I4).</summary>
    public static string AskInReceivedPlaceholder => T("Ask here to start a conversation of your own…", "在这里提问，开始你自己的会话…");

    public static string LegendUnexplored => T("Hollow: not explored yet", "空心：尚未探索");
}
