using System.Text.Json;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Preferences;
using Mindweave.Application.Storage;
using Mindweave.Application.Tools.Mcp;

namespace Mindweave.Application.Credentials;

/// <summary>
/// Every key Mindweave has stored, read only so an exported log can be scrubbed of it (D2 §2.2, architecture §17.2): the
/// model connection's, the web search key and each MCP server's secrets. The values never leave this process; they join
/// <see cref="KnownSecrets"/>, which is what the scrub uses.
/// </summary>
public sealed class StoredKeys(
    ICredentialStore credentials,
    IModelSettingsStore models,
    IPreferenceStore preferences,
    IMcpServers mcp,
    KnownSecrets known)
{
    /// <summary>The keys to scrub with, and whether the system's key store answered.</summary>
    public sealed record Result(IReadOnlyList<string> Keys, bool StoreAnswered);

    /// <summary>
    /// Reads every stored key. With <paramref name="dataAvailable"/> false the references cannot be read, so only the keys
    /// this process already met are used. A key store that does not answer leaves the scrub to credential shapes and the
    /// keys already met.
    /// </summary>
    public async Task<Result> ReadAllAsync(bool dataAvailable, CancellationToken cancellationToken)
    {
        var answered = true;
        if (dataAvailable)
        {
            try
            {
                foreach (var reference in await ReferencesAsync(cancellationToken).ConfigureAwait(false))
                {
                    AddEach(known, await credentials.ReadAsync(reference, cancellationToken).ConfigureAwait(false));
                }
            }
            catch (CredentialUnavailableException)
            {
                answered = false;
            }
        }
        else
        {
            answered = false;
        }

        return new Result(known.Values, answered);
    }

    /// <summary>
    /// Adds a stored value to <paramref name="known"/>: as it is, and when it is a JSON object (an MCP server's secrets), each
    /// of its string values too, so one secret in a log is found without the others around it.
    /// </summary>
    public static void AddEach(KnownSecrets known, string? stored)
    {
        ArgumentNullException.ThrowIfNull(known);
        known.Add(stored);
        if (stored is not { Length: > 1 } || stored[0] != '{')
        {
            return;
        }

        try
        {
            using var json = JsonDocument.Parse(stored);
            Walk(json.RootElement);
        }
        catch (JsonException)
        {
            // Not JSON after all: the whole value is already known.
        }

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        Walk(property.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item);
                    }

                    break;
                case JsonValueKind.String:
                    known.Add(element.GetString());
                    break;
            }
        }
    }

    private async Task<IReadOnlyList<string>> ReferencesAsync(CancellationToken cancellationToken)
    {
        var references = new List<string>();
        if (await models.GetActiveAsync(cancellationToken).ConfigureAwait(false) is { CredentialRef: { Length: > 0 } model })
        {
            references.Add(model);
        }

        if (await preferences.GetAsync(WebSearchSettingsService.CredentialKey, cancellationToken).ConfigureAwait(false) is { Length: > 0 } search)
        {
            references.Add(search);
        }

        references.AddRange((await mcp.ListAsync(cancellationToken).ConfigureAwait(false)).Select(s => s.Credential).OfType<string>().Where(r => r.Length > 0));
        return references;
    }
}
