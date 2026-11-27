using System.Reflection;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Infrastructure.Hosting;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// The composition as the application builds it (health check architecture-4, D-042). The container fills an optional
/// constructor parameter with its default when nothing is registered for it, and build-time validation says nothing:
/// a forgotten registration would then change behaviour silently. So every type the container builds takes only
/// required parameters, and every registered service resolves.
/// </summary>
public class CompositionTests
{
    [AvaloniaFact]
    public async Task Types_the_container_builds_take_only_required_parameters()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mindweave-desktop-tests", Guid.NewGuid().ToString("N"));
        try
        {
            // Built, not resolved: the descriptors are the application's own, with nothing replaced by a test.
            await using var root = CompositionRoot.BuildRoot(new AppPaths("test", directory), new DesktopHarness.RecordingShell(), NullLoggerProvider.Instance);
            var descriptors = root.GetRequiredService<CompositionRoot.RootRegistrations>().Descriptors.Concat(ViewRegistrations());

            var problems = descriptors
                .Select(d => d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType)
                .OfType<Type>()
                .Where(t => t.Assembly.GetName().Name!.StartsWith("Mindweave", StringComparison.Ordinal))
                .Distinct()
                .SelectMany(Problems)
                .Order(StringComparer.Ordinal)
                .ToList();

            Assert.True(problems.Count == 0, "Container-built types with optional or ambiguous constructors:\n" + string.Join('\n', problems));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public async Task Every_registered_service_resolves()
    {
        await using var app = DesktopHarness.Create();
        var root = app.Session.Root;
        var view = app.Session.View;

        Resolve(root, root.GetRequiredService<CompositionRoot.RootRegistrations>().Descriptors);
        Resolve(view, ViewRegistrations());
    }

    // TEMP until CompositionRoot exposes it.
    private static ServiceCollection ViewRegistrations()
    {
        var s = new ServiceCollection();
        typeof(CompositionRoot).GetMethod("AddView", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [s]);
        return s;
    }

    private static void Resolve(IServiceProvider provider, IEnumerable<ServiceDescriptor> descriptors)
    {
        foreach (var (type, key) in descriptors.Select(d => (d.ServiceType, d.ServiceKey)).Distinct())
        {
            if (type.IsGenericTypeDefinition)
            {
                continue;
            }

            var instances = key is null ? provider.GetServices(type) : provider.GetKeyedServices(type, key);
            Assert.All(instances, i => Assert.NotNull(i));
            Assert.NotEmpty(instances);
        }
    }

    /// <summary>
    /// One public constructor, so the container never falls back to a shorter one when a registration is missing, and no
    /// optional parameter, which it would fill with the default.
    /// </summary>
    private static IEnumerable<string> Problems(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (constructors.Length != 1)
        {
            yield return $"{type.FullName}: {constructors.Length} public constructors";
            yield break;
        }

        foreach (var parameter in constructors[0].GetParameters().Where(p => p.IsOptional || p.HasDefaultValue))
        {
            yield return $"{type.FullName}: optional {parameter.ParameterType.Name} {parameter.Name}";
        }
    }
}
