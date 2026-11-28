using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mindweave.Architecture.Tests;

/// <summary>
/// Text the services show or send is written with both languages side by side (docs/design/bilingual-ui.md §4.2, §5).
/// Read from the syntax, so comments never count and a literal is judged by where it sits, not by its line.
/// </summary>
public class ChineseTextTests
{
    /// <summary>
    /// Members that must keep Chinese outside T: they read Chinese the model or a website wrote, or markers stored in the user's data,
    /// so the Chinese there is input, not text shown or sent. Each is (file under src, member, reason).
    /// </summary>
    private static readonly (string File, string Member, string Reason)[] Allowed =
    [
        ("Mindweave.Core/Conversations/ConversationRules.cs", "TitlePrefixes", "accepts the title label a model writes in Chinese"),
        ("Mindweave.Core/Summaries/SummaryRules.cs", "ParseGenerated", "accepts the title and short-name labels a model writes in Chinese"),
        ("Mindweave.Core/Summaries/SummaryRules.cs", "DisplayName", "the mark joining a stored name's two parts, which are in the conversation's language"),
        ("Mindweave.Core/Text/EnclosingMarks.cs", "Pairs", "strips the Chinese quotes and title marks a model wraps around a name"),
        ("Mindweave.Application/Recommendations/RecommendationPrompt.cs", "Canonical", "accepts the Chinese field names a model writes"),
        ("Mindweave.Application/Recommendations/RecommendationPrompt.cs", "JsonKey", "accepts the Chinese field names a model writes"),
        ("Mindweave.Application/Recommendations/RecommendationPrompt.cs", "BasisLabel", "accepts the Chinese basis label a model writes"),
        ("Mindweave.Application/Recommendations/RecommendationPrompt.cs", "ReasonLead", "accepts the Chinese reason label a model writes"),
        ("Mindweave.Application/Recommendations/RecommendationPrompt.cs", "ListMarker", "accepts Chinese list numbering in a model's answer"),
        ("Mindweave.Application/Recommendations/RecommendationPrompt.cs", "Preamble", "skips the Chinese lead-in a model writes before its list"),
        ("Mindweave.Application/Summaries/SummaryNaming.cs", "NamingLine", "accepts Chinese punctuation in a model's numbered lines"),
        ("Mindweave.Application/SummaryLinks/LinkSuggestionPrompt.cs", "Canonical", "accepts the Chinese field names a model writes"),
        ("Mindweave.Application/SummaryLinks/LinkSuggestionPrompt.cs", "JsonKey", "accepts the Chinese field names a model writes"),
        ("Mindweave.Application/SummaryLinks/LinkSuggestionPrompt.cs", "LinePair", "accepts Chinese separators between the two summaries a model names"),
        ("Mindweave.Application/SummaryLinks/LinkSuggestionPrompt.cs", "ReasonLead", "accepts the Chinese reason label a model writes"),
        ("Mindweave.Application/Workspaces/WorkspaceNameSuggestions.cs", "ListMarker", "accepts Chinese list numbering in a model's answer"),
        ("Mindweave.Application/Workspaces/WorkspaceNameSuggestions.cs", "Preamble", "skips the Chinese lead-in a model writes before its list"),
        ("Mindweave.Application/Tools/WebRun.cs", "PointsBack", "recognizes a Chinese question that refers back to earlier material"),
        ("Mindweave.Application/Summaries/MatchingSummaries.cs", "Common", "recognizes the Chinese question words that name no topic, in either interface language"),
        ("Mindweave.Application/Summaries/MatchingSummaries.cs", "Particles", "recognizes Chinese particles and pronouns in a question, in either interface language"),
        ("Mindweave.Application/Tools/WebRun.cs", "Address", "ends an address at the Chinese punctuation that follows it in a question"),
        ("Mindweave.Application/Tools/ITool.cs", "ChinesePlaceholder", "an image placeholder is replaced in either language: the language may switch while a tool runs"),
        ("Mindweave.Infrastructure/Tools/GuardedPageFetcher.cs", "RobotCheckTitles", "recognizes the Chinese titles websites give a check for robots"),
        ("Mindweave.Infrastructure/Sqlite/Migrations/SchemaMigrations.cs", "All", "SQL comments in stored schema text, which never changes"),
    ];

    [Fact]
    public void Services_have_no_Chinese_outside_T()
    {
        var src = Path.Combine(LayeringTests.RepositoryRoot(), "src");
        var allowed = Allowed.Select(a => (a.File.Replace('/', Path.DirectorySeparatorChar), a.Member)).ToHashSet();
        var used = new HashSet<(string, string)>();
        var problems = new List<string>();
        var trees = new[] { "Mindweave.Core", "Mindweave.Application", "Mindweave.Infrastructure" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(src, project), "*.cs", SearchOption.AllDirectories))
            .Where(UiTextSourceTests.IsSource)
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                path => path,
                path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Preview), path, cancellationToken: TestContext.Current.CancellationToken)
                    .GetRoot(TestContext.Current.CancellationToken));

        // Helpers that take both languages side by side, (string english, string chinese, …), count as T.
        var helpers = trees.Values.SelectMany(r => r.DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(m => m.ParameterList.Parameters.Zip(m.ParameterList.Parameters.Skip(1)).Any(p => p.First.Identifier.Text == "english" && p.Second.Identifier.Text == "chinese"))
            .Select(m => m.Identifier.Text)
            .Concat(["T", "Plural", "BilingualText"])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in trees.Keys)
        {
            var file = Path.GetRelativePath(src, path);
            var root = trees[path];
            foreach (var token in root.DescendantTokens().Where(UiTextSourceTests.IsText).Where(t => UiTextSourceTests.Chinese().IsMatch(t.ValueText)))
            {
                if (IsBilingual(token, helpers) || InModelPhrases(token))
                {
                    continue;
                }

                var member = MemberOf(token);
                if (allowed.Contains((file, member)))
                {
                    used.Add((file, member));
                    continue;
                }

                problems.Add($"{file}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1} ({member}) {Shorten(token.ValueText)}");
            }
        }

        problems.AddRange(allowed.Except(used).Select(a => $"allowlist entry no longer needed: {a.Item1} {a.Item2}"));
        Assert.True(problems.Count == 0, $"{problems.Count} places:\n" + string.Join('\n', problems));
    }

    /// <summary>
    /// Inside T(...), Plural(...), another helper that takes both languages, new BilingualText(...) or a target-typed
    /// new(...) that becomes a BilingualText, at any depth.
    /// </summary>
    private static bool IsBilingual(SyntaxToken token, HashSet<string> helpers) =>
        token.Parent!.Ancestors().Any(n => n switch
        {
            InvocationExpressionSyntax call => NameOf(call.Expression) is { } name && helpers.Contains(name),
            ObjectCreationExpressionSyntax creation => IsBilingualType(creation.Type),
            ImplicitObjectCreationExpressionSyntax creation => IsBilingualType(TargetType(creation)),
            _ => false,
        });

    private static bool IsBilingualType(TypeSyntax? type) =>
        type?.ToString().TrimEnd('?').EndsWith("BilingualText", StringComparison.Ordinal) == true;

    /// <summary>
    /// The declared type a target-typed new(...) takes, read from the syntax: through switch arms, conditionals and ??, to a
    /// declaration, an expression body, a return or an assignment to a declared local, field or property. Null when unknown.
    /// </summary>
    private static TypeSyntax? TargetType(ExpressionSyntax creation)
    {
        SyntaxNode node = creation;
        while (node.Parent is SwitchExpressionArmSyntax or SwitchExpressionSyntax or ParenthesizedExpressionSyntax
               || node.Parent is ConditionalExpressionSyntax conditional && conditional.Condition != node
               || node.Parent is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.CoalesceExpression))
        {
            node = node.Parent;
        }

        return node.Parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } } => declaration.Type,
            EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property } => property.Type,
            ArrowExpressionClauseSyntax or ReturnStatementSyntax => ReturnType(node.Parent),
            AssignmentExpressionSyntax { Left: IdentifierNameSyntax name } assignment when assignment.Right == node => DeclaredType(assignment, name.Identifier.Text),
            _ => null,
        };
    }

    private static TypeSyntax? ReturnType(SyntaxNode node) => node.Ancestors().Select(a => a switch
    {
        MethodDeclarationSyntax method => method.ReturnType,
        LocalFunctionStatementSyntax function => function.ReturnType,
        PropertyDeclarationSyntax property => property.Type,
        _ => null,
    }).FirstOrDefault(t => t is not null);

    private static TypeSyntax? DeclaredType(SyntaxNode from, string name) =>
        from.Ancestors().OfType<MemberDeclarationSyntax>().First().DescendantNodes().OfType<VariableDeclarationSyntax>()
            .FirstOrDefault(d => d.Variables.Any(v => v.Identifier.Text == name))?.Type
        ?? from.Ancestors().OfType<TypeDeclarationSyntax>().First().Members.Select(m => m switch
        {
            FieldDeclarationSyntax field when field.Declaration.Variables.Any(v => v.Identifier.Text == name) => field.Declaration.Type,
            PropertyDeclarationSyntax property when property.Identifier.Text == name => property.Type,
            _ => null,
        }).FirstOrDefault(t => t is not null);

    private static string? NameOf(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => name.Identifier.Text,
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        _ => null,
    };

    private static bool InModelPhrases(SyntaxToken token) =>
        token.Parent!.Ancestors().OfType<ClassDeclarationSyntax>().Any(c => c.Identifier.Text == "ModelPhrases");

    /// <summary>The name of the member a literal sits in: a method, property, field, constructor or the like.</summary>
    private static string MemberOf(SyntaxToken token)
    {
        foreach (var node in token.Parent!.Ancestors())
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    return method.Identifier.Text;
                case PropertyDeclarationSyntax property:
                    return property.Identifier.Text;
                case FieldDeclarationSyntax field:
                    return field.Declaration.Variables[0].Identifier.Text;
                case ConstructorDeclarationSyntax constructor:
                    return constructor.Identifier.Text;
                case LocalFunctionStatementSyntax:
                    continue;
                case BaseTypeDeclarationSyntax type:
                    return type.Identifier.Text;
            }
        }

        return "(file)";
    }

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length > 80 ? line[..80] + "…" : line;
    }
}
