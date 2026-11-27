using Mindweave.Application.ManualBackup;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Tests.Chat;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.ManualBackup;

/// <summary>
/// Scenarios 9 to 11 as a property over generated cases, through the real stores and packages: two folders start alike;
/// each asks further in the same conversation, retries the last answer, asks in a conversation that was empty, branches from
/// a later answer and summarizes, in an order the case gives; then they exchange manual backups both ways.
/// </summary>
public sealed class ManualBackupJoinFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-backup-join", Guid.NewGuid().ToString("N"));

    public ManualBackupJoinFlowTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 40)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Two_folders_that_went_on_alike_reach_one_line_and_a_second_exchange_changes_nothing(int seed)
    {
        var random = new Random(seed);
        await using var a = await ChatHarness.StartAsync();
        await using var b = await ChatHarness.StartAsync();
        foreach (var h in new[] { a, b })
        {
            h.Client.Titles.FailNext(Core.Conversations.GenerationErrorKind.Unauthorized, 1000);
        }

        // What both start with: a conversation with one to three questions, and an empty branch of it.
        var log = new Log();
        a.Client.Reply("Answer 0");
        var started = await a.Conversations.StartAsync(a.Workspace, "Question 0?", Ct);
        var first = await a.WaitForAnswerAsync(started.Answer!.Value);
        var root = started.Conversation;
        log.Asked((await a.Store.GetMessagesAsync(root, Ct))[0].Id, first.Id);
        for (var i = random.Next(0, 3); i > 0; i--)
        {
            await AskAsync(a, root, log);
        }

        var empty = (await a.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(first, "Answer 0"), Ct)).Child;
        var package = Path.Combine(_dir, "start.mwbackup");
        await ManualBackupFlowTests.Backups(a).SaveAsync([a.Workspace], new Application.Export.ExportDestination("start", package, null), null, Ct);
        Assert.Equal(ManualBackupImportStatus.Imported, (await ManualBackupImportFlowTests.Importer(b).ImportAsync(await ManualBackupImportFlowTests.ReadAsync(b, package), Ct)).Status);

        // Each side goes on in its own way; the times are the clock's, in the order the turns happen.
        var branches = new List<ConversationId> { empty };
        var summaries = new List<(Core.Summaries.SummaryVersionId Version, IReadOnlySet<MessageId> Known)>();
        var acted = new[] { false, false };
        for (var turn = 0; turn < 3 + random.Next(6) || !acted[0] || !acted[1]; turn++)
        {
            var which = random.Next(2);
            acted[which] = true;
            var side = which == 0 ? a : b;
            var messages = await side.Store.GetMessagesAsync(root, Ct);
            switch (random.Next(10))
            {
                case 0 or 1 when messages[^1].Role == MessageRole.Assistant:
                    side.Client.Reply($"Retry {log.Count}");
                    var retried = await side.Conversations.RetryAsync(root, messages[^1].Id, Ct);
                    Assert.True(retried.Accepted, retried.Rejection?.ToString());
                    log.Retried(messages[^1].Id, (await side.WaitForAnswerAsync(retried.Answer!.Value)).Id);
                    break;
                case 2:
                    await AskAsync(side, empty, log);
                    break;
                case 3:
                    var answer = messages.Last(m => m.Role == MessageRole.Assistant);
                    var branch = (await side.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(answer, answer.Text), Ct)).Child;
                    await AskAsync(side, branch, log);
                    branches.Add(branch);
                    break;
                case 4:
                    var known = messages.Select(m => m.Id).ToHashSet();
                    await ManualBackupImportFlowTests.SummarizeAsync(side, root, $"Title: Summary {log.Count}\nShort name: S{log.Count}\n\nWhat was asked so far");
                    // A candidate stays home (§1); adopted, it is a saved version and travels.
                    var node = (await side.SummaryNodes.ForConversationAsync(root, Ct))!.Node;
                    if (node.Pending is { } candidate)
                    {
                        await side.SummaryService.AdoptAsync(node.Id, candidate, Ct);
                    }

                    summaries.Add((node.Pending ?? node.Current, known));
                    break;
                default:
                    await AskAsync(side, root, log);
                    break;
            }
        }

        // Both ways at once: each side's backup is saved before either imports.
        var fromA = Path.Combine(_dir, "a.mwbackup");
        var fromB = Path.Combine(_dir, "b.mwbackup");
        await ManualBackupFlowTests.Backups(a).SaveAsync([a.Workspace], new Application.Export.ExportDestination("a", fromA, null), null, Ct);
        await ManualBackupFlowTests.Backups(b).SaveAsync([a.Workspace], new Application.Export.ExportDestination("b", fromB, null), null, Ct);
        await ManualBackupImportFlowTests.Importer(a).ImportAsync(await ManualBackupImportFlowTests.ReadAsync(a, fromB), Ct);
        await ManualBackupImportFlowTests.Importer(b).ImportAsync(await ManualBackupImportFlowTests.ReadAsync(b, fromA), Ct);

        foreach (var conversation in new[] { root, empty })
        {
            var lineA = await LineAsync(a, conversation);
            var lineB = await LineAsync(b, conversation);
            Assert.Equal(lineA, lineB);
            Assert.Equal(Enumerable.Range(0, lineA.Count), lineA.Select(m => m.Seq));

            // Each answer right after its question's line, questions in the order they were asked, the latest attempt last.
            var messages = await a.Store.GetMessagesAsync(conversation, Ct);
            MessageId? question = null;
            foreach (var message in messages)
            {
                if (message.Role == MessageRole.User)
                {
                    question = message.Id;
                }
                else
                {
                    Assert.Equal(log.QuestionOf(message.Id), question);
                }
            }

            var asked = messages.Where(m => m.Role == MessageRole.User).ToList();
            Assert.Equal(asked.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id.Value).Select(m => m.Id), asked.Select(m => m.Id));
            foreach (var group in messages.Where(m => m.Role == MessageRole.Assistant).GroupBy(m => log.QuestionOf(m.Id)))
            {
                Assert.Equal(group.MaxBy(m => (m.CreatedAt, m.Id.Value))!.Id, group.Last().Id);
                Assert.Contains(group.Last(), ConversationRules.SelectContext(messages));
                Assert.All(group.Skip(1), m => Assert.NotNull(m.Supersedes));
            }

            Assert.Equal(messages.Select(m => m.Supersedes), (await b.Store.GetMessagesAsync(conversation, Ct)).Select(m => m.Supersedes));
        }

        // A branch keeps its context: up to the message it left from, as that message now stands, the same on both sides.
        var final = (await LineAsync(a, root)).ToDictionary(m => m.Id, m => m.Seq);
        foreach (var branch in branches)
        {
            var source = await a.Branches.GetSourceAsync(branch, Ct);
            Assert.Equal(source, await b.Branches.GetSourceAsync(branch, Ct));
            var ranges = await ManualBackupImportFlowTests.RangesAsync(a, branch);
            Assert.Equal(ranges, await ManualBackupImportFlowTests.RangesAsync(b, branch));
            Assert.Equal((root, final[source!.SourceMessage!.Value]), ranges.Single(r => r.Item1 == root));
        }

        // A summary ends before the first message it did not know, so the conversation says later content is not in it.
        var line = await a.Store.GetMessagesAsync(root, Ct);
        var coverageA = await VersionsAsync(a, root);
        var coverageB = await VersionsAsync(b, root);
        foreach (var (version, known) in summaries)
        {
            var lastKnown = line.Last(m => known.Contains(m.Id)).Seq;
            var firstUnknown = line.FirstOrDefault(m => !known.Contains(m.Id))?.Seq ?? int.MaxValue;
            var expected = Math.Max(0, Math.Min(lastKnown, firstUnknown - 1));
            Assert.Equal(expected, coverageA[version]);
            Assert.Equal(expected, coverageB[version]);
            Assert.All(line.Where(m => m.Seq <= expected), m => Assert.True(known.Contains(m.Id) || expected == 0));
        }

        if (await a.SummaryNodes.ForConversationAsync(root, Ct) is { } summary)
        {
            Assert.Equal(summary.Current.ToSeq < line[^1].Seq, summary.Markers.HasLaterContent);
        }

        // A second exchange adds nothing and numbers nothing anew.
        var stateA = await ManualBackupImportFlowTests.StateAsync(a);
        var stateB = await ManualBackupImportFlowTests.StateAsync(b);
        var againA = Path.Combine(_dir, "a2.mwbackup");
        var againB = Path.Combine(_dir, "b2.mwbackup");
        await ManualBackupFlowTests.Backups(a).SaveAsync([a.Workspace], new Application.Export.ExportDestination("a2", againA, null), null, Ct);
        await ManualBackupFlowTests.Backups(b).SaveAsync([a.Workspace], new Application.Export.ExportDestination("b2", againB, null), null, Ct);
        foreach (var (side, other) in new[] { (a, againB), (b, againA), (a, fromB), (b, fromA) })
        {
            var read = await ManualBackupImportFlowTests.ReadAsync(side, other);
            Assert.True((await ManualBackupImportFlowTests.Importer(side).PlanAsync(read, Ct)).IsEmpty);
            Assert.Equal(ManualBackupImportStatus.NothingNew, (await ManualBackupImportFlowTests.Importer(side).ImportAsync(read, Ct)).Status);
        }

        Assert.Equal(stateA, await ManualBackupImportFlowTests.StateAsync(a));
        Assert.Equal(stateB, await ManualBackupImportFlowTests.StateAsync(b));
    }

    private static async Task AskAsync(ChatHarness side, ConversationId conversation, Log log)
    {
        side.Client.Reply($"Answer {log.Count}");
        var sent = await side.Conversations.AskAsync(conversation, $"Question {log.Count}?", Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        var answer = await side.WaitForAnswerAsync(sent.Answer!.Value);
        log.Asked((await side.Store.GetMessagesAsync(conversation, Ct)).Single(m => m.Seq == answer.Seq - 1).Id, answer.Id);
    }

    private static async Task<List<(MessageId Id, int Seq)>> LineAsync(ChatHarness h, ConversationId conversation) =>
        [.. (await h.Store.GetMessagesAsync(conversation, Ct)).Select(m => (m.Id, m.Seq))];

    /// <summary>Every summary version of the conversation by its identity, with how far it covers.</summary>
    private static async Task<Dictionary<Core.Summaries.SummaryVersionId, int>> VersionsAsync(ChatHarness h, ConversationId conversation) =>
        await h.SummaryNodes.ForConversationAsync(conversation, Ct) is { } entry
            ? (await h.SummaryNodes.GetAsync(entry.Node.Id, Ct))!.Versions.ToDictionary(v => v.Id, v => v.ToSeq)
            : [];

    /// <summary>Which question each answer belongs to, as the turns made them.</summary>
    private sealed class Log
    {
        private readonly Dictionary<MessageId, MessageId> _question = [];

        public int Count => _question.Count;

        public void Asked(MessageId question, MessageId answer) => _question[answer] = question;

        public void Retried(MessageId answer, MessageId retry) => _question[retry] = _question[answer];

        public MessageId QuestionOf(MessageId answer) => _question[answer];
    }
}
