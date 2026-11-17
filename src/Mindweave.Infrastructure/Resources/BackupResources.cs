namespace Mindweave.Infrastructure.Resources;

/// <summary>Where a backup keeps the attachment originals its delete removed: a folder beside it, named after it.</summary>
internal static class BackupResources
{
    public const string Suffix = ".resources";

    public static string DirectoryOf(string backupPath) => backupPath + Suffix;

    /// <summary>Removes a backup's originals folder, if any. Throws like <see cref="Directory.Delete(string, bool)"/>.</summary>
    public static void DeleteFor(string backupPath)
    {
        var folder = DirectoryOf(backupPath);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Bytes the backup's originals take, 0 without any.</summary>
    public static long SizeFor(string backupPath)
    {
        var folder = DirectoryOf(backupPath);
        try
        {
            return Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles().Sum(f => f.Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
