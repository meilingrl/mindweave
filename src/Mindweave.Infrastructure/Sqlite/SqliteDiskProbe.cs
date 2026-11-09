using Mindweave.Application.Storage;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>The file-system half of the storage probe: whether the disk under the data directory takes writes.</summary>
internal static class SqliteDiskProbe
{
    /// <summary>
    /// Writes <see cref="ProbeBytes"/> to a file in <paramref name="dataDirectory"/>, flushes it through to the disk and
    /// deletes it again. A failure is a <see cref="StorageWriteException"/> classified as read-only, full or I/O error.
    /// </summary>
    public static void Write(string dataDirectory)
    {
        var probe = Path.Combine(dataDirectory, ".storage-probe");
        try
        {
            using (var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(new byte[ProbeBytes]);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new StorageWriteException(StorageFailureKind.ReadOnly, ex.Message, ex);
        }
        catch (IOException ex)
        {
            var kind = IsDiskFull(ex) ? StorageFailureKind.Full : StorageFailureKind.IoError;
            throw new StorageWriteException(kind, ex.Message, ex);
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL on Windows, ENOSPC elsewhere.</summary>
    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27 || ex.HResult == 28;

    /// <summary>How much the probe writes: more than a typical question or summary, so recovery is not declared early.</summary>
    internal const int ProbeBytes = 64 * 1024;
}
