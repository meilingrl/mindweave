using System.Text.RegularExpressions;

namespace Mindweave.Architecture.Tests;

/// <summary>
/// Keeps docs/changes.md and the build's version in step (docs/design/versioning.md §5 and §7): the newest version
/// section names the version in Directory.Build.props, version sections descend and stand above the dated entries
/// that predate them, and each section has one line per change, then how to try it and what was not verified. The
/// source package leaves the change log out; without the file there is nothing to check.
/// </summary>
public partial class ChangeLogRulesTests
{
    private const string DatedEntriesHeading = "## Before version headings";

    [Fact]
    public void The_newest_version_section_is_the_build_version()
    {
        if (ChangeLog() is not { } lines)
        {
            return;
        }

        var props = File.ReadAllText(Path.Combine(LayeringTests.RepositoryRoot(), "Directory.Build.props"));
        var build = BuildVersion().Match(props).Groups["version"].Value;
        var newest = VersionSections(lines).Select(s => s.Version).FirstOrDefault();
        Assert.True(newest is not null, "docs/changes.md has no version section (a heading like \"## 0.5.10 · 2026-11-23\")");
        Assert.True(newest == build, $"docs/changes.md starts at {newest} but Directory.Build.props says {build}");
    }

    [Fact]
    public void Version_headings_descend_and_stand_above_the_dated_entries()
    {
        if (ChangeLog() is not { } lines)
        {
            return;
        }

        var sections = VersionSections(lines).ToList();
        var problems = new List<string>();
        for (var i = 1; i < sections.Count; i++)
        {
            if (new Version(sections[i - 1].Version) <= new Version(sections[i].Version))
            {
                problems.Add($"line {sections[i].Line}: {sections[i].Version} is not below {sections[i - 1].Version}");
            }
        }

        // A heading that starts with a number is meant as a version heading; one in another form would go unchecked.
        problems.AddRange(lines.Select((text, i) => (Text: text, Line: i + 1))
            .Where(l => NumberedHeading().IsMatch(l.Text) && !VersionHeading().IsMatch(l.Text))
            .Select(l => $"line {l.Line}: write the heading as \"## 0.5.10 · 2026-11-23\": {l.Text}"));

        var dated = Array.IndexOf(lines, DatedEntriesHeading);
        problems.AddRange(sections.Where(s => dated >= 0 && s.Line - 1 > dated)
            .Select(s => $"line {s.Line}: {s.Version} comes after \"{DatedEntriesHeading}\""));
        Assert.True(problems.Count == 0, "Version sections in docs/changes.md are out of order:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void Version_sections_follow_the_entry_form()
    {
        if (ChangeLog() is not { } lines)
        {
            return;
        }

        var problems = new List<string>();
        foreach (var section in VersionSections(lines))
        {
            var bullets = section.Body.Where(l => l.Text.StartsWith("- ", StringComparison.Ordinal)).ToList();
            if (bullets.Count < 2
                || !bullets[^2].Text.StartsWith("- Try: ", StringComparison.Ordinal)
                || !bullets[^1].Text.StartsWith("- Not verified: ", StringComparison.Ordinal))
            {
                problems.Add($"line {section.Line}: {section.Version} must end with a \"- Try: \" line and then a \"- Not verified: \" line");
                continue;
            }

            problems.AddRange(bullets.SkipLast(2)
                .Where(l => !ChangeLine().IsMatch(l.Text))
                .Select(l => $"line {l.Line}: start with Added, Changed, Fixed or Removed (after an optional \"Prefix: \"), or with \"Documents: \": {l.Text}"));

            // One line per change: nothing hangs under a line, and the bullet is always "- ".
            problems.AddRange(section.Body
                .Where(l => NestedOrOtherBullet().IsMatch(l.Text))
                .Select(l => $"line {l.Line}: one line per change, with no indented or \"*\" list items: {l.Text}"));

            // Added, then Changed, then Fixed, then Removed.
            var last = -1;
            foreach (var line in bullets.SkipLast(2))
            {
                var verb = Array.IndexOf(Verbs, ChangeLine().Match(line.Text).Groups["verb"].Value);
                if (verb >= 0 && verb < last)
                {
                    problems.Add($"line {line.Line}: {Verbs[verb]} lines come before {Verbs[last]} lines: {line.Text}");
                }

                last = Math.Max(last, verb);
            }
        }

        Assert.True(problems.Count == 0, "Version sections in docs/changes.md do not follow versioning.md §5:\n" + string.Join('\n', problems));
    }

    /// <summary>The change log's lines with their ends trimmed, or null where the file is not present.</summary>
    private static string[]? ChangeLog()
    {
        var path = Path.Combine(LayeringTests.RepositoryRoot(), "docs", "changes.md");
        return File.Exists(path) ? File.ReadAllLines(path).Select(l => l.TrimEnd()).ToArray() : null;
    }

    /// <summary>Each version section: its version, its heading's line number and the lines up to the next "## ".</summary>
    private static IEnumerable<(string Version, int Line, List<(int Line, string Text)> Body)> VersionSections(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var heading = VersionHeading().Match(lines[i]);
            if (!heading.Success)
            {
                continue;
            }

            var body = new List<(int, string)>();
            for (var j = i + 1; j < lines.Length && !lines[j].StartsWith("## ", StringComparison.Ordinal); j++)
            {
                body.Add((j + 1, lines[j]));
            }

            yield return (heading.Groups["version"].Value, i + 1, body);
        }
    }

    [GeneratedRegex(@"^## (?<version>\d+\.\d+\.\d+) · \d{4}-\d{2}-\d{2}$")]
    private static partial Regex VersionHeading();

    [GeneratedRegex(@"<Version>(?<version>[^<]+)</Version>")]
    private static partial Regex BuildVersion();

    [GeneratedRegex(@"^- (?:Documents: \S|(?:[^:]{1,40}: )?(?<verb>Added|Changed|Fixed|Removed) \S)")]
    private static partial Regex ChangeLine();

    [GeneratedRegex(@"^(?:\s+[-*+] |[*+] )")]
    private static partial Regex NestedOrOtherBullet();

    [GeneratedRegex(@"^## \d")]
    private static partial Regex NumberedHeading();

    private static readonly string[] Verbs = ["Added", "Changed", "Fixed", "Removed"];
}
