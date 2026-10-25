using Mindweave.Core.Chat;

namespace Mindweave.Application.Chat;

public sealed class ChatConfigurationService(IChatStore store, ICredentialVault vault, IChatTransport transport)
{
    public async Task SaveAsync(AiConfiguration configuration, string? replacementSecret)
    {
        string? created = null;
        if (replacementSecret is not null)
        {
            created = await vault.WriteNewAsync(replacementSecret).ConfigureAwait(false);
            configuration = configuration with { CredentialReference = created };
        }
        try { await store.SaveConfigurationAsync(configuration).ConfigureAwait(false); }
        catch
        {
            // This call alone owns the freshly created reference. Older task snapshots keep their versions.
            if (created is not null)
            {
                // If a storage implementation reports an uncertain outcome, do not delete a
                // reference which is now active. If verification fails, retain it for later repair.
                var active = await store.LoadActiveConfigurationAsync().ConfigureAwait(false);
                if (active?.CredentialReference != created) await vault.DeleteAsync(created).ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> DiscoverModelsAsync(AiConfiguration configuration, string? unsavedSecret, CancellationToken cancellationToken)
    {
        var secret = unsavedSecret ?? await vault.ReadAsync(configuration.CredentialReference).ConfigureAwait(false);
        return await transport.ListModelsAsync(configuration, secret, cancellationToken).ConfigureAwait(false);
    }
}
