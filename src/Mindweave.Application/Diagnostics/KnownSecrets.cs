namespace Mindweave.Application.Diagnostics;

/// <summary>
/// The keys this process has read from or written to the system's key store (D2, architecture §17.2): the file log scrubs
/// each of them, so a key without a known shape is removed too. Values are held only in memory and never written anywhere;
/// a key removed from the store stays here until the process ends, which only means more is scrubbed.
/// </summary>
public sealed class KnownSecrets
{
    private readonly Lock _gate = new();
    private string[] _values = [];

    /// <summary>The keys known now, longest first, so a key that contains another is removed whole.</summary>
    public IReadOnlyList<string> Values
    {
        get
        {
            lock (_gate)
            {
                return _values;
            }
        }
    }

    /// <summary>Remembers <paramref name="secret"/>; values too short to be told from ordinary text are ignored (<see cref="Redaction"/>).</summary>
    public void Add(string? secret)
    {
        if (secret is null || secret.Length < Redaction.MinSecretLength)
        {
            return;
        }

        lock (_gate)
        {
            if (Array.IndexOf(_values, secret) >= 0)
            {
                return;
            }

            _values = [.. _values.Append(secret).OrderByDescending(v => v.Length)];
        }
    }

    /// <summary><see cref="Redaction.Scrub(string, string?)"/> with every known key.</summary>
    public string Scrub(string text) => Redaction.Scrub(text, Values);
}
