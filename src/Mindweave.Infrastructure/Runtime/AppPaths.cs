using System.Security.Cryptography;
using System.Text;

namespace Mindweave.Infrastructure.Runtime;

public sealed record AppPaths(string Track, string DataDirectory, string DatabasePath,
    string BackupDirectory, string LockPath, string PipeName, string CredentialNamespace)
{
    public static AppPaths ForTrack(string track, string? dataRoot = null)
    {
        if (string.IsNullOrWhiteSpace(track) || track.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Track must contain only ASCII letters, numbers and hyphens.", nameof(track));
        var root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mindweave");
        var directory = Path.GetFullPath(Path.Combine(root, track));
        var identity = OperatingSystem.IsWindows() ? directory.ToUpperInvariant() : directory;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        return new(track, directory, Path.Combine(directory, "mindweave.db"),
            Path.Combine(directory, "backups"), Path.Combine(directory, "instance.lock"),
            $"mindweave-{hash}", $"Mindweave.{track}");
    }
}
