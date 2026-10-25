using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;

namespace Mindweave.Desktop.Services;

/// <summary>Operating-system side effects the UI may request.</summary>
public interface ISystemShell
{
    void OpenFolder(string path);

    void ExitApplication();
}

internal sealed class SystemShell : ISystemShell
{
    public void OpenFolder(string path)
    {
        var opener = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        var info = new ProcessStartInfo(opener) { UseShellExecute = false };
        info.ArgumentList.Add(path);
        using var _ = Process.Start(info);
    }

    public void ExitApplication()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
