using Mindweave.Application.Generation;
using Mindweave.Application.Summaries;
using Mindweave.Application.Chat;
using Mindweave.Infrastructure.AI;
using Mindweave.Infrastructure.Credentials;
namespace Mindweave.Desktop.Services;

/// <summary>Owns app-lifetime generation participants and the optional HTTP client. Never owned by a page.</summary>
public sealed class DesktopChatSession : IAsyncDisposable
{
    private readonly IDisposable? ownedTransport;
    public AppGenerationRuntime Runtime { get; } = new();
    public GenerationCoordinator Coordinator { get; }
    public KnowledgeSummaryCoordinator Summaries { get; }
    public GroupNamingCoordinator Naming { get; }
    public ChatConfigurationService Configuration { get; }
    public DesktopChatSession(IChatStore store, IChatTransport transport, ICredentialVault vault, IDisposable? ownedTransport = null)
    {
        var summaries = store as IKnowledgeSummaryStore ?? throw new ArgumentException("Session store must support knowledge summaries.", nameof(store));
        var naming = store as INodeNamingStore ?? throw new ArgumentException("Session store must support node naming.", nameof(store));
        var versions = store as ISummaryVersionStore ?? throw new ArgumentException("Session store must support summary versions.", nameof(store));
        Coordinator = new(store, transport, vault, Runtime);
        Summaries = new(summaries, store, Coordinator, transport, vault, Runtime);
        Naming = new(naming, versions, store, transport, vault, Runtime);
        Summaries.Committed += SummaryCommitted;
        Configuration = new(store, vault, transport);
        this.ownedTransport = ownedTransport;
    }
    public static DesktopChatSession Create(IChatStore store, string credentialNamespace)
    {
        var client = ChatCompletionsTransport.CreateHttpClient();
        return new(store, new ChatCompletionsTransport(client), new SystemCredentialVault(credentialNamespace), client);
    }
    public async Task InitializeAsync()
    {
        // One admission lease covers recovery of every implemented family before Root exposes the workbench.
        using var admission = await Runtime.EnterAcceptanceAsync();
        await Coordinator.InitializeAsync();
        await Summaries.InitializeAsync();
        await Naming.InitializeAsync();
    }
    private void SummaryCommitted(KnowledgeSummaryCommitted committed)
    {
        if (committed.Result.IsCandidate) return;
        // Coordinator tracks this asynchronous tail and reports its own persistence source.
        _ = Naming.NotifyCurrentChangedAsync(committed.Result.NodeId, committed.Result.VersionId)
            .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
    public async ValueTask DisposeAsync()
    {
        await Runtime.ShutdownAsync();
        Summaries.Committed -= SummaryCommitted;
        await Naming.DisposeAsync();
        await Summaries.DisposeAsync();
        await Coordinator.DisposeAsync();
        await Runtime.DisposeAsync();
        ownedTransport?.Dispose();
    }
}
