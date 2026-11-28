using System.Reflection;
using System.Runtime.CompilerServices;

namespace Mindweave.Architecture.Tests;

/// <summary>
/// architecture-8 (D-042; architecture §2): abstractions exist only at real boundaries. The Desktop reaches each
/// Application service through one interface, which is where tests put a stand-in; a class used only inside its own
/// project gets no interface of its own. So an interface with one implementation must be used from a project other than
/// the one that implements it. "Used" means it appears in a signature there (a constructor, field, property, method or
/// event); registering it in the composition root does not count. Test projects do not count either.
/// </summary>
public class InterfaceBoundaryTests
{
    /// <summary>
    /// Interfaces with one implementation used only inside its project, each with the boundary it stands for. A new
    /// entry is a decision, not an accident.
    /// </summary>
    private static readonly Dictionary<string, string> InsideOnPurpose = new(StringComparer.Ordinal)
    {
        // The platform: tests put a stand-in here (the headless window has no system shell, theme or UI thread of its own).
        ["Mindweave.Desktop.Services.ISystemShell"] = "The operating system's folders, browser and quit; tests record calls instead.",
        ["Mindweave.Desktop.Services.IThemeApplier"] = "Avalonia's theme; tests read what was applied.",
        ["Mindweave.Desktop.Services.IUiDispatcher"] = "The UI thread; tests run work at once.",
        ["Mindweave.Desktop.Navigation.IPageFactory"] = "Making pages; a test puts in a factory that fails.",

        // Navigation, named as a boundary in architecture §2: narrow views of the workbench and the root.
        ["Mindweave.Desktop.Navigation.IContentNavigator"] = "Navigation (architecture §2): what a page may ask of the workbench.",
        ["Mindweave.Desktop.Navigation.IMenuNavigator"] = "Navigation (architecture §2): what the menus may ask of the workbench.",
        ["Mindweave.Desktop.Navigation.IRootNavigator"] = "Navigation (architecture §2): which root page the window shows.",
        ["Mindweave.Desktop.Navigation.IWorkbenchNavigator"] = "Navigation (architecture §2): moving between pages and areas.",
        ["Mindweave.Desktop.Navigation.IWorkspaceDirectory"] = "Navigation (architecture §2): the workspaces the workbench knows.",

        // Narrow views of one large class, not forwarding interfaces: each consumer sees only its part.
        ["Mindweave.Application.Conversations.IGenerationEvents"] = "The events of the generation coordinator, without its controls.",
        ["Mindweave.Application.Conversations.IRunSlots"] = "The run slots of the generation coordinator, for the run loop only.",

        // An extension point that takes a list.
        ["Mindweave.Application.Tools.IToolSource"] = "The tool registry takes any number of sources (architecture §15.6); MCP is the first.",
    };

    private static readonly Assembly[] Production =
    [
        typeof(Core.Workspaces.Workspace).Assembly,
        typeof(Application.Startup.StartupCoordinator).Assembly,
        typeof(Infrastructure.Sqlite.SqliteDatabase).Assembly,
        typeof(Desktop.ViewModels.RootViewModel).Assembly,
    ];

    [Fact]
    public void An_interface_with_one_implementation_is_used_from_another_project()
    {
        var types = Production.SelectMany(a => a.GetTypes()).Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute), false)).ToList();
        var usedFrom = new Dictionary<Type, HashSet<Assembly>>();
        foreach (var type in types)
        {
            foreach (var used in SignatureTypes(type))
            {
                if (used.IsInterface && Production.Contains(used.Assembly))
                {
                    if (!usedFrom.TryGetValue(used, out var assemblies))
                    {
                        usedFrom[used] = assemblies = [];
                    }

                    assemblies.Add(type.Assembly);
                }
            }
        }

        var offenders = new List<string>();
        foreach (var contract in types.Where(t => t.IsInterface && t.Namespace?.StartsWith("Mindweave.", StringComparison.Ordinal) == true))
        {
            var implementations = types.Where(t => t.IsClass && !t.IsAbstract && Implements(t, contract)).ToList();
            if (implementations.Count != 1)
            {
                continue;
            }

            var home = implementations[0].Assembly;
            var outside = usedFrom.TryGetValue(contract, out var assemblies) && assemblies.Any(a => a != home);
            if (!outside && !InsideOnPurpose.ContainsKey(contract.FullName!))
            {
                offenders.Add($"{contract.FullName} (implemented by {implementations[0].Name} in {home.GetName().Name})");
            }
        }

        Assert.True(offenders.Count == 0, "Interfaces used only inside the project of their one implementation:\n" + string.Join('\n', offenders.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Every_interface_kept_on_purpose_still_exists()
    {
        var names = Production.SelectMany(a => a.GetTypes()).Where(t => t.IsInterface).Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(InsideOnPurpose.Keys, k => !names.Contains(k));
    }

    private static bool Implements(Type type, Type contract) =>
        contract.IsGenericTypeDefinition
            ? type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == contract)
            : contract.IsAssignableFrom(type) && type != contract;

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>The types a type names in its own signatures, generic arguments included; not the interfaces it implements.</summary>
    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        var direct = new List<Type>();
        if (type.BaseType is { } baseType)
        {
            direct.Add(baseType);
        }

        direct.AddRange(type.GetFields(Declared).Select(f => f.FieldType));
        direct.AddRange(type.GetProperties(Declared).Select(p => p.PropertyType));
        direct.AddRange(type.GetEvents(Declared).Select(e => e.EventHandlerType!));
        foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            direct.AddRange(method.GetParameters().Select(p => p.ParameterType));
            if (method is MethodInfo info)
            {
                direct.Add(info.ReturnType);
            }
        }

        return direct.SelectMany(Flatten);
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var inner in Flatten(type.GetElementType()!))
            {
                yield return inner;
            }

            yield break;
        }

        if (type.IsGenericType)
        {
            yield return type.GetGenericTypeDefinition();
            foreach (var inner in type.GetGenericArguments().SelectMany(Flatten))
            {
                yield return inner;
            }

            yield break;
        }

        yield return type;
    }
}
