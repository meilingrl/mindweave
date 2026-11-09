using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop.Tests;

/// <summary>The real reading-state store, whose draft writes fail while <see cref="Fail"/> is set.</summary>
internal sealed class FailingDrafts
{
    public volatile bool Fail;

    public IReadingStateStore Wrap(SqliteDatabase database) => new Store(new SqliteReadingStateStore(database), this);

    private sealed class Store(IReadingStateStore inner, FailingDrafts gate) : IReadingStateStore
    {
        public Task<ReadingPosition?> GetPositionAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken) =>
            inner.GetPositionAsync(conversation, slot, cancellationToken);

        public Task SavePositionAsync(ConversationId conversation, ViewerSlotKey slot, ReadingPosition position, DateTimeOffset at, CancellationToken cancellationToken) =>
            inner.SavePositionAsync(conversation, slot, position, at, cancellationToken);

        public Task<string?> GetDraftAsync(ConversationId conversation, ViewerSlotKey slot, CancellationToken cancellationToken) =>
            inner.GetDraftAsync(conversation, slot, cancellationToken);

        public Task SaveDraftAsync(ConversationId conversation, ViewerSlotKey slot, string text, DateTimeOffset at, CancellationToken cancellationToken) =>
            gate.Fail ? throw new IOException("simulated draft write failure") : inner.SaveDraftAsync(conversation, slot, text, at, cancellationToken);
    }
}
