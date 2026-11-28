using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests.Migrations;

/// <summary>
/// gap-schema-3 (D-042; architecture §12.2): the schema version is the only gate for what is stored. A build that starts
/// writing a value an older build cannot read raises the version, with a migration that may be empty. This test keeps
/// that true for the names it can see: the set of stored names is recorded in <c>stored-names.txt</c> together with the
/// schema version it belongs to, and the set may change only together with that number.
/// <para>
/// What it sees: every table, column (with its type), index and trigger of a database made at the latest version; every
/// name a stored enumeration is written as (<see cref="SqlNames"/>, including the block kinds); the property names of
/// the JSON kept in columns (the <c>…Data</c> and <c>…Row</c> records of the stores, and the model capabilities); and
/// every preference key (a constant string named <c>Key</c> or <c>…Key</c>). Values written by a <c>switch</c> elsewhere
/// (an appearance's "light") are not seen; they follow the same rule by review.
/// </para>
/// <para>
/// A new preference key or a new JSON field is something an older build passes over (it never asks for the key, and
/// reading JSON ignores a field it does not know), so adding one is recorded without a new version. Anything else, and
/// removing or renaming any name, needs the number.
/// </para>
/// </summary>
public sealed class StoredNamesTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task The_stored_names_change_only_with_the_schema_version()
    {
        await using (var database = _dir.Database())
        {
            Assert.True((await database.OpenAsync(Ct)).IsReady);
        }

        var latest = MigrationCatalog.Default.LatestVersion;
        var actual = Collect(_dir.Paths.DatabasePath);
        var recordPath = Path.Combine(RepositoryRoot(), "tests", "Mindweave.Infrastructure.Tests", "Migrations", "stored-names.txt");
        var lines = File.ReadAllLines(recordPath);
        var recordedVersion = int.Parse(lines.Single(l => l.StartsWith("schema ", StringComparison.Ordinal))["schema ".Length..], System.Globalization.CultureInfo.InvariantCulture);
        var recorded = lines.Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("schema ", StringComparison.Ordinal)).ToList();
        var record = $"{Header}\nschema {latest}\n{string.Join('\n', actual)}\n";

        Assert.True(
            recordedVersion <= latest,
            $"{recordPath} records schema {recordedVersion}, newer than the latest migration ({latest}).");
        if (recordedVersion < latest)
        {
            Assert.Fail($"Schema {latest} is new: record its stored names. Replace {recordPath} with:\n{record}");
        }

        var added = actual.Except(recorded, StringComparer.Ordinal).ToList();
        var removed = recorded.Except(actual, StringComparer.Ordinal).ToList();
        var gated = added.Where(n => !n.StartsWith("key ", StringComparison.Ordinal) && !n.StartsWith("json ", StringComparison.Ordinal)).ToList();
        Assert.True(
            gated.Count > 0 || removed.Count > 0 || added.Count == 0,
            $"New preference keys or JSON fields, which an older build passes over: record them without a new version. Replace {recordPath} with:\n{record}");
        Assert.True(
            gated.Count == 0 && removed.Count == 0,
            $"The stored names changed while the schema stayed at {latest}. An older build would meet names it cannot read: "
                + "add a migration (it may be empty) to raise the version, then record the names for it.\n"
                + $"Added: {string.Join(", ", added)}\nRemoved: {string.Join(", ", removed)}\nNew record for {recordPath}:\n{record}");
    }

    private const string Header = "# Stored names per schema version (StoredNamesTests, gap-schema-3). Changes only with a new migration.";

    private static List<string> Collect(string databasePath)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        using (var connection = SqliteConnections.OpenReadOnly(databasePath))
        {
            foreach (var (type, name, table) in Rows(connection, "SELECT type, name, tbl_name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY name;"))
            {
                if (type == "table")
                {
                    names.Add($"table {name}");
                    foreach (var (column, columnType, _) in Rows(connection, $"SELECT name, type, '' FROM pragma_table_info('{name.Replace("'", "''", StringComparison.Ordinal)}');"))
                    {
                        names.Add($"column {name}.{column} {columnType}".TrimEnd());
                    }
                }
                else
                {
                    names.Add($"{type} {name} on {table}");
                }
            }
        }

        var infrastructure = typeof(SqlNames).Assembly;
        foreach (var method in typeof(SqlNames).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Of"))
        {
            var parameter = method.GetParameters() is [{ ParameterType.IsEnum: true } only] ? only.ParameterType : null;
            if (parameter is null)
            {
                continue;
            }

            foreach (var value in Enum.GetValues(parameter))
            {
                try
                {
                    names.Add($"value {parameter.Name} {method.Invoke(null, [value])}");
                }
                catch (TargetInvocationException)
                {
                    // A value that is never stored (it throws when asked for its name).
                }
            }
        }

        foreach (var field in typeof(SqlNames).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
        {
            names.Add($"value {field.Name} {field.GetRawConstantValue()}");
        }

        foreach (var type in infrastructure.GetTypes().Where(t => t.Namespace == typeof(SqlNames).Namespace && t.IsNested && (t.Name.EndsWith("Data", StringComparison.Ordinal) || t.Name.EndsWith("Row", StringComparison.Ordinal))))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                names.Add($"json {type.Name}.{JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)}");
            }
        }

        foreach (var property in typeof(Core.Models.ModelCapabilities).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            names.Add($"json ModelCapabilities.{JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)}");
        }

        foreach (var assembly in new[] { typeof(Application.Storage.IPreferenceStore).Assembly, infrastructure })
        {
            foreach (var field in assembly.GetTypes().SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)))
            {
                if (field.IsLiteral && field.FieldType == typeof(string) && field.Name.EndsWith("Key", StringComparison.Ordinal) && !field.Name.StartsWith('<'))
                {
                    names.Add($"key {field.GetRawConstantValue()}");
                }
            }
        }

        return [.. names];
    }

    private static List<(string, string, string)> Rows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
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
}
