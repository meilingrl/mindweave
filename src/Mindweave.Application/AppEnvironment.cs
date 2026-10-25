namespace Mindweave.Application;

/// <summary>Facts about this installation that the UI may show; assembled once at startup.</summary>
public sealed record AppEnvironment(
    string Channel,
    string Version,
    string DataDirectory,
    string DatabasePath,
    string BackupDirectory,
    string LogDirectory,
    int SchemaVersion);
