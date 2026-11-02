using System.Reflection;

namespace Mindweave.Architecture.Tests;

/// <summary>
/// View models reach application services through interfaces (course requirement C3; architecture review A6). Bounded
/// on purpose: records, options, child view models, Desktop UI ports and framework-neutral values such as
/// <see cref="TimeProvider"/> stay allowed.
/// </summary>
public class ViewModelBoundaryTests
{
    private static readonly Assembly Desktop = typeof(Mindweave.Desktop.ViewModels.ViewModelBase).Assembly;
    private static readonly Assembly Application = typeof(Mindweave.Application.Startup.StartupCoordinator).Assembly;
    private static readonly Assembly Infrastructure = typeof(Mindweave.Infrastructure.InfrastructureServices).Assembly;

    private static readonly string[] Presentation =
    [
        "Mindweave.Desktop.ViewModels",
        "Mindweave.Desktop.Navigation",
        "Mindweave.Desktop.Network",
    ];

    [Fact]
    public void View_models_take_no_concrete_application_or_infrastructure_service()
    {
        var offenders = PresentationTypes()
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Where(p => IsConcreteService(p.ParameterType))
                .Select(p => $"{t.Name}({p.ParameterType.Name} {p.Name})"))
            .ToArray();
        Assert.Empty(offenders);
    }

    /// <summary>
    /// View models stay free of the UI framework (architecture review L5): no public property, constructor or method of
    /// theirs takes or returns an Avalonia type. Margins, brushes and the like come from converters in the view layer.
    /// </summary>
    [Fact]
    public void View_models_expose_no_Avalonia_types()
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        foreach (var type in PresentationTypes().Where(t => t.IsPublic || t.IsNestedPublic))
        {
            offenders.AddRange(type.GetProperties(Public).Where(p => IsAvalonia(p.PropertyType)).Select(p => $"{type.Name}.{p.Name}"));
            offenders.AddRange(type.GetConstructors(Public)
                .SelectMany(c => c.GetParameters()).Where(p => IsAvalonia(p.ParameterType)).Select(p => $"{type.Name}({p.Name})"));
            offenders.AddRange(type.GetMethods(Public).Where(m => !m.IsSpecialName)
                .Where(m => IsAvalonia(m.ReturnType) || m.GetParameters().Any(p => IsAvalonia(p.ParameterType)))
                .Select(m => $"{type.Name}.{m.Name}()"));
        }

        Assert.Empty(offenders);
    }

    /// <summary>The type, its element type or any of its generic arguments comes from an Avalonia assembly.</summary>
    private static bool IsAvalonia(Type type)
    {
        if (type.HasElementType)
        {
            return IsAvalonia(type.GetElementType()!);
        }

        return (type.Assembly.GetName().Name ?? string.Empty).StartsWith("Avalonia", StringComparison.Ordinal)
            || (type.IsGenericType && type.GetGenericArguments().Any(IsAvalonia));
    }

    private static IEnumerable<Type> PresentationTypes() =>
        Desktop.GetTypes().Where(t => t.Namespace is { } ns && Presentation.Any(p => ns.StartsWith(p, StringComparison.Ordinal)));

    /// <summary>
    /// Observable state a view model watches rather than a service it calls: an answer's live text while it streams,
    /// handed out by the conversation use case.
    /// </summary>
    private static readonly Type[] ObservedState = [typeof(Mindweave.Application.Conversations.LiveAnswer)];

    /// <summary>A class from Application or Infrastructure that is neither a record nor abstract: a service, not a value.</summary>
    private static bool IsConcreteService(Type type) =>
        (type.Assembly == Application || type.Assembly == Infrastructure)
        && !ObservedState.Contains(type)
        && type is { IsClass: true, IsAbstract: false }
        && type.GetMethod("<Clone>$") is null
        && !typeof(Delegate).IsAssignableFrom(type);
}
