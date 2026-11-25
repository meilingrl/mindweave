using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Mindweave.Application.Export;

namespace Mindweave.Desktop.Services;

/// <summary>A kind of file a panel offers.</summary>
/// <param name="Name">As the panel names it, in the interface language.</param>
/// <param name="Extensions">Without the dot; the first is the one a saved file gets.</param>
/// <param name="MediaType">Its media type, when there is one.</param>
/// <param name="AppleType">Its uniform type identifier, when there is one.</param>
internal sealed record FileKind(string Name, IReadOnlyList<string> Extensions, string? MediaType = null, string? AppleType = null);

/// <summary>
/// The system's file panels, the only place that opens one (SeamTests). On macOS they are app-modal: as a sheet on the
/// window (what Avalonia's picker opens there) a panel can stop taking clicks for good. A click while its location menu is
/// open makes macOS bring a hidden window of its own panel service to the front, over the panel (macOS 27.2.1, found with
/// the owner on 2026-11-25; it happens in a program without Avalonia too, and as a separate window that does not block
/// the app). The app itself goes on while an app-modal panel is open: answers, saving and drawing.
/// </summary>
internal static class FilePanels
{
    /// <summary>Asks where to save one file; the panel itself asks before replacing one. Null when cancelled.</summary>
    public static async Task<ExportDestination?> SaveAsync(Window owner, string title, string suggestedFileName, FileKind kind)
    {
        if (MacFilePanel.IsFor(owner, out var window))
        {
            return DestinationOf(await MacFilePanel.SaveAsync(window, title, suggestedFileName, kind.Extensions));
        }

        var type = PickerType(kind);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = kind.Extensions[0],
            FileTypeChoices = [type],
            SuggestedFileType = type,
            ShowOverwritePrompt = true,
        });
        return file is null ? null : DestinationOf(file);
    }

    /// <summary>Asks for files to open, of <paramref name="kind"/> or any; the paths of those on this computer.</summary>
    public static async Task<IReadOnlyList<string>> OpenAsync(TopLevel owner, string title, FileKind? kind)
    {
        if (MacFilePanel.IsFor(owner, out var window))
        {
            return await MacFilePanel.OpenAsync(window, title, kind?.Extensions ?? []);
        }

        var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = kind is null ? null : [PickerType(kind)],
        });
        return [.. picked.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    /// <summary>The destination for a path picked in a system panel; null when it was cancelled.</summary>
    internal static ExportDestination? DestinationOf(string? path) => path is null ? null : new ExportDestination(Path.GetFileName(path), path, null);

    private static ExportDestination DestinationOf(IStorageFile file)
    {
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

    private static FilePickerFileType PickerType(FileKind kind) => new(kind.Name)
    {
        Patterns = [.. kind.Extensions.Select(e => "*." + e)],
        MimeTypes = kind.MediaType is { } media ? [media] : null,
        AppleUniformTypeIdentifiers = kind.AppleType is { } type ? [type] : null,
    };
}
