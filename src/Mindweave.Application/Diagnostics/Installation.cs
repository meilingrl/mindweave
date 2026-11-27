using System.Globalization;
using System.Runtime.InteropServices;
using Mindweave.Application.Languages;

namespace Mindweave.Application.Diagnostics;

/// <summary>
/// This build and the system it runs on, in one line of plain English (D2): the log's start line, the end of copied
/// failure details and the diagnostic summary use the same words. Holds no path, account name or content.
/// </summary>
public static class Installation
{
    /// <summary>"Mindweave 0.6.29 (3e90875)".</summary>
    public static string App(AppEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.Commit is { } commit ? $"Mindweave {environment.Version} ({commit})" : $"Mindweave {environment.Version}";
    }

    /// <summary>"macOS 15.4.1 Arm64".</summary>
    public static string System() =>
        string.Create(CultureInfo.InvariantCulture, $"{SystemName()} {Environment.OSVersion.Version} {RuntimeInformation.OSArchitecture}");

    /// <summary>
    /// "Mindweave 0.6.29 (3e90875), channel dev, macOS 15.4.1 Arm64, .NET 10.0.0, schema 20, interface English": the log's
    /// start line (low finding observability-5).
    /// </summary>
    public static string Describe(AppEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{App(environment)}, channel {environment.Channel}, {System()}, {RuntimeInformation.FrameworkDescription}, schema {environment.SchemaVersion}, interface {CurrentLanguage.Value}");
    }

    private static string SystemName() =>
        OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsLinux() ? "Linux"
        : RuntimeInformation.OSDescription;
}
