using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Mindweave.Application.Export;

namespace Mindweave.Desktop.Services;

/// <summary>Operating-system side effects the UI may request.</summary>
public interface ISystemShell
{
    void OpenFolder(string path);

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
