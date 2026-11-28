using Mindweave.Application.Storage;

namespace Mindweave.Application.Preferences;

/// <summary>
/// The personal note (requirements §10.9, docs/design/reply-quality.md Q4): one text the user writes in Settings about
/// themself and how they want answers. It is sent with every answer request and every summary request, marked as the user's
/// own preferences; an empty note sends nothing. One note serves all workspaces; it is never shared, and nothing is derived
/// from it.
/// </summary>
public interface IPersonalNote
{
    /// <summary>The note as written; empty when there is none.</summary>
    Task<string> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies at once for this run, even when the write then fails. A note of only spaces is kept as empty. Longer than
    /// <see cref="PersonalNoteService.MaxLength"/> is refused: Settings never lets more be typed, and nothing is cut silently.
    /// </summary>
    Task SetAsync(string note, CancellationToken cancellationToken);
}

/// <summary>One preference key (no migration), held in memory once read or written, as for the other preferences.</summary>
public sealed class PersonalNoteService(IPreferenceStore store) : IPersonalNote, IDisposable
{
    public const string Key = "personal-note";

    /// <summary>The longest note, in characters (requirements §10.9: about 500).</summary>
    public const int MaxLength = 500;

    private readonly SemaphoreSlim _writing = new(1, 1);
    private string? _value;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _value) is { } known)
        {
            return known;
        }

        var stored = Normalize(await store.GetAsync(Key, cancellationToken).ConfigureAwait(false));
        Interlocked.CompareExchange(ref _value, stored, null);
        return Volatile.Read(ref _value)!;
    }

    public async Task SetAsync(string note, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.Length > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(note), note.Length, $"A personal note holds at most {MaxLength} characters.");
        }

        Volatile.Write(ref _value, Normalize(note));

        // Writes one after another, each the newest value: a slow earlier write never lands after a later one.
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var latest = Volatile.Read(ref _value)!;
            if (latest.Length == 0)
            {
                await store.RemoveAsync(Key, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await store.SetAsync(Key, latest, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writing.Release();
        }
    }

    public void Dispose() => _writing.Dispose();

    /// <summary>The text with line breaks as <c>\n</c>; only spaces count as no note.</summary>
    private static string Normalize(string? note) =>
        string.IsNullOrWhiteSpace(note) ? string.Empty : note.ReplaceLineEndings("\n");
}
