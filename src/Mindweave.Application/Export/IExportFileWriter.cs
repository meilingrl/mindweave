namespace Mindweave.Application.Export;

/// <summary>
/// The file the user picked in the save dialog. Desktop files have a <see cref="LocalPath"/>; a picker that only
/// hands out a stream (sandboxed or virtual locations) gives <see cref="OpenWrite"/> instead.
/// </summary>
/// <param name="DisplayName">The file name, for telling the user where it went.</param>
public sealed record ExportDestination(string DisplayName, string? LocalPath, Func<CancellationToken, Task<Stream>>? OpenWrite);

public enum ExportWriteMode
{
    /// <summary>Written to a temporary file in the same folder, then moved over the target.</summary>
    Atomic,

    /// <summary>Written straight into the picked file's stream; a failure midway can leave it partial.</summary>
    Direct,
}

public enum ExportFailure
{
    /// <summary>Another program holds the file and did not let go within the retry window.</summary>
    InUse,

    /// <summary>The file or its folder is read-only, or the user may not write there.</summary>
    NotWritable,

    /// <summary>Anything else (missing folder, disk full); <see cref="ExportWriteException.Detail"/> says what.</summary>
    Failed,
}

/// <summary>Writing the export failed; the target is as it was before and no temporary file is left.</summary>
public sealed class ExportWriteException(ExportFailure failure, string detail, Exception? innerException)
    : Exception(detail, innerException)
{
    public ExportFailure Failure { get; } = failure;

    public string Detail { get; } = detail;
}

/// <summary>Writes an exported file so that the target is either left alone or completely replaced.</summary>
public interface IExportFileWriter
{
    /// <exception cref="ExportWriteException">The file could not be written.</exception>
    Task<ExportWriteMode> WriteAsync(ExportDestination destination, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>
    /// Writes what <paramref name="write"/> puts into the stream it is given, for a file too large to hold in memory (a manual
    /// backup). The same promise: the target is left alone or completely replaced, and a cancel or a failure of
    /// <paramref name="write"/> leaves no temporary file. This default holds the content in memory first.
    /// </summary>
    /// <exception cref="ExportWriteException">The file could not be written.</exception>
    async Task<ExportWriteMode> WriteAsync(ExportDestination destination, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var buffer = new MemoryStream();
        await write(buffer, cancellationToken).ConfigureAwait(false);
        return await WriteAsync(destination, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
