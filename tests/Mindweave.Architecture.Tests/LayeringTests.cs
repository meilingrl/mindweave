using System.Reflection;

namespace Mindweave.Architecture.Tests;

/// <summary>Enforces the four-project dependency direction (architecture §2) by inspecting real references.</summary>
public class LayeringTests
{
    private static readonly Assembly Core = typeof(Mindweave.Core.Workspaces.Workspace).Assembly;
    private static readonly Assembly Application = typeof(Mindweave.Application.Startup.StartupCoordinator).Assembly;
    private static readonly Assembly Infrastructure = typeof(Mindweave.Infrastructure.InfrastructureServices).Assembly;

    [Fact]
    public void Core_depends_on_nothing_but_the_runtime()
    {
        Assert.DoesNotContain(References(Core), n => !IsRuntime(n));
    }

    [Fact]
    public void Application_depends_only_on_core_and_logging_abstractions()
    {
        var allowed = new[] { "Mindweave.Core", "Microsoft.Extensions.Logging.Abstractions" };
        Assert.DoesNotContain(References(Application), n => !IsRuntime(n) && !allowed.Contains(n));
    }

    [Fact]
    public void Infrastructure_does_not_reference_the_ui()
    {
        Assert.DoesNotContain(References(Infrastructure), n => n.StartsWith("Avalonia", StringComparison.Ordinal) || n == "Mindweave");
    }

    [Fact]
    public void Desktop_mentions_infrastructure_only_in_the_composition_files()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Program.cs", "CompositionRoot.cs", "AppHost.cs" };
        var desktop = Path.Combine(RepositoryRoot(), "src", "Mindweave.Desktop");
        var offenders = Directory.EnumerateFiles(desktop, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !allowed.Contains(Path.GetFileName(f)))
            .Where(f => File.ReadAllText(f).Contains("Mindweave.Infrastructure", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Empty(offenders);
    }

    private static string[] References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static bool IsRuntime(string name) =>
        name is "System.Runtime" or "netstandard" or "mscorlib" || name.StartsWith("System.", StringComparison.Ordinal);

    internal static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mindweave.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
