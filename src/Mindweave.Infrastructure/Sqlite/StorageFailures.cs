using Microsoft.Data.Sqlite;
using Mindweave.Application.Storage;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>
/// Maps SQLite result codes to the storage conditions the application reacts to (requirements §9.3). Errors in the
/// request itself, such as a constraint violation, are not storage conditions and stay <see cref="SqliteException"/>.
/// </summary>
internal static class StorageFailures
{
    public static StorageFailureKind? Classify(SqliteException exception) => exception.SqliteErrorCode switch
    {
        13 => StorageFailureKind.Full, // SQLITE_FULL
        5 or 6 => StorageFailureKind.Busy, // SQLITE_BUSY, SQLITE_LOCKED
        3 or 8 => StorageFailureKind.ReadOnly, // SQLITE_PERM, SQLITE_READONLY
        10 or 14 or 15 => StorageFailureKind.IoError, // SQLITE_IOERR, SQLITE_CANTOPEN, SQLITE_PROTOCOL
        11 or 26 => StorageFailureKind.Corrupt, // SQLITE_CORRUPT, SQLITE_NOTADB
        _ => null,
    };

    /// <summary>The exception a writer's caller sees: a storage condition as <see cref="StorageWriteException"/>, anything else unchanged.</summary>
    public static Exception ForCaller(Exception exception) =>
        exception is SqliteException sqlite && Classify(sqlite) is { } kind
            ? new StorageWriteException(kind, $"{sqlite.Message} (extended code {sqlite.SqliteExtendedErrorCode})", sqlite)
            : exception;
}
