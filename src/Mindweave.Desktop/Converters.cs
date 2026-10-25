using Avalonia.Data.Converters;
using Mindweave.Application.Startup;

namespace Mindweave.Desktop;

public static class Converters
{
    public static FuncValueConverter<BackupInfo?, string> BackupLabel { get; } =
        new(backup => backup is null ? string.Empty : UiText.FormatBackup(backup));
}
