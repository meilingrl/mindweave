using System.Text;
using Mindweave.Application.Diagnostics;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Logging;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Diagnostics;

/// <summary>Counts, sizes and log files for the diagnostic package (D2 §2.2); nothing here reads what the user wrote.</summary>
internal sealed class DiagnosticFacts(SqliteDatabase database, AppPaths paths) : IDiagnosticFacts
{
    /// <summary>A log file larger than this is read from its end: the newest lines matter most.</summary>
    internal const int MaxLogBytes = 8 * 1024 * 1024;

    public Task<DataCounts> CountAsync(CancellationToken cancellationToken) =>
        database.ReadAsync(
            connection =>
            {
                long Count(string table)
                {
                    using var command = connection.Command(null, $"SELECT count(*) FROM {table};");
                    return (long)command.ExecuteScalar()!;
                }

                var resources = new List<(string, long)>();
                using (var command = connection.Command(null, "SELECT origin, count(*) FROM resource GROUP BY origin ORDER BY origin;"))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        resources.Add((reader.GetString(0), reader.GetInt64(1)));
                    }
                }

                return new DataCounts(Count("workspace"), Count("conversation"), Count("message"), Count("summary_node"), Count("summary_version"), resources);
            },
            cancellationToken);

    public IReadOnlyList<SizeOnDisk> Sizes()
    {
        var sizes = new List<SizeOnDisk>();
        AddFile("database", paths.DatabasePath);
        AddFile("database write-ahead log", paths.DatabasePath + "-wal");
        AddFolder("attachments and saved pages", paths.ResourceDirectory);
        AddFolder("backups", paths.BackupDirectory);
        AddFolder("logs", paths.LogDirectory);
        return sizes;

        void AddFile(string what, string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    sizes.Add(new SizeOnDisk(what, new FileInfo(path).Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left out: the summary lists what could be measured.
            }
        }

        void AddFolder(string what, string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    sizes.Add(new SizeOnDisk(what, new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left out, as above.
            }
        }
    }

    public IReadOnlyList<LogFile> ReadLogs()
    {
        var logs = new List<LogFile>();
        IEnumerable<string> files;
        try
        {
            files = Directory.Exists(paths.LogDirectory)
                ? [.. Directory.EnumerateFiles(paths.LogDirectory, "mindweave-*.log").Order(StringComparer.Ordinal).TakeLast(FileLoggerProvider.DaysKept)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return logs;
        }

        foreach (var file in files)
        {
            if (Read(file) is { } text)
            {
                logs.Add(new LogFile(Path.GetFileName(file), text));
            }
        }

        return logs;
    }

    /// <summary>Read as far as it goes, alongside the logger that may be writing it (design §2.6).</summary>
    private static string? Read(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxLogBytes)
            {
                stream.Seek(-MaxLogBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
