using System.IO.Pipes;

namespace Mindweave.Infrastructure.Runtime;

public sealed class SingleInstance : IDisposable
{
    private readonly FileStream lease;
    private readonly string pipeName;

    private SingleInstance(FileStream lease, string pipeName) => (this.lease, this.pipeName) = (lease, pipeName);

    public static SingleInstance? TryAcquire(AppPaths paths)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        try
        {
            return new(new FileStream(paths.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), paths.PipeName);
        }
        // EAGAIN on Linux, EWOULDBLOCK on macOS, sharing/lock violation on Windows.
        catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33 or 35)
        {
            return null;
        }
    }

    public static async Task NotifyExistingAsync(AppPaths paths, CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(".", paths.PipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ListenAsync(Action activate, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[1];
                if (await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) == 1 && buffer[0] == 1) activate();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public void Dispose() => lease.Dispose();
}
