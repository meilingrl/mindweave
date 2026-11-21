using System.Reflection;
using System.Text.RegularExpressions;
using Mindweave.Application.Languages;

namespace Mindweave.Desktop.Tests.Text;

/// <summary>
/// Every piece of interface text reads in both languages when called (docs/design/bilingual-ui.md §8). The source
/// check (Architecture tests, UiTextSourceTests) covers every member; this one runs the members it can call with
/// sample arguments, which also catches text assembled from other members and formatting in each language.
/// </summary>
[Collection(LanguageTests.Name)]
public partial class UiTextCompletenessTests
{
    private static readonly HashSet<string> SameInBoth = new(StringComparer.Ordinal) { "简体中文" };

    /// <summary>Calls that return no text on purpose: a web citation names its site, not a place in the app.</summary>
    private static readonly HashSet<string> EmptyOnPurpose = new(StringComparer.Ordinal) { "CitationPlace(Web)" };

    [Fact]
    public void Every_member_is_English_under_English()
    {
        var problems = Results(UiLanguage.English)
            .Where(r => r.Text is not null && Chinese().IsMatch(Strip(r.Text)))
            .Select(r => $"{r.Member}: {r.Text}")
            .ToList();
        Assert.True(problems.Count == 0, $"{problems.Count} members:\n" + string.Join('\n', problems.Take(80)));
    }

    [Fact]
    public void Every_member_is_non_empty_in_both_languages()
    {
        var problems = Results(UiLanguage.English).Concat(Results(UiLanguage.Chinese))
            .Where(r => !EmptyOnPurpose.Contains(r.Member))
            .Where(r => r.Text is null ? !r.MayBeNull : string.IsNullOrWhiteSpace(r.Text))
            .Select(r => r.Member)
            .ToList();
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void Most_members_can_be_called_with_samples()
    {
        // The source check covers the rest; this keeps the runtime check from quietly shrinking.
        var members = Members().ToList();
        var callable = members.Count(m => Samples(m) is not null);
        Assert.True(callable * 10 >= members.Count * 8, $"only {callable} of {members.Count} members can be called");
    }

    private static string Strip(string text)
    {
        foreach (var same in SameInBoth)
        {
            text = text.Replace(same, string.Empty, StringComparison.Ordinal);
        }

        return text;
    }

    private static List<(string Member, string? Text, bool MayBeNull)> Results(UiLanguage language)
    {
        try
        {
            CurrentLanguage.Set(language);
            var results = new List<(string, string?, bool)>();
            var nullability = new NullabilityInfoContext();
            foreach (var member in Members())
            {
                foreach (var arguments in Samples(member) ?? [])
                {
                    var text = member switch
                    {
                        PropertyInfo p => (string?)p.GetValue(null),
                        FieldInfo f => (string?)f.GetValue(null),
                        MethodInfo m => (string?)m.Invoke(null, arguments),
                        _ => null,
                    };
                    var mayBeNull = member is MethodInfo method && nullability.Create(method.ReturnParameter).ReadState == NullabilityState.Nullable;
                    results.Add(($"{member.Name}({string.Join(", ", arguments)})", text, mayBeNull));
                }
            }

            return results;
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    private static IEnumerable<MemberInfo> Members() =>
        typeof(UiText).GetMembers(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m is PropertyInfo { PropertyType: var t } && t == typeof(string)
                     || m is FieldInfo { FieldType: var f } && f == typeof(string)
                     || m is MethodInfo { ReturnType: var r, IsSpecialName: false } && r == typeof(string))
            .OrderBy(m => m.Name, StringComparer.Ordinal);

    /// <summary>The argument lists to call a member with, or null when a parameter type has no samples.</summary>
    private static List<object?[]>? Samples(MemberInfo member)
    {
        if (member is FieldInfo field)
        {
            return field.IsLiteral || field.IsInitOnly ? [[]] : null;
        }

        if (member is not MethodInfo method)
        {
            return [[]];
        }

        var lists = new List<object?[]> { Array.Empty<object?>() };
        foreach (var parameter in method.GetParameters())
        {
            if (ValuesFor(parameter.ParameterType) is not { } values)
            {
                return null;
            }

            lists = lists.SelectMany(l => values.Select(v => l.Append(v).ToArray())).Take(64).ToList();
        }

        return lists;
    }

    private static object?[]? ValuesFor(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is { IsEnum: true })
        {
            return [.. Enum.GetValues(underlying).Cast<object?>(), null];
        }

        if (type.IsEnum)
        {
            return [.. Enum.GetValues(type).Cast<object?>()];
        }

        return type switch
        {
            _ when type == typeof(string) => ["x"],
            _ when type == typeof(int) => [1, 2],
            _ when type == typeof(long) => [1L, 2048L],
            _ when type == typeof(double) => [2.5],
            _ when type == typeof(decimal) => [2.5m],
            _ when type == typeof(bool) => [true, false],
            _ when type == typeof(TimeSpan) => [TimeSpan.FromSeconds(90), TimeSpan.FromHours(2)],
            _ when type == typeof(DateOnly) => [new DateOnly(2026, 11, 20)],
            _ when type == typeof(DateTimeOffset) => [new DateTimeOffset(2026, 11, 20, 21, 38, 0, TimeSpan.FromHours(8))],
            _ when type == typeof(IEnumerable<string>) || type == typeof(IReadOnlyList<string>) => [new[] { "a", "b" }],
            _ when type == typeof(Uri) => [new Uri("https://example.com/a")],
            _ => null,
        };
    }

    /// <summary>Chinese characters and full-width punctuation.</summary>
    [GeneratedRegex(@"[　-〿㐀-䶿一-鿿＀-￯]")]
    private static partial Regex Chinese();
}

/// <summary>Tests that change the process-wide language run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageTests
{
    public const string Name = "Language";
}
