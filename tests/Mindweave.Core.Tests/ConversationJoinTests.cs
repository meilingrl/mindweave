using Mindweave.Core.Conversations;

namespace Mindweave.Core.Tests;

/// <summary>
/// Joining two lines of one conversation by time (docs/design/sharing-backup.md V2, scenarios 9 to 11), as a property over
/// generated cases: two devices start alike, each asks further and retries, in an order the generator interleaves.
/// </summary>
public sealed class ConversationJoinTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 8, 0, 0, TimeSpan.Zero);

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 400)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Both_sides_reach_one_line_by_time_with_every_answer_after_its_question(int seed)
    {
        var (a, b) = Generate(seed);

        var onA = ConversationJoin.Join(a, b);
        var onB = ConversationJoin.Join(b, a);

        // The same line, and the same numbers, whichever side imports which.
        Assert.Equal(onA.Messages, onB.Messages);
        var line = onA.Messages.Select(m => m.Id).ToList();
        Assert.Equal(a.Concat(b).Select(m => m.Id).ToHashSet(), line.ToHashSet());
        Assert.Equal(line.Count, line.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, line.Count), onA.Messages.Select(m => m.Seq));

        // Each side's own order is kept.
        Assert.True(IsSubsequence(a.Select(m => m.Id), line), "A's order");
        Assert.True(IsSubsequence(b.Select(m => m.Id), line), "B's order");

        // Every answer stands right after its question's group, and the questions go by when they were asked.
        var all = a.Concat(b).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var question = QuestionsOf(a).Concat(QuestionsOf(b)).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value);
        MessageId? current = null;
        foreach (var id in line)
        {
            if (all[id].Role == MessageRole.User)
            {
                current = id;
            }
            else
            {
                Assert.Equal(question[id], current);
            }
        }

        var questions = line.Where(id => all[id].Role == MessageRole.User).Select(id => all[id]).ToList();
        Assert.Equal(questions.OrderBy(q => q.At).ThenBy(q => q.Id.Value), questions);

        // Within a question, the latest attempt is last, so it is the one that stands (scenario 10).
        foreach (var group in line.Where(id => all[id].Role == MessageRole.Assistant).GroupBy(id => question[id]))
        {
            Assert.Equal(group.Max(id => (all[id].At, all[id].Id.Value)), (all[group.Last()].At, all[group.Last()].Id.Value));
        }

        // Importing again, either way, changes nothing and numbers nothing anew.
        var joined = line.Select(id => all[id] with { Seq = onA.Seqs[id] }).ToList();
        foreach (var side in new[] { a, b })
        {
            var again = ConversationJoin.Join(joined, side);
            Assert.False(again.Renumbered);
            Assert.Equal(onA.Messages, again.Messages);
        }
    }

    /// <summary>A branch's context follows the message it left from; a summary ends before the first message it did not know (scenario 11).</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Places_follow_their_message_and_a_summary_ends_before_what_it_did_not_know(int seed)
    {
        var (a, b) = Generate(seed);
        var onA = ConversationJoin.Join(a, b);
        var onB = ConversationJoin.Join(b, a);
        foreach (var side in new[] { a, b })
        {
            foreach (var at in side)
            {
                Assert.Equal(onA.Seqs[at.Id], onA.Follow(side, at.Seq));

                var coverage = onA.Coverage(side, at.Seq);
                Assert.Equal(coverage, onB.Coverage(side, at.Seq));
                var known = side.Where(m => m.Seq <= at.Seq).Select(m => m.Id).ToHashSet();
                var covered = onA.Messages.Where(m => m.Seq <= coverage).Select(m => m.Id).ToList();
                Assert.True(coverage == onA.Messages[0].Seq || covered.All(known.Contains), "a covered message it did not know");
                Assert.True(coverage <= onA.Seqs[at.Id]);
                var unknownBefore = onA.Messages.Any(m => m.Seq <= onA.Seqs[at.Id] && !known.Contains(m.Id));
                Assert.Equal(unknownBefore, coverage < onA.Seqs[at.Id]);
            }
        }

        Assert.Equal(JoinedLine.Nothing, onA.Follow(a, -1));
    }

    [Fact]
    public void Missing_messages_after_the_end_keep_what_is_here_and_their_own_numbers()
    {
        var q0 = Entry(MessageRole.User, 0, 0);
        var x0 = Entry(MessageRole.Assistant, 1, 1);
        var q1 = Entry(MessageRole.User, 2, 2);
        var x1 = Entry(MessageRole.Assistant, 3, 3);

        var joined = ConversationJoin.Join([q0, x0], [q0, x0, q1, x1]);

        Assert.False(joined.Renumbered);
        Assert.Equal([(q0.Id, 0), (x0.Id, 1), (q1.Id, 2), (x1.Id, 3)], joined.Messages);
    }

    [Fact]
    public void A_retry_on_each_side_of_the_last_shared_answer_stays_with_its_question()
    {
        var q0 = Entry(MessageRole.User, 0, 0);
        var x0 = Entry(MessageRole.Assistant, 1, 1);
        var retryHere = Entry(MessageRole.Assistant, 2, 5);
        var retryThere = Entry(MessageRole.Assistant, 2, 3);
        var later = Entry(MessageRole.User, 3, 4);

        var joined = ConversationJoin.Join([q0, x0, retryHere], [q0, x0, retryThere, later]);

        Assert.True(joined.Renumbered);
        Assert.Equal([q0.Id, x0.Id, retryThere.Id, retryHere.Id, later.Id], joined.Messages.Select(m => m.Id));
    }

    [Fact]
    public void Both_asking_in_an_empty_conversation_go_by_time()
    {
        var mine = Entry(MessageRole.User, 0, 7);
        var mineAnswer = Entry(MessageRole.Assistant, 1, 8);
        var theirs = Entry(MessageRole.User, 0, 2);
        var theirsAnswer = Entry(MessageRole.Assistant, 1, 3);

        var joined = ConversationJoin.Join([mine, mineAnswer], [theirs, theirsAnswer]);

        Assert.Equal([theirs.Id, theirsAnswer.Id, mine.Id, mineAnswer.Id], joined.Messages.Select(m => m.Id));

        // Mine covered its own question, but the other side's lies before it: coverage cannot go below the first message.
        Assert.Equal(0, joined.Coverage([mine, mineAnswer], 1));
    }

    [Fact]
    public void Questions_of_the_same_instant_go_by_identity()
    {
        var q0 = Entry(MessageRole.User, 0, 0);
        var x0 = Entry(MessageRole.Assistant, 1, 1);
        var one = Entry(MessageRole.User, 2, 5);
        var two = Entry(MessageRole.User, 2, 5);
        var (low, high) = one.Id.Value.CompareTo(two.Id.Value) < 0 ? (one, two) : (two, one);

        Assert.Equal([q0.Id, x0.Id, low.Id, high.Id], ConversationJoin.Order([q0, x0, one], [q0, x0, two]));
        Assert.Equal([q0.Id, x0.Id, low.Id, high.Id], ConversationJoin.Order([q0, x0, two], [q0, x0, one]));
    }

    /// <summary>
    /// Two lines of one conversation: a shared start (possibly empty), then each side asks and retries in turn, at times a
    /// clock gives out in the order the turns happen; sometimes two sides act in the same instant.
    /// </summary>
    private static (List<LineEntry> A, List<LineEntry> B) Generate(int seed)
    {
        var random = new Random(seed);
        var clock = 0;
        DateTimeOffset Tick(bool same = false) => Start.AddSeconds(same ? clock : ++clock);

        var shared = new List<LineEntry>();
        for (var i = random.Next(0, 4); i > 0; i--)
        {
            Ask(shared, Tick(), Tick());
        }

        var a = new List<LineEntry>(shared);
        var b = new List<LineEntry>(shared);
        var turns = random.Next(1, 9);
        var acted = new[] { false, false };
        for (var t = 0; t < turns || !acted[0] || !acted[1]; t++)
        {
            var which = random.Next(2);
            acted[which] = true;
            var side = which == 0 ? a : b;
            var same = random.Next(6) == 0;
            if (side.Count > 0 && side[^1].Role == MessageRole.Assistant && random.Next(4) == 0)
            {
                side.Add(new LineEntry(MessageId.New(), MessageRole.Assistant, side.Count, Tick()));
            }
            else
            {
                Ask(side, Tick(same), Tick());
            }
        }

        return (a, b);
    }

    private static void Ask(List<LineEntry> line, DateTimeOffset asked, DateTimeOffset answered)
    {
        line.Add(new LineEntry(MessageId.New(), MessageRole.User, line.Count, asked));
        line.Add(new LineEntry(MessageId.New(), MessageRole.Assistant, line.Count, answered));
    }

    private static LineEntry Entry(MessageRole role, int seq, int second) => new(MessageId.New(), role, seq, Start.AddSeconds(second));

    private static IEnumerable<KeyValuePair<MessageId, MessageId>> QuestionsOf(List<LineEntry> line)
    {
        MessageId? question = null;
        foreach (var message in line)
        {
            if (message.Role == MessageRole.User)
            {
                question = message.Id;
            }
            else
            {
                yield return new(message.Id, question!.Value);
            }
        }
    }

    private static bool IsSubsequence(IEnumerable<MessageId> part, List<MessageId> whole)
    {
        var at = 0;
        foreach (var id in part)
        {
            at = whole.IndexOf(id, at);
            if (at < 0)
            {
                return false;
            }

            at++;
        }

        return true;
    }
}
