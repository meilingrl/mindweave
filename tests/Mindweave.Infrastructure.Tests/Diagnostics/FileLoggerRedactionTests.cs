using Microsoft.Extensions.Logging;
using Mindweave.Infrastructure.Logging;

namespace Mindweave.Infrastructure.Tests.Diagnostics;

public sealed class FileLoggerRedactionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mindweave-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // A test skipped before writing (the read-only folder test on Windows) leaves no folder to remove.
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Keys_in_messages_and_exceptions_never_reach_the_file()
    {
        using (var provider = new FileLoggerProvider(_directory, TimeProvider.System, new Mindweave.Application.Diagnostics.KnownSecrets()))
        {
#pragma warning disable CA1848, CA2254 // The point is an arbitrary, unstructured message.
            provider.CreateLogger("test").LogWarning(new InvalidOperationException("echo sk-live-AbCdEf123456"), "header Bearer abcdefgh12345678");
#pragma warning restore CA1848, CA2254
        }

        var text = File.ReadAllText(Directory.EnumerateFiles(_directory).Single());
        Assert.Contains("[redacted]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live", text, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefgh12345678", text, StringComparison.Ordinal);
    }

    /// <summary>Low findings observability-9, cross-platform-10: a log folder that cannot be written never takes the app down.</summary>
    [Fact]
    public void Logging_into_a_read_only_folder_is_quietly_skipped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses POSIX permissions.");
        if (OperatingSystem.IsWindows())
        {
            return; // Unreachable after the skip; tells the platform analyzer the Unix calls below are safe.
        }

        var folder = Path.Combine(_directory, "read-only");
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var provider = new FileLoggerProvider(folder, TimeProvider.System, new Mindweave.Application.Diagnostics.KnownSecrets());
#pragma warning disable CA1848 // A plain call in a test of the logger itself.
            provider.CreateLogger("test").LogWarning("nothing may be thrown");
#pragma warning restore CA1848
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
