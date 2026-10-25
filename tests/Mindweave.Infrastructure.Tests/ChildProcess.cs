using System.Diagnostics;

namespace Mindweave.Infrastructure.Tests;

/// <summary>Runs Mindweave.TestHost so a test can kill a real process at a known point.</summary>
public sealed class ChildProcess : IDisposable
{
    private readonly Process _process;

    private ChildProcess(Process process) => _process = process;

    public static ChildProcess Start(string command, string dataDirectory)
    {
        var host = Path.Combine(AppContext.BaseDirectory, "Mindweave.TestHost.dll");
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(host);
        info.ArgumentList.Add(command);
        info.ArgumentList.Add(dataDirectory);
        return new ChildProcess(Process.Start(info) ?? throw new InvalidOperationException("Could not start test host."));
    }

    public async Task<string> WaitForLineAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var line = await _process.StandardOutput.ReadLineAsync(cts.Token);
        return line ?? throw new InvalidOperationException($"Test host exited: {await _process.StandardError.ReadToEndAsync(cts.Token)}");
    }

    public async Task KillAsync()
    {
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }
}
