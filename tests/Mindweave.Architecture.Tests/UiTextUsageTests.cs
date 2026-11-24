using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mindweave.Architecture.Tests;

/// <summary>
/// Interface texts and converters nobody shows are removed, not kept and translated: each member of UiText and of
/// Converters is named somewhere in src besides its own declaration.
/// </summary>
public partial class UiTextUsageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("UiText")]
    [InlineData("Converters")]
    public void Every_member_is_used(string type)
    {
        var src = Path.Combine(LayeringTests.RepositoryRoot(), "src", "Mindweave.Desktop");
        var files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .ToList();

        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = files.SelectMany(f => Access().Matches(f.Text)).Where(m => m.Groups["type"].Value == type).Select(m => m.Groups["member"].Value).ToHashSet(StringComparer.Ordinal);
        foreach (var (file, text) in files.Where(f => f.File.EndsWith(".cs", StringComparison.Ordinal) && f.Text.Contains($"class {type}", StringComparison.Ordinal)))
        {
            foreach (var declared in CSharpSyntaxTree.ParseText(text, cancellationToken: Ct).GetRoot(Ct).DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text == type))
            {
                foreach (var member in declared.Members.Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword)))
                {
                    var name = member switch
                    {
                        PropertyDeclarationSyntax p => p.Identifier.Text,
                        MethodDeclarationSyntax m => m.Identifier.Text,
                        _ => null,
                    };
                    if (name is not null)
                    {
                        members[name] = Path.GetFileName(file);
                    }
                }

                // Inside the class itself a member is named without the type.
                used.UnionWith(declared.DescendantNodes().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text));
            }
        }

        Assert.NotEmpty(members);
        var unused = members.Where(m => !used.Contains(m.Key)).Select(m => $"{m.Key} ({m.Value})").Order(StringComparer.Ordinal).ToList();
        Assert.True(unused.Count == 0, $"{type} members nothing uses (remove them):\n" + string.Join('\n', unused));
    }

    [GeneratedRegex(@"\b(?<type>UiText|Converters)\.(?<member>\w+)")]
    private static partial Regex Access();
}
