using System.Text.RegularExpressions;

namespace Mindweave.Architecture.Tests;

/// <summary>
/// The cheap, mechanical part of the document rules (AGENTS.md "Where files belong"): living documents link only to
/// files that exist, every design document and review batch can be found from its folder's index, and the source
/// package's exclusions name files that exist. Frozen records (docs/history, docs/reviews) are not checked: they keep the
/// paths of their time.
/// </summary>
public partial class DocsRulesTests
{
    [Fact]
    public void Living_documents_link_only_to_files_that_exist()
    {
        var root = LayeringTests.RepositoryRoot();
        var broken = new List<string>();
        foreach (var document in LivingDocuments(root))
        {
            var folder = Path.GetDirectoryName(document)!;
            foreach (var (line, target) in RelativeLinks(File.ReadAllLines(document)))
            {
                var path = Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(target)));
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    broken.Add($"{Path.GetRelativePath(root, document)}:{line} → {target}");
                }
            }
        }

        Assert.True(broken.Count == 0, "Broken links in living documents:\n" + string.Join('\n', broken));
    }

    [Fact]
    public void Every_design_document_is_in_the_design_index()
    {
        var design = Path.Combine(LayeringTests.RepositoryRoot(), "docs", "design");
        var index = File.ReadAllText(Path.Combine(design, "README.md"));
        var missing = Directory.EnumerateFiles(design, "*.md")
            .Select(Path.GetFileName)
            .Where(name => name != "README.md" && !index.Contains($"]({name})", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0, "Add a row to docs/design/README.md for:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Every_review_batch_is_in_the_reviews_index()
    {
        var reviews = Path.Combine(LayeringTests.RepositoryRoot(), "docs", "reviews");
        var index = File.ReadAllText(Path.Combine(reviews, "README.md"));
        var missing = Directory.EnumerateDirectories(reviews)
            .Select(Path.GetFileName)
            .Where(batch => !index.Contains($"]({batch}/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0, "Add a row to docs/reviews/README.md for:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Every_source_package_exclusion_matches_a_file()
    {
        var root = LayeringTests.RepositoryRoot();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(f => !f.StartsWith(".git/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal) && !f.Contains("/obj/", StringComparison.Ordinal))
            .ToList();
        var stale = File.ReadAllLines(Path.Combine(root, ".gitattributes"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.EndsWith(" export-ignore", StringComparison.Ordinal))
            .Select(l => l[..l.IndexOf(' ', StringComparison.Ordinal)])
            .Where(pattern => !files.Any(Glob(pattern).IsMatch))
            .ToList();
        Assert.True(stale.Count == 0, ".gitattributes excludes paths that no longer exist (remove the lines):\n" + string.Join('\n', stale));
    }

    /// <summary>The root documents, docs/*.md, the design documents and the idea drafts.</summary>
    private static IEnumerable<string> LivingDocuments(string root)
    {
        foreach (var name in new[] { "AGENTS.md", "CLAUDE.md", "README.md", "STATUS.md" })
        {
            yield return Path.Combine(root, name);
        }

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md").Order(StringComparer.Ordinal))
        {
            yield return path;
        }

        foreach (var folder in new[] { "design", "ideas" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "docs", folder), "*.md").Order(StringComparer.Ordinal))
            {
                yield return path;
            }
        }
    }

    /// <summary>Relative link targets outside code, without their #anchor.</summary>
    private static IEnumerable<(int Line, string Target)> RelativeLinks(string[] lines)
    {
        var fenced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            foreach (Match match in Link().Matches(InlineCode().Replace(lines[i], string.Empty)))
            {
                var target = match.Groups["target"].Value;
                if (target.StartsWith('#') || target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.Ordinal))
                {
                    continue;
                }

                var anchor = target.IndexOf('#', StringComparison.Ordinal);
                yield return (i + 1, anchor < 0 ? target : target[..anchor]);
            }
        }
    }

    /// <summary>A .gitattributes pattern as a regular expression over paths relative to the root.</summary>
    private static Regex Glob(string pattern)
    {
        var directory = pattern.EndsWith('/');
        var body = Regex.Escape(pattern.TrimEnd('/')).Replace(@"\*\*", "\u0001", StringComparison.Ordinal).Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace("\u0001", ".*", StringComparison.Ordinal);
        var anchored = pattern.TrimEnd('/').Contains('/', StringComparison.Ordinal) ? $"^{body}" : $"(^|.*/){body}";
        return new Regex(directory ? $"{anchored}/.*$" : $"{anchored}(/.*)?$", RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"\[[^\]]*\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"`[^`]*`")]
    private static partial Regex InlineCode();
}
