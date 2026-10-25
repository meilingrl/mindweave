using Mindweave.Application.Chat;
using Mindweave.Infrastructure.AI;
using Mindweave.Infrastructure.Credentials;
namespace Mindweave.Desktop.Services;

/// <summary>Owns the single app-lifetime coordinator and optional HTTP client. Never owned by a page.</summary>
public sealed class DesktopChatSession : IAsyncDisposable
{
    private readonly IDisposable? ownedTransport;
    public GenerationCoordinator Coordinator { get; }
    public ChatConfigurationService Configuration { get; }
    public DesktopChatSession(IChatStore store, IChatTransport transport, ICredentialVault vault, IDisposable? ownedTransport = null)
    {
        Coordinator = new(store, transport, vault);
        Configuration = new(store, vault, transport);
        this.ownedTransport = ownedTransport;
    }
    public static DesktopChatSession Create(IChatStore store, string credentialNamespace)
    {
        var client = ChatCompletionsTransport.CreateHttpClient();
        return new(store, new ChatCompletionsTransport(client), new SystemCredentialVault(credentialNamespace), client);
    }
    public async ValueTask DisposeAsync() { await Coordinator.DisposeAsync(); ownedTransport?.Dispose(); }
}
