using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>
/// The only writer. Work items run one at a time on a dedicated thread, each inside its own transaction,
/// so callers never contend for the write lock and every write is all-or-nothing.
/// </summary>
internal sealed class SqliteWriteQueue : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Channel<WorkItem> _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _consumer;

    public SqliteWriteQueue(SqliteConnection connection)
    {
        _connection = connection;
        _consumer = Task.Factory.StartNew(Consume, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public Task<T> EnqueueAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken cancellationToken)
    {
        var item = new WorkItem<T>(work, cancellationToken);
        if (!_channel.Writer.TryWrite(item))
        {
            throw new ObjectDisposedException(nameof(SqliteWriteQueue), "The database is closing; no further writes are accepted.");
        }

        return item.Completion.Task;
    }

    /// <summary>Stops accepting work, runs everything already queued, then closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private void Consume()
    {
        var reader = _channel.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var item))
            {
                item.Run(_connection);
            }
        }
    }

    private abstract class WorkItem
    {
        public abstract void Run(SqliteConnection connection);
    }

    private sealed class WorkItem<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken cancellationToken) : WorkItem
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Run(SqliteConnection connection)
        {
            // Cancellation is honored only before the write starts; a started transaction always finishes.
            if (cancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                using var transaction = connection.BeginTransaction();
                var result = work(connection, transaction);
                transaction.Commit();
                Completion.TrySetResult(result);
            }
#pragma warning disable CA1031 // The exception is handed to the caller through the task.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Completion.TrySetException(ex);
            }
        }
    }
}
