using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mindweave.Architecture.Tests;

/// <summary>
/// The failure-handling rules of docs/design/failure-handling.md, read from the source. A handled failure always
/// leaves a trace; <c>async void</c> stays in view event handlers; programmer-error types are not used as signals.
/// Known exceptions are listed by file and member with their reason, so a new one is a decision, not an accident.
/// </summary>
public class CatchPolicyTests
{
    /// <summary>
    /// Broad catches that neither rethrow nor log nor hand the exception on, each with the reason that is enough.
    /// Key: path under src, then "::" and the member that holds the catch.
    /// </summary>
    private static readonly Dictionary<string, string> QuietOnPurpose = new(StringComparer.Ordinal)
    {
        ["Mindweave.Application/Recommendations/RecommendationService.cs::RecommendationService.ExecuteAfterAsync"] = "Waits for the previous request, whose own run logs its outcome.",
        ["Mindweave.Application/Recommendations/RecommendationService.cs::RecommendationService.ForgetAsync"] = "Waits for the run, which logged its failure; forgetting must finish.",
        ["Mindweave.Desktop/Program.cs::Program.ShowStartupFailure"] = "Even the startup failure window failed; the console line written before is all there is.",
        ["Mindweave.Desktop/Services/UiExceptionGuard.cs::UiExceptionGuard.OnRefreshFailed"] = "The failure is logged before the notice; between two sessions the shell may be gone.",
        ["Mindweave.Desktop/Services/UiExceptionGuard.cs::UiExceptionGuard.OnUnhandledException"] = "The exception is logged before the notice; between two sessions the shell may be gone.",
        ["Mindweave.Infrastructure/Hosting/SingleInstance.cs::SingleInstance.ListenAsync"] = "Started before the application's logger exists; the listener must keep serving.",
        ["Mindweave.Infrastructure/Logging/FileLoggerProvider.cs::FileLoggerProvider..ctor"] = "The logger itself: logging must never stop the application.",
        ["Mindweave.Infrastructure/Logging/FileLoggerProvider.cs::FileLoggerProvider.Write"] = "The logger itself: logging must never stop the application.",
        ["Mindweave.Infrastructure/Logging/FileLoggerProvider.cs::FileLoggerProvider.Prune"] = "The logger itself: logging must never stop the application.",
        ["Mindweave.Infrastructure/Platform/SystemAlertSound.cs::Sound.Play"] = "A missing system sound is not a failure: the visible hint is already shown.",
        ["Mindweave.Infrastructure/Resources/SkiaImageProcessor.cs::SkiaImageProcessor.Probe"] = "Bad bytes fail anywhere in the native codec; the result is \"not an image\", which the caller reports.",
        ["Mindweave.Infrastructure/Resources/SkiaImageProcessor.cs::SkiaImageProcessor.ScaleForSending"] = "Bad bytes fail anywhere in the native codec; the result is \"does not decode\", which the caller reports.",
        ["Mindweave.Infrastructure/Tools/GuardedPageFetcher.cs::GuardedPageFetcher.GoesThroughProxy"] = "An unreadable proxy setting means a direct connection, which gets every address check.",
    };

    /// <summary>
    /// Catches of InvalidOperationException that read it as "shutting down" or "closed" (maintainability review
    /// 2026-11-22, EXC-3). They are to be replaced by a dedicated type; no new one may be added.
    /// </summary>
    private static readonly HashSet<string> InvalidOperationAsSignal = new(StringComparer.Ordinal)
    {
        "Mindweave.Application/Conversations/ConversationTitler.cs::ConversationTitler.ScheduleTitleAsync",
        "Mindweave.Application/Recommendations/RecommendationService.cs::RecommendationService.ExecuteAsync",
        "Mindweave.Application/SummaryLinks/LinkSuggestionService.cs::LinkSuggestionService.ExecuteAsync",
        "Mindweave.Application/Workspaces/WorkspaceNameSuggestions.cs::WorkspaceNameSuggester.SuggestAsync",
        "Mindweave.Infrastructure/Resources/FileResourceBlobStore.cs::FileResourceBlobStore.SweepAsync",
    };

    /// <summary>Names that, called inside a catch, show the failure went somewhere: a log, a fault, a failure path.</summary>
    private static bool LeavesTrace(string name) =>
        name == "Swallowed"
        || name.StartsWith("Log", StringComparison.Ordinal)
        || name.StartsWith("Fail", StringComparison.Ordinal)
        || name.StartsWith("Report", StringComparison.Ordinal)
        || name is "TrySetException" or "SetException";

    [Fact]
    public void Every_broad_catch_rethrows_logs_or_hands_the_exception_on()
    {
        var found = new List<string>();
        foreach (var (file, root) in SourceFiles())
        {
            foreach (var clause in root.DescendantNodes().OfType<CatchClauseSyntax>().Where(IsBroad))
            {
                if (Rethrows(clause) || HandsOn(clause))
                {
                    continue;
                }

                var key = $"{file}::{Member(clause)}";
                if (!QuietOnPurpose.ContainsKey(key))
                {
                    found.Add($"{key} (line {Line(clause)})");
                }
            }
        }

        Assert.True(found.Count == 0, "Broad catches that leave no trace (log with DesktopLog.Swallowed or a LoggerMessage, or list the reason in QuietOnPurpose):\n" + string.Join('\n', found));
    }

    [Fact]
    public void Every_listed_quiet_catch_still_exists()
    {
        var keys = SourceFiles()
            .SelectMany(f => f.Root.DescendantNodes().OfType<CatchClauseSyntax>().Select(c => $"{f.File}::{Member(c)}"))
            .ToHashSet(StringComparer.Ordinal);
        var stale = QuietOnPurpose.Keys.Concat(InvalidOperationAsSignal).Where(k => !keys.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Listed catches that no longer exist (remove them from the list):\n" + string.Join('\n', stale));
    }

    [Fact]
    public void Async_void_is_only_a_view_event_handler()
    {
        const string views = "Mindweave.Desktop/Views/";
        var found = SourceFiles()
            .Where(f => !f.File.StartsWith(views, StringComparison.Ordinal))
            .SelectMany(f => f.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(SyntaxKind.AsyncKeyword) && m.ReturnType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword })
                .Select(m => $"{f.File}::{m.Identifier.Text}"))
            .ToList();
        Assert.True(found.Count == 0, "async void outside Desktop/Views (return a Task and let the caller observe it):\n" + string.Join('\n', found));
    }

    [Fact]
    public void Programmer_error_types_are_not_caught_as_signals()
    {
        var found = new List<string>();
        foreach (var (file, root) in SourceFiles())
        {
            foreach (var clause in root.DescendantNodes().OfType<CatchClauseSyntax>())
            {
                var type = clause.Declaration?.Type.ToString();
                if (type is "NullReferenceException" or "IndexOutOfRangeException")
                {
                    found.Add($"{file}::{Member(clause)} catches {type}");
                }
                else if (type == "InvalidOperationException" && !Rethrows(clause) && !InvalidOperationAsSignal.Contains($"{file}::{Member(clause)}"))
                {
                    found.Add($"{file}::{Member(clause)} catches InvalidOperationException (use a dedicated exception type for an expected state)");
                }
            }
        }

        Assert.True(found.Count == 0, string.Join('\n', found));
    }

    /// <summary>catch, catch (Exception), or catch (Exception) with a filter that does not narrow it to named types.</summary>
    private static bool IsBroad(CatchClauseSyntax clause)
    {
        var type = clause.Declaration?.Type.ToString();
        if (type is not (null or "Exception" or "System.Exception"))
        {
            return false;
        }

        // `when (ex is IOException or UnauthorizedAccessException)` names what it expects; `is not` does not.
        var filter = clause.Filter?.FilterExpression;
        return filter is null || !NarrowsToTypes(filter);
    }

    private static bool NarrowsToTypes(ExpressionSyntax filter) => filter switch
    {
        ParenthesizedExpressionSyntax p => NarrowsToTypes(p.Expression),
        IsPatternExpressionSyntax { Pattern: var pattern } => IsPositiveTypePattern(pattern),
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalOrExpression } or_ => NarrowsToTypes(or_.Left) && NarrowsToTypes(or_.Right),
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression } and_ => NarrowsToTypes(and_.Left) || NarrowsToTypes(and_.Right),
        _ => false,
    };

    private static bool IsPositiveTypePattern(PatternSyntax pattern) => pattern switch
    {
        TypePatternSyntax or DeclarationPatternSyntax or RecursivePatternSyntax { Type: not null } => true,
        ConstantPatternSyntax { Expression: IdentifierNameSyntax or QualifiedNameSyntax or MemberAccessExpressionSyntax } => true,
        BinaryPatternSyntax { RawKind: (int)SyntaxKind.OrPattern } or_ => IsPositiveTypePattern(or_.Left) && IsPositiveTypePattern(or_.Right),
        BinaryPatternSyntax { RawKind: (int)SyntaxKind.AndPattern } and_ => IsPositiveTypePattern(and_.Left) || IsPositiveTypePattern(and_.Right),
        ParenthesizedPatternSyntax p => IsPositiveTypePattern(p.Pattern),
        _ => false,
    };

    private static bool Rethrows(CatchClauseSyntax clause) =>
        clause.Block.DescendantNodes().Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax);

    /// <summary>The block logs, faults something, or passes the exception itself on (an argument or an assignment).</summary>
    private static bool HandsOn(CatchClauseSyntax clause)
    {
        var name = clause.Declaration?.Identifier.Text;
        foreach (var node in clause.Block.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax call when LeavesTrace(InvokedName(call)):
                    return true;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Invoke" } or ConditionalAccessExpressionSyntax } call
                    when call.ToString().Contains("Failed", StringComparison.Ordinal):
                    return true;
                case ArgumentSyntax { Expression: IdentifierNameSyntax id } when name is { Length: > 0 } && id.Identifier.Text == name:
                    return true;
                case AssignmentExpressionSyntax { Right: IdentifierNameSyntax id } when name is { Length: > 0 } && id.Identifier.Text == name:
                    return true;
            }
        }

        return false;
    }

    private static string InvokedName(InvocationExpressionSyntax call) => call.Expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        MemberBindingExpressionSyntax b => b.Name.Identifier.Text,
        _ => string.Empty,
    };

    /// <summary>The method, accessor, constructor or local function that holds the node, named the way the lists use.</summary>
    private static string Member(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case LocalFunctionStatementSyntax local:
                    return $"{Member(local)}.{local.Identifier.Text}";
                case MethodDeclarationSyntax method:
                    return $"{Type(method)}.{method.Identifier.Text}";
                case ConstructorDeclarationSyntax ctor:
                    return $"{Type(ctor)}..ctor";
                case PropertyDeclarationSyntax property:
                    return $"{Type(property)}.{property.Identifier.Text}";
                case AccessorDeclarationSyntax accessor when accessor.Parent?.Parent is EventDeclarationSyntax e:
                    return $"{Type(e)}.{e.Identifier.Text}";
            }
        }

        return "?";
    }

    private static string Type(SyntaxNode member) =>
        member.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "?";

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>Every C# file under src, keyed by its path below src with forward slashes.</summary>
    private static IEnumerable<(string File, SyntaxNode Root)> SourceFiles()
    {
        var src = Path.Combine(LayeringTests.RepositoryRoot(), "src");
        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(src, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (relative, CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot());
        }
    }
}
