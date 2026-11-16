namespace Mindweave.Application;

/// <summary>Facts about this installation that the UI may show; assembled once at startup.</summary>
public sealed record AppEnvironment(
    string Channel,
    string Version,
    string DataDirectory,
    string DatabasePath,
    string BackupDirectory,
    string LogDirectory,
    int SchemaVersion)
{
    /// <summary>
    /// The channel of published builds (Release configuration with no explicit channel; see Directory.Build.props).
    /// It is not named in the interface: a release shows only the product name and version.
    /// </summary>
    public const string ReleaseChannel = "release";

    public bool ShowsChannel => !string.Equals(Channel, ReleaseChannel, StringComparison.Ordinal);
}
