using System.IO.Pipes;

namespace Mindweave.Infrastructure.Hosting;

public enum SecondaryLaunchResult
{
    /// <summary>The running instance acknowledged and will bring its window forward.</summary>
    Activated,

    /// <summary>Another process holds the lock but did not answer in time.</summary>
    PrimaryNotResponding,
}

/// <summary>
/// One writer per data directory. The lock is an exclusively opened file, which the OS releases when the
/// process dies, so a killed instance never leaves a stale lock behind. A named pipe carries activation
/// requests from later launches to the holder.
/// </summary>
public sealed class SingleInstance : IAsyncDisposable
{
    private const byte ActivateRequest = 0x41;
    private const byte Acknowledge = 0x06;

    private readonly FileStream _lock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;

    private SingleInstance(FileStream lockStream, string pipeName)
    {
        _lock = lockStream;
        _pipeName = pipeName;
    }

    public static string PipeNameFor(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return $"mindweave-{paths.InstanceKey}";
    }

    /// <summary>Returns the holder, or null when another process already owns this data directory.</summary>
    public static SingleInstance? TryAcquire(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.DataDirectory);
        try
        {
            var stream = new FileStream(paths.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            WriteOwner(stream);
            return new SingleInstance(stream, PipeNameFor(paths));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Asks the holder to come forward. Retries while the holder may still be starting its listener.</summary>
    public static async Task<SecondaryLaunchResult> SignalPrimaryAsync(AppPaths paths, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return SecondaryLaunchResult.PrimaryNotResponding;
            }

            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
                await using var client = new NamedPipeClientStream(".", PipeNameFor(paths), PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(attempt.Token).ConfigureAwait(false);
                await client.WriteAsync(new[] { ActivateRequest }, attempt.Token).ConfigureAwait(false);
                await client.FlushAsync(attempt.Token).ConfigureAwait(false);
                var reply = new byte[1];
                var read = await client.ReadAsync(reply, attempt.Token).ConfigureAwait(false);
                if (read == 1 && reply[0] == Acknowledge)
                {
                    return SecondaryLaunchResult.Activated;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Connect or reply timed out for this attempt; try again until the deadline.
            }
            catch (IOException)
            {
                // Pipe broken or not yet created.
            }
            catch (TimeoutException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts answering activation requests. <paramref name="onActivate"/> runs on a pool thread.</summary>
    public void StartListening(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        if (_listener is not null)
        {
            throw new InvalidOperationException("Already listening.");
        }

        _listener = Task.Run(() => ListenAsync(onActivate, _stop.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_listener is not null)
        {
            try
            {
                await _listener.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
        await _lock.DisposeAsync().ConfigureAwait(false);
    }

    private static void WriteOwner(FileStream stream)
    {
        // Diagnostic only; correctness rests on the exclusive open.
        stream.SetLength(0);
        var text = System.Text.Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n");
        stream.Write(text);
        stream.Flush(flushToDisk: false);
    }

    private async Task ListenAsync(Action onActivate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var request = new byte[1];
                var read = await server.ReadAsync(request, cancellationToken).ConfigureAwait(false);
                if (read == 1 && request[0] == ActivateRequest)
                {
                    await server.WriteAsync(new[] { Acknowledge }, cancellationToken).ConfigureAwait(false);
                    await server.FlushAsync(cancellationToken).ConfigureAwait(false);
                    onActivate();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // A client disconnected mid-request; keep serving.
            }
        }
    }
}
