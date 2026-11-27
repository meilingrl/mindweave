using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
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

    /// <summary>The same dialog for a share, which is one HTML file (docs/design/sharing.md S2). Null when cancelled.</summary>
    Task<ExportDestination?> PickShareFileAsync(string suggestedFileName, CancellationToken cancellationToken);

    /// <summary>
    /// The same dialog for a manual backup, a package of its own kind (docs/design/sharing-backup.md §4), offering
    /// <paramref name="suggestedFileName"/> with the package's extension. Null when cancelled.
    /// </summary>
    Task<ExportDestination?> PickBackupFileAsync(string suggestedFileName, CancellationToken cancellationToken);

    /// <summary>The system open dialog for one shared file to import: its path, or null when cancelled.</summary>
    Task<string?> PickSharedFileAsync(CancellationToken cancellationToken);
}

internal sealed class SystemShell : ISystemShell
{
    /// <summary>Built when the dialog opens, so its label is in the current interface language.</summary>
    private static FileKind Markdown() => new(UiText.MarkdownFileType, ["md"], "text/markdown", "net.daringfireball.markdown");

    private static FileKind Html() => new(UiText.HtmlFileType, ["html"], "text/html", "public.html");

    /// <summary>
    /// Shows the folder in the system's file manager. The opener is named by its full path, so a program of the same name
    /// earlier on the search path is never run (low finding gap-native-interop-5); a failure to start it is logged, not
    /// thrown: the page shows the path either way.
    /// </summary>
    public void OpenFolder(string path)
    {
        var opener = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
            : OperatingSystem.IsMacOS() ? "/usr/bin/open"
            : File.Exists("/usr/bin/xdg-open") ? "/usr/bin/xdg-open" : "xdg-open";
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

    public Task<ExportDestination?> PickExportFileAsync(string suggestedFileName, CancellationToken cancellationToken) =>
        PickFileAsync(UiText.ExportMarkdown, suggestedFileName, Markdown());

    public Task<ExportDestination?> PickShareFileAsync(string suggestedFileName, CancellationToken cancellationToken) =>
        PickFileAsync(UiText.ShareTitle, suggestedFileName, Html());

    /// <summary>The package's extension is the writer's (<c>ManualBackupPackage.FileExtension</c>); Desktop names it once, here.</summary>
    public Task<ExportDestination?> PickBackupFileAsync(string suggestedFileName, CancellationToken cancellationToken) =>
        PickFileAsync(UiText.ManualBackupTitle, suggestedFileName + "." + BackupExtension, new FileKind(UiText.ManualBackupFileType, [BackupExtension], "application/zip"));

    /// <summary>The manual backup's own extension (docs/design/sharing-backup.md §4).</summary>
    internal const string BackupExtension = "mwbackup";

    public async Task<string?> PickSharedFileAsync(CancellationToken cancellationToken) =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            && await FilePanels.OpenAsync(window, UiText.ImportTitle, Html() with { Extensions = ["html", "htm"] }, multiple: false) is [var path, ..]
            ? path
            : null;

    private static async Task<ExportDestination?> PickFileAsync(string title, string suggestedFileName, FileKind kind) =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? await FilePanels.SaveAsync(window, title, suggestedFileName, kind)
            : null;
}
