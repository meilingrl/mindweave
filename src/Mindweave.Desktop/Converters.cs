using Avalonia.Data.Converters;
using Mindweave.Application.Startup;

namespace Mindweave.Desktop;

public static class Converters
{
    /// <summary>The switcher's small square (2026-11-10): the workspace name's first character as a reader sees it (an emoji stays whole).</summary>
    public static FuncValueConverter<Mindweave.Core.Workspaces.Workspace?, string> WorkspaceInitial { get; } = new(workspace =>
        workspace is null || workspace.Name.Value.Length == 0
            ? string.Empty
            : System.Globalization.StringInfo.GetNextTextElement(workspace.Name.Value).ToUpperInvariant());

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
}
