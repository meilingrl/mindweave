using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Export;

namespace Mindweave.Infrastructure.Export;

/// <summary>
/// Writes an export atomically (§8.3): a temporary file in the target's folder (same volume, so the move is a rename)
/// is written exclusively and flushed to disk, then moved over the target. The target is either untouched or fully
/// replaced. Windows refuses the move while another program holds the target (an editor, a sync client, a virus
/// scanner), usually briefly, so sharing violations and access denials are retried for <see cref="RetryWindow"/>
/// before being reported. Every failure removes the temporary file.
/// </summary>
public sealed partial class AtomicFileWriter(ILogger<AtomicFileWriter> logger) : IExportFileWriter
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    public TimeSpan RetryWindow { get; init; } = TimeSpan.FromSeconds(1.5);

    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The replacing move; tests substitute it to simulate a locked target on any platform.</summary>
    internal Action<string, string> Move { get; init; } = static (temp, target) => File.Move(temp, target, overwrite: true);

    public async Task<ExportWriteMode> WriteAsync(ExportDestination destination, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.LocalPath is { } path)
        {
            await WriteAtomicAsync(Path.GetFullPath(path), content, cancellationToken).ConfigureAwait(false);
            return ExportWriteMode.Atomic;
        }

        if (destination.OpenWrite is not { } open)
        {
            throw new ArgumentException("The destination has neither a local path nor a stream.", nameof(destination));
        }

        // No path to put a temporary file beside: write in place, which is not atomic.
        LogNotAtomic(logger, destination.DisplayName);
        await WriteDirectAsync(open, content, cancellationToken).ConfigureAwait(false);
        return ExportWriteMode.Direct;
    }

    private async Task WriteAtomicAsync(string target, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        // A fixed short name, so a long target name cannot push the temporary one past the file system's limit.
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".mindweave-export-{Guid.NewGuid():N}.tmp");
        try
        {
            try
            {
                using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(content.Span);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Classify(ex, null);
            }

            await MoveWithRetryAsync(temp, target, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private async Task MoveWithRetryAsync(string temp, string target, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                Move(temp, target);
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                if (Stopwatch.GetElapsedTime(started) >= RetryWindow)
                {
                    throw Classify(ex, target);
                }

                LogRetrying(logger, ex);
            }
            catch (IOException ex)
            {
                throw Classify(ex, target);
            }

            await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteDirectAsync(Func<CancellationToken, Task<Stream>> open, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await open(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                if (stream.CanSeek)
                {
                    stream.SetLength(0);
                }

                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Classify(ex, null);
        }
    }

    private static bool IsTransient(Exception ex) => ex is UnauthorizedAccessException || IsSharingViolation(ex);

    private static bool IsSharingViolation(Exception ex) =>
        ex is IOException && (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    /// <summary>
    /// Windows reports a target held by another program as either a sharing violation or "access denied". An access
    /// denial on a target that is not marked read-only is taken as held by another program (an editor holding it
    /// without delete sharing still lets readers in, so a read probe cannot tell).
    /// </summary>
    private static ExportWriteException Classify(Exception ex, string? target)
    {
        if (IsSharingViolation(ex) || (ex is UnauthorizedAccessException && target is not null && File.Exists(target) && !IsReadOnly(target)))
        {
            return new ExportWriteException(ExportFailure.InUse, ex.Message, ex);
        }

        return ex is UnauthorizedAccessException
            ? new ExportWriteException(ExportFailure.NotWritable, ex.Message, ex)
            : new ExportWriteException(ExportFailure.Failed, ex.Message, ex);
    }

    private static bool IsReadOnly(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void TryDelete(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogTempNotDeleted(logger, ex, temp);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Export to {Name} is written in place (no local path); not atomic")]
    private static partial void LogNotAtomic(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Export target busy; retrying")]
    private static partial void LogRetrying(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Temporary export file {Path} could not be removed")]
    private static partial void LogTempNotDeleted(ILogger logger, Exception exception, string path);
}
