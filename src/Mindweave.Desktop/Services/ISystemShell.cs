using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;

namespace Mindweave.Desktop.Services;

/// <summary>Operating-system side effects the UI may request.</summary>
public interface ISystemShell
{
    void OpenFolder(string path);

    /// <summary>
    /// The final, programmatic shutdown. Only <see cref="ViewModels.ExitCoordinator"/> calls it, once everything is
    /// saved; every other way of quitting goes through the coordinator first.
    /// </summary>
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

    /// <summary>
    /// <c>TryShutdown</c>, not <c>Shutdown</c>: in Avalonia 12 <c>Shutdown()</c> forces the exit, skipping the lifetime's
    /// <c>ShutdownRequested</c> and closing windows even when their <c>Closing</c> handler cancels — it would bypass
    /// the exit sequence. By the time the coordinator calls this both hooks let the request through.
    /// </summary>
    public void ExitApplication()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.TryShutdown();
        }
    }
}
