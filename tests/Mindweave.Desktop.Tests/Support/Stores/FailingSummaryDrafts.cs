using Mindweave.Application.Storage;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop.Tests;

/// <summary>The real summary store, whose edit-draft writes fail while <see cref="Fail"/> is set.</summary>
internal sealed class FailingSummaryDrafts
{
    public volatile bool Fail;

    /// <summary>Saving an edit as a version fails as storage refusing the write.</summary>
    public volatile bool FailEdits;

    public ISummaryStore Wrap(SqliteDatabase database)
    {
        var proxy = System.Reflection.DispatchProxy.Create<ISummaryStore, Proxy>();
        ((Proxy)(object)proxy).Configure(new SqliteSummaryStore(database), this);
        return proxy;
    }

#pragma warning disable CA1852 // DispatchProxy derives a runtime type from it, so it cannot be sealed.
    public class Proxy : System.Reflection.DispatchProxy
#pragma warning restore CA1852
    {
        private ISummaryStore _inner = null!;
        private FailingSummaryDrafts _gate = null!;

        public void Configure(ISummaryStore inner, FailingSummaryDrafts gate)
        {
            _inner = inner;
            _gate = gate;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(ISummaryStore.SaveEditAsync) && _gate.FailEdits)
            {
                return Task.FromException<SummaryVersionId?>(new StorageWriteException(StorageFailureKind.Full, "simulated full disk", null));
            }

            return targetMethod.Name == nameof(ISummaryStore.SaveDraftAsync) && _gate.Fail
                ? Task.FromException(new IOException("simulated summary draft write failure"))
                : targetMethod.Invoke(_inner, args);
        }
    }
}
