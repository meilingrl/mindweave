using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Diagnostics;

namespace Mindweave.Infrastructure.Logging;

/// <summary>
/// Minimal daily-rolling file logger. Writes are serialized and flushed per entry so the log survives a
/// forced kill. Logs contain identifiers and errors, never message bodies or credentials; as a last line of
/// defence, every entry (exception text included) is scrubbed of credential-shaped tokens and of every key this process
/// has read or written (<see cref="KnownSecrets"/>, D2) before it is written.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int DaysKept = 14;

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly KnownSecrets _secrets;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;

    public FileLoggerProvider(string directory, TimeProvider time, KnownSecrets secrets)
    {
        _directory = directory;
        _time = time;
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (CannotWrite(ex))
        {
            // Logging must never stop the application; writes simply fail quietly below.
        }

        Prune();
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = _time.GetUtcNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(' ').Append(LevelTag(level))
            .Append(' ').Append(category)
            .Append(": ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_gate)
        {
            try
            {
                WriterFor(DateOnly.FromDateTime(now.UtcDateTime)).WriteLine(_secrets.Scrub(line.ToString()));
            }
            catch (Exception ex) when (CannotWrite(ex))
            {
                // Logging must never take the application down (low findings observability-9, cross-platform-10: a
                // read-only or denied log folder raised UnauthorizedAccessException, which was not caught). The broken
                // writer is let go, its file handle with it.
                try
                {
                    _writer?.Dispose();
                }
                catch (Exception closing) when (CannotWrite(closing))
                {
                }

                _writer = null;
            }
        }
    }

    private StreamWriter WriterFor(DateOnly date)
    {
        if (_writer is null || _writerDate != date)
        {
            _writer?.Dispose();
            var path = Path.Combine(_directory, $"mindweave-{date:yyyyMMdd}.log");
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            _writerDate = date;
        }

        return _writer;
    }

    private void Prune()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(_directory, "mindweave-*.log").OrderDescending().Skip(DaysKept))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (CannotWrite(ex))
        {
        }
    }

    private static bool CannotWrite(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
