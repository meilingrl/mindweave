using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

internal sealed class SummaryStorageFixture : IAsyncDisposable
{
    private readonly TestDirectory directory = new();
    public AppPaths Paths { get; }
    public SqliteWorkspaceStore Store { get; private set; } = null!;
    public ISummaryVersionStore Versions => Assert.IsAssignableFrom<ISummaryVersionStore>(Store);
    public Guid Workspace { get; private set; }
    public Guid Conversation { get; private set; }
    public Guid Node { get; private set; }
    public Guid First { get; private set; }
    private SummaryStorageFixture() => Paths = AppPaths.ForTrack("codex-steady", directory.Path);
    public static async Task<SummaryStorageFixture> Open()
    {
        var f = new SummaryStorageFixture(); f.Store = await SqliteWorkspaceStore.OpenAsync(f.Paths);
        f.Workspace = (await f.Store.CreateAsync("Synthetic versions")).Id;
        f.Conversation = (await f.Store.CreateConversationAsync(f.Workspace)).Id;
        await BranchStorageTests.Answer(f.Store, f.Conversation, "question", "answer");
        var first = await f.Generate("First"); f.Node = first.NodeId; f.First = first.VersionId;
        return f;
    }
    public async Task<SummaryGenerationResult> Generate(string title)
    {
        var job = (await Store.AcceptKnowledgeSummaryAsync(Guid.NewGuid(), Conversation, BranchStorageTests.Config(32768))).Value!;
        Assert.NotNull(job);
        Assert.Equal(SummaryOutcomeKind.Applied, (await Store.PrepareKnowledgeSummaryRequestAsync(job.Id, job.Revision)).Kind);
        var result = await Store.CompleteKnowledgeSummaryAsync(new(job.Id, job.Revision, new(title, "# body\n" + title), "synthetic"));
        Assert.Equal(SummaryOutcomeKind.Applied, result.Kind); return result.Value!;
    }
    public async Task<SummaryNodeSnapshot> Snapshot() => (await Versions.LoadSummaryNodeAsync(Node))!;
    public async Task<SummaryEditDraft> Edit(string title, string body)
    {
        var begin = await Versions.BeginSummaryEditAsync((await Snapshot()).State.Token);
        Assert.Equal(SummaryOutcomeKind.Applied, begin.Kind);
        var saved = await Versions.SaveSummaryDraftAsync(begin.Value!.Token, new(title, body));
        Assert.Equal(SummaryOutcomeKind.Applied, saved.Kind); return saved.Value!;
    }
    public async Task<SummaryVersion> Commit(string title, string body = "manual body")
    {
        var draft = await Edit(title, body);
        var result = await Versions.CommitSummaryDraftAsync((await Snapshot()).State.Token, draft.Token);
        Assert.Equal(SummaryOutcomeKind.Applied, result.Kind); return result.Value!;
    }
    public object? Sql(string sql, params (string, object?)[] args) => ContextStorageTests.Sql(Paths, sql, args);
    public string DatabaseSnapshot() => SummaryMigrationTests.Snapshot(Paths.DatabasePath);
    public async ValueTask DisposeAsync() { await Store.DisposeAsync(); directory.Dispose(); }
}
