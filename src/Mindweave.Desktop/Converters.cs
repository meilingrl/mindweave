using Avalonia.Data.Converters;
using Mindweave.Application.Startup;

namespace Mindweave.Desktop;

public static class Converters
{
    public static FuncValueConverter<bool, double> SupersededOpacity { get; } = new(superseded => superseded ? 0.55 : 1.0);

    public static FuncValueConverter<int, bool> IsPositive { get; } = new(count => count > 0);

    public static FuncValueConverter<BackupInfo?, string> BackupLabel { get; } =
        new(backup => backup is null ? string.Empty : UiText.FormatBackup(backup));
}
