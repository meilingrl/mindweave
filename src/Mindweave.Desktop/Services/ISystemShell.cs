using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Mindweave.Application.Export;

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

    /// <summary>
    /// The system save dialog for a Markdown export, offering <paramref name="suggestedFileName"/>. The dialog itself
    /// asks before replacing an existing file (§8.3), so the app does not ask again. Null when cancelled.
    /// </summary>
    Task<ExportDestination?> PickExportFileAsync(string suggestedFileName, CancellationToken cancellationToken);
}

internal sealed class SystemShell : ISystemShell
{
    private static readonly FilePickerFileType Markdown = new(UiText.MarkdownFileType)
    {
        Patterns = ["*.md"],
        MimeTypes = ["text/markdown"],
        AppleUniformTypeIdentifiers = ["net.daringfireball.markdown"],
    };

    /// <summary>
    /// Shows the folder in the system's file manager. The opener is named by its full path, so a program of the same name
    /// earlier on the search path is never run (low finding gap-native-interop-5); a failure to start it is logged, not
    /// thrown: the page shows the path either way.
    /// </summary>
    public void OpenFolder(string path)
    {
        var opener = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
            : OperatingSystem.IsMacOS() ? "/usr/bin/open"
            : "/usr/bin/xdg-open";
        var info = new ProcessStartInfo(opener) { UseShellExecute = false };
        info.ArgumentList.Add(path);
        try
        {
            using var _ = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            DesktopLog.Swallowed(ex);
        }
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

    public async Task<ExportDestination?> PickExportFileAsync(string suggestedFileName, CancellationToken cancellationToken)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return null;
        }

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = UiText.ExportMarkdown,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "md",
            FileTypeChoices = [Markdown],
            SuggestedFileType = Markdown,
            ShowOverwritePrompt = true,
        });
        if (file is null)
        {
            return null;
        }

        var name = file.Name;
        if (file.TryGetLocalPath() is { } path)
        {
            file.Dispose();
            return new ExportDestination(name, path, null);
        }

        // Only a stream (sandboxed or virtual location): the writer falls back to writing in place.
        return new ExportDestination(name, null, async _ =>
        {
            try
            {
                return await file.OpenWriteAsync();
            }
            finally
            {
                file.Dispose();
            }
        });
    }
}
