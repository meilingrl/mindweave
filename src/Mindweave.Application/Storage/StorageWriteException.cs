namespace Mindweave.Application.Storage;

/// <summary>Why the local database refused a write; the write was rolled back as a whole.</summary>
public enum StorageFailureKind
{
    /// <summary>The disk (or the database's size limit) is full.</summary>
    Full,

    /// <summary>Another process held the write lock longer than the busy timeout.</summary>
    Busy,

    /// <summary>The file or its folder cannot be written (permissions, read-only media).</summary>
    ReadOnly,

    /// <summary>The operating system reported an I/O error.</summary>
    IoError,

    /// <summary>The file is damaged or no longer a database.</summary>
    Corrupt,
}

/// <summary>
/// A core write failed for a reason in the environment rather than in the request (requirements §9.3): callers keep
/// the input and pause work that depends on storage until a write succeeds again.
/// </summary>
public sealed class StorageWriteException : Exception
{
    public StorageWriteException()
        : this(StorageFailureKind.IoError, "The local database refused the write.", null)
    {
    }

    public StorageWriteException(string message)
        : this(StorageFailureKind.IoError, message, null)
    {
    }

    public StorageWriteException(string message, Exception innerException)
        : this(StorageFailureKind.IoError, message, innerException)
    {
    }

    public StorageWriteException(StorageFailureKind kind, string message, Exception? innerException)
        : base(message, innerException) => Kind = kind;

    public StorageFailureKind Kind { get; }
}
