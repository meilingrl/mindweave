using Avalonia.Data.Converters;
using Mindweave.Application.Startup;

namespace Mindweave.Desktop.Views;

/// <summary>A backup row in two parts: its date (the round-2 date format) and its version and size.</summary>
public static class BackupFormat
{
    public static FuncValueConverter<BackupInfo?, string> Time { get; } =
        new(backup => backup is null ? string.Empty : UiText.FormatTime(backup.CreatedAt));

    public static FuncValueConverter<BackupInfo?, string> Details { get; } =
        new(backup => backup is null ? string.Empty : UiText.BackupDetails(backup));
}
