using System.IO.Compression;
using System.Text;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Export;

namespace Mindweave.Infrastructure.Diagnostics;

/// <summary>
/// The diagnostic package as one zip (D2 §2.2), through the atomic file write: written beside the target, then moved over
/// it, so a full disk or a refused folder leaves no partial file (design §2.6).
/// </summary>
internal sealed class DiagnosticPackageWriter(IExportFileWriter files) : IDiagnosticPackageWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public Task<ExportWriteMode> WriteAsync(ExportDestination destination, IReadOnlyList<DiagnosticFile> contents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return files.WriteAsync(destination, async (stream, ct) =>
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
            foreach (var file in contents)
            {
                ct.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(file.Path, CompressionLevel.Optimal);
                var target = await entry.OpenAsync(ct).ConfigureAwait(false);
                await using (target.ConfigureAwait(false))
                {
                    await target.WriteAsync(Utf8.GetBytes(file.Text), ct).ConfigureAwait(false);
                }
            }
        }, cancellationToken);
    }
}
