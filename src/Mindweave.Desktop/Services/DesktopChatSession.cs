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
    public ChatConfigurationService Configuration { get; }
    public DesktopChatSession(IChatStore store, IChatTransport transport, ICredentialVault vault, IDisposable? ownedTransport = null)
    {
        var summaries = store as IKnowledgeSummaryStore ?? throw new ArgumentException("Session store must support knowledge summaries.", nameof(store));
        Coordinator = new(store, transport, vault, Runtime);
        Summaries = new(summaries, store, Coordinator, transport, vault, Runtime);
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
    }
    public async ValueTask DisposeAsync()
    {
        await Runtime.ShutdownAsync();
        await Summaries.DisposeAsync();
        await Coordinator.DisposeAsync();
        await Runtime.DisposeAsync();
        ownedTransport?.Dispose();
    }
}
