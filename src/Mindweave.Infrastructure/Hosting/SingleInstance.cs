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
        FileStream stream;
        try
        {
            stream = new FileStream(paths.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (IOException ex) when (!IsReadOnlyVolume(ex))
        {
            // Held by another instance. Anything else (no permission, a read-only volume) propagates: it is a failure to
            // report, not a running instance to signal (health check 2026-11-12, errors-6).
            return null;
        }

        try
        {
            WriteOwner(stream);
            return new SingleInstance(stream, PipeNameFor(paths));
        }
        catch
        {
            // Not kept open: a second attempt in this process must not find its own handle holding the lock.
            stream.Dispose();
            throw;
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
                // Only a pipe owned by this user is talked to (low finding gap-native-interop-6), as the server only accepts this user.
                await using var client = new NamedPipeClientStream(".", PipeNameFor(paths), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
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
            catch (UnauthorizedAccessException)
            {
                // A pipe of that name that is not this user's (review 2026-11-14): treated as no answer, never as a crash.
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

    /// <summary>How long a connected client has to send its request.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ListenRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>EROFS (30 on Linux and macOS), which .NET reports as a plain I/O error carrying the errno (review 2026-11-13).</summary>
    private static bool IsReadOnlyVolume(IOException ex) => !OperatingSystem.IsWindows() && ex.HResult == 30;

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

                // A client that connects and never sends is given up on (low finding concurrency-10): it used to hold the
                // only connection for good, so every later launch reported "not responding".
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                request.CancelAfter(RequestTimeout);
                var buffer = new byte[1];
                var read = await server.ReadAsync(buffer, request.Token).ConfigureAwait(false);
                if (read == 1 && buffer[0] == ActivateRequest)
                {
                    await server.WriteAsync(new[] { Acknowledge }, request.Token).ConfigureAwait(false);
                    await server.FlushAsync(request.Token).ConfigureAwait(false);
                    try
                    {
                        onActivate();
                    }
#pragma warning disable CA1031 // Bringing the window forward is a courtesy; it must not end the listener.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // That client's request timed out; serve the next.
            }
#pragma warning disable CA1031 // Anything else (a broken pipe, a refused create): keep serving after a short pause, never spin.
            catch (Exception)
#pragma warning restore CA1031
            {
                try
                {
                    await Task.Delay(ListenRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
