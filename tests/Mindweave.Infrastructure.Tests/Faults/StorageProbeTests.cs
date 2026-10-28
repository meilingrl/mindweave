using System.Runtime.Versioning;
using Mindweave.Application.Storage;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.Faults;

/// <summary>
/// The recovery probe must reach the disk. In WAL mode a rolled-back database write never does, so on a real full disk
/// it would always succeed and lift the storage pause while nothing can be saved; the probe also writes a file.
/// </summary>
[Trait("Category", "FaultInjection")]
public class StorageProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task The_probe_writes_to_the_disk_so_a_folder_that_takes_no_new_files_fails_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A read-only folder attribute does not stop new files on Windows.");
        await using var h = await ChatHarness.StartAsync();
        var folder = h.DataDirectory;
        var mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // The database itself still takes the rolled-back write; only real file I/O notices.
            var error = await Assert.ThrowsAsync<StorageWriteException>(() => h.Database.ProbeWriteAsync(Ct));
            Assert.Equal(StorageFailureKind.ReadOnly, error.Kind);
        }
        finally
        {
            File.SetUnixFileMode(folder, mode);
        }

        await h.Database.ProbeWriteAsync(Ct);
        Assert.False(File.Exists(Path.Combine(folder, ".storage-probe")));
    }
}
