using Avalonia.Data.Converters;
using Mindweave.Application.Conversations;
using Mindweave.Application.Startup;
using Mindweave.Desktop.Views;

namespace Mindweave.Desktop;

public static class Converters
{
    public static FuncValueConverter<bool, double> SupersededOpacity { get; } = new(superseded => superseded ? 0.55 : 1.0);

    /// <summary>Unread names are bold (S7 已读／未读约定).</summary>
    public static FuncValueConverter<bool, Avalonia.Media.FontWeight> BoldWhen { get; } = new(bold => bold ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);

    public static FuncValueConverter<int, bool> IsPositive { get; } = new(count => count > 0);

    /// <summary>The read toggle says what pressing it does.</summary>
    public static FuncValueConverter<bool, string> ReadToggleLabel { get; } = new(read => read ? UiText.MarkUnread : UiText.MarkRead);

    public static FuncValueConverter<BackupInfo?, string> BackupLabel { get; } =
        new(backup => backup is null ? string.Empty : UiText.FormatBackup(backup));

    // S7 round 3 · settings: a backup row's columns on the storage page.
    public static FuncValueConverter<DateTimeOffset, string> Time { get; } = new(UiText.FormatTime);

    public static FuncValueConverter<long, string> FileSize { get; } = new(UiText.FormatSize);

    public static FuncValueConverter<int, string> SchemaVersion { get; } = new(UiText.SchemaVersionBadge);

    // Round 4 · topic-delete: a backup made before a delete (a workspace, or conversations with what went along) is marked in its row.
    public static FuncValueConverter<BackupReason, bool> IsBeforeDelete { get; } = new(reason => UiText.BackupReasonLabel(reason) is not null);

    public static FuncValueConverter<BackupReason, string?> BackupReasonLabel { get; } = new(UiText.BackupReasonLabel);

    /// <summary>The database lives in the data directory shown just above it, so its row shows the file name (full path on hover).</summary>
    public static FuncValueConverter<string?, string> FileName { get; } = new(path => Path.GetFileName(path) ?? string.Empty);

    /// <summary>
    /// A running answer's weaving motion (docs/design/generating-indicator.md), from its phase and whether text has
    /// arrived: the thread once text streams in; before that the braid, joining while thinking, tightening while compressing.
    /// </summary>
    public static FuncMultiValueConverter<object?, WeavingMotion> AnswerMotion { get; } = new(values =>
    {
        var list = values.ToList();
        if (list is [_, true, ..])
        {
            return WeavingMotion.Thread;
        }

        return list.FirstOrDefault() switch
        {
            AnswerPhase.Thinking => WeavingMotion.Joining,
            AnswerPhase.Compressing => WeavingMotion.Tightening,
            _ => WeavingMotion.Braid,
        };
    });
}
