using Microsoft.Extensions.Logging;
using Mindweave.Infrastructure.Logging;

namespace Mindweave.Infrastructure.Tests.Diagnostics;

public sealed class FileLoggerRedactionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mindweave-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Keys_in_messages_and_exceptions_never_reach_the_file()
    {
        using (var provider = new FileLoggerProvider(_directory, TimeProvider.System))
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
}
