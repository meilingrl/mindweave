using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests.Migrations;

/// <summary>
/// Health check 2026-11-12 (gap-schema-1, decided 2026-11-13): a database records only the number of the last migration
/// it ran, so a released migration that is edited afterwards would never reach the databases that already ran it, and
/// nothing would notice. Each migration's source is fingerprinted here: a released one must not change, and a new one
/// is added to <c>migration-fingerprints.txt</c> when it is written (the failure message gives the line).
/// </summary>
public partial class MigrationFingerprintTests
{
    [Fact]
    public void Released_migrations_are_never_edited_and_new_ones_are_recorded()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "Mindweave.Infrastructure", "Sqlite", "Migrations", "SchemaMigrations.cs"));
        var actual = Fingerprints(source);
        Assert.Equal(MigrationCatalog.Default.LatestVersion, actual.Count);

        var recordPath = Path.Combine(root, "tests", "Mindweave.Infrastructure.Tests", "Migrations", "migration-fingerprints.txt");
        var recorded = File.ReadAllLines(recordPath)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', 2))
            .ToDictionary(p => int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), p => p[1]);

        foreach (var (version, hash) in recorded)
        {
            Assert.True(actual.TryGetValue(version, out var now), $"Released migration {version} is gone.");
            Assert.True(now == hash, $"Released migration {version} was edited. Databases that already ran it will never see the change: undo the edit and add a new migration instead.");
        }

        var unrecorded = actual.Keys.Except(recorded.Keys).Order().Select(v => $"{v} {actual[v]}").ToList();
        Assert.True(unrecorded.Count == 0, $"New migrations are not recorded yet; add to {recordPath}:\n{string.Join('\n', unrecorded)}");
    }

    /// <summary>Hash of each migration's source block (<c>Migration.Sql(n, …)</c> or <c>new(n, …)</c>, as scripts/daily.sh reads
    /// them), line endings and trailing spaces normalized.</summary>
    internal static Dictionary<int, string> Fingerprints(string source)
    {
        var starts = MigrationStart().Matches(source).ToList();
        var result = new Dictionary<int, string>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Index : source.LastIndexOf("];", StringComparison.Ordinal);
            var block = source[starts[i].Index..end].Replace("\r\n", "\n", StringComparison.Ordinal);
            var normalized = string.Join('\n', block.Split('\n').Select(l => l.TrimEnd())).Trim();
            var version = int.Parse(starts[i].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            result[version] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
        }

        return result;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Mindweave.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (Mindweave.slnx) was not found above the test output.");
    }

    [GeneratedRegex(@"^[ \t]+(?:Migration\.Sql|new)\((\d+), """, RegexOptions.Multiline)]
    private static partial Regex MigrationStart();
}
