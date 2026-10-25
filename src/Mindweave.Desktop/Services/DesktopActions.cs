using System.Diagnostics;

namespace Mindweave.Desktop.Services;

public sealed class DesktopActions(string dataDirectory, Action exit) : IDesktopActions
{
    public Task OpenDataDirectoryAsync() => Task.Run(() => Process.Start(new ProcessStartInfo(dataDirectory) { UseShellExecute = true }));
    public void Exit() => exit();
}
