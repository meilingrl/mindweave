using Mindweave.Application.Diagnostics;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Reading;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Low finding tests-6 (triage 2026-11-14): every value of every stored enumeration is written as its own name and read
/// back as itself. A value added to an enumeration but not to both directions here would be stored unreadable, or read
/// back as another value (unknown error kinds read as "invalid response").
/// </summary>
public class SqlNamesTests
{
    [Fact]
    public void Every_stored_enumeration_value_round_trips()
    {
        RoundTrips<MessageRole>(SqlNames.Of, SqlNames.ParseRole);
        RoundTrips<MessageState>(SqlNames.Of, SqlNames.ParseMessageState);
        RoundTrips<TitleSource>(SqlNames.Of, SqlNames.ParseTitleSource);
        RoundTrips<GenerationTaskState>(SqlNames.Of, SqlNames.ParseTaskState);
        RoundTrips<SummaryOrigin>(SqlNames.Of, SqlNames.ParseOrigin);
        RoundTrips<NameSource>(SqlNames.Of, SqlNames.ParseNameSource);
        RoundTrips<ReadSource>(SqlNames.Of, SqlNames.ParseReadSource);
        RoundTrips<GenerationErrorKind>(SqlNames.Of, SqlNames.ParseErrorKind);
        RoundTrips<ChatProtocol>(SqlNames.Of, SqlNames.ParseProtocol);
        RoundTrips<AiRequestOutcome>(SqlNames.Of, SqlNames.ParseOutcome);
        // Name suggestions are never task rows (they run as transient work and are recorded per workspace, migration v10).
        Assert.All(Enum.GetValues<GenerationTaskKind>().Where(k => k != GenerationTaskKind.NameSuggestion), kind => Assert.False(string.IsNullOrEmpty(SqlNames.Of(kind))));
    }

    private static void RoundTrips<T>(Func<T, string> write, Func<string, T> read)
        where T : struct, Enum
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in Enum.GetValues<T>())
        {
            var name = write(value);
            Assert.True(names.Add(name), $"{typeof(T).Name}.{value} is written as '{name}', like another value.");
            Assert.Equal(value, read(name));
        }
    }
}
