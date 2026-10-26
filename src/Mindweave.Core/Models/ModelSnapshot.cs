namespace Mindweave.Core.Models;

public enum ChatProtocol
{
    ChatCompletions,
}

/// <summary>
/// The configuration a generation task was accepted with. Frozen at acceptance: later settings changes
/// affect only new tasks. Holds a credential reference, never the secret.
/// </summary>
public sealed record ModelSnapshot(
    string ModelConfigId,
    string BaseUrl,
    ChatProtocol Protocol,
    string Model,
    string CredentialRef,
    int? MaxOutputTokens,
    ModelCapabilities Capabilities);
