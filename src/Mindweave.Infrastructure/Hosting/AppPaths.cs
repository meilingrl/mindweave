using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Mindweave.Infrastructure.Hosting;

/// <summary>
/// Per-channel data locations. The channel (build property <c>MindweaveChannel</c>) keeps tracks
/// and builds that must not share data apart; <c>MINDWEAVE_DATA_DIR</c> overrides the whole directory.
/// </summary>
public sealed record AppPaths(string Channel, string DataDirectory)
{
    public const string DataDirectoryVariable = "MINDWEAVE_DATA_DIR";

    public string DatabasePath => Path.Combine(DataDirectory, "mindweave.db");

    public string LockPath => Path.Combine(DataDirectory, "instance.lock");

    public string BackupDirectory => Path.Combine(DataDirectory, "backups");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Resource originals and their temporary files (docs/design/resources.md).</summary>
    public string ResourceDirectory => Path.Combine(DataDirectory, "resources");

    /// <summary>Short stable identity of this data directory, used for the activation pipe name.</summary>
    public string InstanceKey
    {
        get
        {
            var full = Path.GetFullPath(DataDirectory);
            if (!OperatingSystem.IsLinux())
            {
                // macOS and Windows file systems are case-insensitive by default.
                full = full.ToUpperInvariant();
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
            return Convert.ToHexStringLower(hash.AsSpan(0, 8));
        }
    }

    public static AppPaths Resolve(string channel, Func<string, string?>? getEnvironment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        getEnvironment ??= Environment.GetEnvironmentVariable;

        var overridden = getEnvironment(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AppPaths(channel, Path.GetFullPath(overridden));
        }

        return new AppPaths(channel, Path.Combine(PlatformDataRoot(getEnvironment), "Mindweave", channel));
    }

    /// <summary>Reads the channel stamped into the entry assembly at build time.</summary>
    public static string ChannelOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MindweaveChannel")?.Value
            ?? throw new InvalidOperationException("Assembly has no MindweaveChannel metadata.");
    }

    private static string PlatformDataRoot(Func<string, string?> getEnvironment)
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        }

        if (OperatingSystem.IsWindows())
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        var xdg = getEnvironment("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
    }
}
