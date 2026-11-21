using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mindweave.Architecture.Tests;

/// <summary>
/// Every piece of interface text is written once with both languages side by side, <c>T("English", "中文")</c>
/// (docs/design/bilingual-ui.md §4.2, §8). Read from the source, so every member is covered whatever its parameters.
/// </summary>
public partial class UiTextSourceTests
{
    /// <summary>Text that is the same in both languages on purpose: a language's own name.</summary>
    private static readonly HashSet<string> SameInBoth = new(StringComparer.Ordinal) { "简体中文" };

    [Fact]
    public void English_arguments_have_no_Chinese()
    {
        var problems = new List<string>();
        foreach (var (file, root) in UiTextFiles())
        {
            foreach (var call in TCalls(root))
            {
                var args = call.ArgumentList.Arguments;
                if (args.Count != 2)
                {
                    problems.Add($"{file}:{Line(call)} T(...) takes two arguments, English then Chinese");
                    continue;
                }

                foreach (var text in Texts(args[0].Expression).Where(t => Chinese().IsMatch(t) && !SameInBoth.Contains(t)))
                {
                    problems.Add($"{file}:{Line(call)} English argument has Chinese: {text}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void Chinese_appears_only_in_the_second_argument_of_T()
    {
        var problems = new List<string>();
        foreach (var (file, root) in UiTextFiles())
        {
            foreach (var token in root.DescendantTokens().Where(IsText))
            {
                var text = token.ValueText;
                if (!Chinese().IsMatch(text) || SameInBoth.Contains(text))
                {
                    continue;
                }

                if (!IsInSecondArgumentOfT(token))
                {
                    problems.Add($"{file}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1} Chinese outside T(english, chinese): {text}");
                }
            }
        }

        Assert.True(problems.Count == 0, $"{problems.Count} places:\n" + string.Join('\n', problems.Take(60)));
    }

    [Fact]
    public void No_text_is_fixed_when_first_read()
    {
        var problems = new List<string>();
        foreach (var (file, root) in UiTextFiles())
        {
            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                         .Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword) || f.Modifiers.Any(SyntaxKind.StaticKeyword))
                         .Where(f => f.Declaration.Type.ToString() is "string" or "string?"))
            {
                problems.Add($"{file}:{Line(field)} {field.Declaration.Variables[0].Identifier.Text} is a fixed string field");
            }

            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                         .Where(p => p.Initializer is not null && p.Type.ToString() is "string" or "string?"))
            {
                problems.Add($"{file}:{Line(property)} {property.Identifier.Text} keeps the text it first read");
            }
        }

        Assert.True(problems.Count == 0, $"{problems.Count} members:\n" + string.Join('\n', problems.Take(60)));
    }

    private static IEnumerable<(string File, SyntaxNode Root)> UiTextFiles()
    {
        var folder = Path.Combine(LayeringTests.RepositoryRoot(), "src", "Mindweave.Desktop", "Text");
        foreach (var path in Directory.EnumerateFiles(folder, "UiText*.cs").Order(StringComparer.Ordinal))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Preview), path);
            yield return (Path.GetFileName(path), tree.GetRoot());
        }
    }

    private static IEnumerable<InvocationExpressionSyntax> TCalls(SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(IsT);

    private static bool IsT(InvocationExpressionSyntax call) => call.Expression switch
    {
        IdentifierNameSyntax name => name.Identifier.Text == "T",
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text == "T",
        _ => false,
    };

    private static bool IsInSecondArgumentOfT(SyntaxToken token)
    {
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            if (node is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } list } argument
                && IsT(call)
                && list.Arguments.IndexOf(argument) == 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The literal text in an expression: string literals and the text parts of interpolated strings.</summary>
    private static IEnumerable<string> Texts(SyntaxNode node) => node.DescendantTokens().Where(IsText).Select(t => t.ValueText);

    private static bool IsText(SyntaxToken token) =>
        token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
        || token.IsKind(SyntaxKind.CharacterLiteralToken) || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
        || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken);

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>Chinese characters and full-width punctuation.</summary>
    [GeneratedRegex(@"[　-〿㐀-䶿一-鿿＀-￯]")]
    private static partial Regex Chinese();
}
