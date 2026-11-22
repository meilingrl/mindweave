using System.Reflection;

namespace Mindweave.Desktop;

/// <summary>
/// Which build this is: the version, the commit it was built from and that commit's date (Mindweave.Desktop.csproj).
/// The commit is the part of the informational version after '+', shortened to seven characters.
/// </summary>
internal sealed record BuildStamp(string Version, string? Commit, string? CommitDate)
{
    public static BuildStamp Read(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var date = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MindweaveCommitDate")?.Value;
        return Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, date);
    }

    public static BuildStamp Parse(string? informationalVersion, string? commitDate)
    {
        var parts = (informationalVersion ?? "").Split('+', 2);
        var version = parts[0].Length > 0 ? parts[0] : "0.0.0";
        var commit = parts.Length > 1 && parts[1].Length > 0 ? parts[1][..Math.Min(7, parts[1].Length)] : null;
        return new BuildStamp(version, commit, string.IsNullOrWhiteSpace(commitDate) ? null : commitDate.Trim());
    }
}
