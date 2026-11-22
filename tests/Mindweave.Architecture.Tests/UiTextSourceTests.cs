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

    /// <summary>
    /// English words written outside T(...) in UiText would show in English under Chinese too. Format strings, URLs and
    /// other machine text have no run of two lowercase words; anything that does is text and belongs inside T.
    /// </summary>
    [Fact]
    public void English_words_appear_only_inside_T()
    {
        var problems = new List<string>();
        foreach (var (file, root) in UiTextFiles())
        {
            foreach (var token in root.DescendantTokens().Where(IsText))
            {
                if (EnglishWords().IsMatch(token.ValueText) && !token.Parent!.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().Any(IsT))
                {
                    problems.Add($"{file}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1} English outside T: {token.ValueText}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>The language a T(...) call returns depends on the order of its arguments: English first, everywhere.</summary>
    [Fact]
    public void T_and_BilingualText_take_English_first_across_src()
    {
        var src = Path.Combine(LayeringTests.RepositoryRoot(), "src");
        var problems = new List<string>();
        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(IsSource))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Preview), path, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
            var pairs = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(IsT).Select(c => c.ArgumentList.Arguments)
                .Concat(root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
                    .Where(c => c is ObjectCreationExpressionSyntax { Type: var t } ? t.ToString().EndsWith("BilingualText", StringComparison.Ordinal) : c.Parent is not null && IsBilingualTarget(c))
                    .Where(c => c.ArgumentList is not null).Select(c => c.ArgumentList!.Arguments));
            foreach (var args in pairs.Where(a => a.Count == 2))
            {
                if (Texts(args[0].Expression).Any(t => Chinese().IsMatch(t) && !SameInBoth.Contains(t)))
                {
                    problems.Add($"{Path.GetRelativePath(src, path)}:{Line(args[0])} Chinese in the English argument");
                }

                if (Texts(args[1].Expression).Any(EnglishWords().IsMatch) && !Texts(args[1].Expression).Any(t => Chinese().IsMatch(t)))
                {
                    problems.Add($"{Path.GetRelativePath(src, path)}:{Line(args[1])} English in the Chinese argument");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>A target-typed <c>new("…", "…")</c> assigned to something declared as <c>BilingualText</c>.</summary>
    private static bool IsBilingualTarget(BaseObjectCreationExpressionSyntax creation) =>
        creation is ImplicitObjectCreationExpressionSyntax
        && creation.Ancestors().FirstOrDefault(a => a is PropertyDeclarationSyntax or FieldDeclarationSyntax or VariableDeclarationSyntax or MethodDeclarationSyntax) switch
        {
            PropertyDeclarationSyntax p => p.Type.ToString().TrimEnd('?').EndsWith("BilingualText", StringComparison.Ordinal),
            FieldDeclarationSyntax f => f.Declaration.Type.ToString().TrimEnd('?').EndsWith("BilingualText", StringComparison.Ordinal),
            VariableDeclarationSyntax v => v.Type.ToString().TrimEnd('?').EndsWith("BilingualText", StringComparison.Ordinal),
            MethodDeclarationSyntax m => m.ReturnType.ToString().TrimEnd('?').EndsWith("BilingualText", StringComparison.Ordinal) && creation.Parent is ArrowExpressionClauseSyntax or ReturnStatementSyntax,
            _ => false,
        };

    [GeneratedRegex(@"\b[a-z]{2,}\s+[a-z]{2,}\b")]
    private static partial Regex EnglishWords();

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

    /// <summary>
    /// Nothing static keeps interface text it read once, anywhere in the Desktop project: a language switch rebuilds the
    /// views, but a static value would stay in the old language (docs/design/bilingual-ui.md §6). Inside UiText no static
    /// initializer is allowed at all; elsewhere one may not read UiText or T(...) except inside a lambda.
    /// </summary>
    [Fact]
    public void No_static_value_keeps_interface_text()
    {
        var desktop = Path.Combine(LayeringTests.RepositoryRoot(), "src", "Mindweave.Desktop");
        var problems = new List<string>();
        foreach (var path in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories).Where(IsSource))
        {
            var inUiText = Path.GetFileName(path).StartsWith("UiText", StringComparison.Ordinal);
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Preview), path, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
            var initializers =
                root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                    .Where(f => f.Modifiers.Any(SyntaxKind.StaticKeyword) || f.Modifiers.Any(SyntaxKind.ConstKeyword))
                    .SelectMany(f => f.Declaration.Variables.Where(v => v.Initializer is not null).Select(v => (Name: v.Identifier.Text, Value: (SyntaxNode)v.Initializer!.Value)))
                .Concat(root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                    .Where(p => p.Modifiers.Any(SyntaxKind.StaticKeyword) && p.Initializer is not null)
                    .Select(p => (Name: p.Identifier.Text, Value: (SyntaxNode)p.Initializer!.Value)));
            foreach (var (name, value) in initializers)
            {
                if (inUiText ? !IsPlainValue(value) : ReadsText(value))
                {
                    problems.Add($"{Path.GetRelativePath(desktop, path)}:{Line(value)} {name}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>A number, character or other constant that is not text (a length limit, say).</summary>
    private static bool IsPlainValue(SyntaxNode value) =>
        !value.DescendantNodesAndSelf().Any(n => n is InvocationExpressionSyntax or InterpolatedStringExpressionSyntax
            or CollectionExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
            || n.IsKind(SyntaxKind.StringLiteralExpression));

    /// <summary>
    /// Whether a static initializer reads UiText or T(...) outside a lambda. A UiText method passed as a method group
    /// (a converter, say) is read at each call, so it does not count.
    /// </summary>
    private static bool ReadsText(SyntaxNode value) =>
        value.DescendantNodesAndSelf(n => n is not LambdaExpressionSyntax and not AnonymousMethodExpressionSyntax)
            .Any(n => n is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "UiText" } } member
                      && (!UiTextMethods.Value.Contains(member.Name.Identifier.Text) || member.Parent is InvocationExpressionSyntax)
                   || n is InvocationExpressionSyntax call && IsT(call));

    private static readonly Lazy<HashSet<string>> UiTextMethods = new(() =>
        UiTextFiles().SelectMany(f => f.Root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text)).ToHashSet(StringComparer.Ordinal));

    /// <summary>Interface text lives in UiText only: the rest of the Desktop project, code and XAML, has none.</summary>
    [Fact]
    public void Desktop_has_no_Chinese_outside_UiText()
    {
        var desktop = Path.Combine(LayeringTests.RepositoryRoot(), "src", "Mindweave.Desktop");
        var text = Path.Combine(desktop, "Text") + Path.DirectorySeparatorChar;
        var problems = new List<string>();
        foreach (var path in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.StartsWith(text, StringComparison.Ordinal) && IsSource(p)))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Preview), path, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
            foreach (var token in root.DescendantTokens().Where(t => IsText(t) || t.IsKind(SyntaxKind.CharacterLiteralToken)).Where(t => Chinese().IsMatch(t.ValueText)))
            {
                problems.Add($"{Path.GetRelativePath(desktop, path)}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1} {token.ValueText}");
            }
        }

        foreach (var path in Directory.EnumerateFiles(desktop, "*.axaml", SearchOption.AllDirectories).Where(IsSource))
        {
            var lines = XmlComment().Replace(File.ReadAllText(path), m => new string('\n', m.Value.Count(c => c == '\n'))).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (Chinese().IsMatch(lines[i]))
                {
                    problems.Add($"{Path.GetRelativePath(desktop, path)}:{i + 1} {lines[i].Trim()}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    internal static bool IsSource(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();

    private static IEnumerable<(string File, SyntaxNode Root)> UiTextFiles()
    {
        var folder = Path.Combine(LayeringTests.RepositoryRoot(), "src", "Mindweave.Desktop", "Text");
        foreach (var path in Directory.EnumerateFiles(folder, "UiText*.cs").Order(StringComparer.Ordinal))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Preview), path, cancellationToken: TestContext.Current.CancellationToken);
            yield return (Path.GetFileName(path), tree.GetRoot(TestContext.Current.CancellationToken));
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

    /// <summary>
    /// The literal text in an expression: string literals and the text parts of interpolated strings. Character
    /// literals are left out: they are parsing (a full-width colon a summary may contain), not text shown.
    /// </summary>
    private static IEnumerable<string> Texts(SyntaxNode node) => node.DescendantTokens().Where(IsText).Select(t => t.ValueText);

    internal static bool IsText(SyntaxToken token) =>
        token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
        || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
        || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken);

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>Chinese characters and full-width punctuation.</summary>
    [GeneratedRegex(@"[　-〿㐀-䶿一-鿿＀-￯]")]
    internal static partial Regex Chinese();
}
