using Mindweave.Application.Storage;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop.Tests;

/// <summary>The real model settings store, whose saves are refused as on a full disk while <see cref="Refuse"/> is set.</summary>
internal sealed class RefusingSettingsStore
{
    public volatile bool Refuse;

    public IModelSettingsStore Wrap(SqliteDatabase database) => new Store(new SqliteModelSettingsStore(database), this);

    private sealed class Store(IModelSettingsStore inner, RefusingSettingsStore gate) : IModelSettingsStore
    {
        public Task<ModelSettings?> GetActiveAsync(CancellationToken cancellationToken) => inner.GetActiveAsync(cancellationToken);

        public Task SaveActiveAsync(ModelSettings settings, DateTimeOffset at, CancellationToken cancellationToken) =>
            gate.Refuse
                ? Task.FromException(new StorageWriteException(StorageFailureKind.Full, "simulated full disk", null))
                : inner.SaveActiveAsync(settings, at, cancellationToken);

        public Task<IReadOnlyList<string>> RetiredCredentialRefsAsync(CancellationToken cancellationToken) => inner.RetiredCredentialRefsAsync(cancellationToken);

        public Task SetRetiredCredentialRefsAsync(IReadOnlyList<string> references, CancellationToken cancellationToken) =>
            inner.SetRetiredCredentialRefsAsync(references, cancellationToken);
    }
}
