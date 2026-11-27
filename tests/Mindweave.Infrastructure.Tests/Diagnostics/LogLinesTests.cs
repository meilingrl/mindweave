using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mindweave.Application;
using Mindweave.Application.Credentials;
using Mindweave.Application.Diagnostics;
using Mindweave.Application.Storage;
using Mindweave.Infrastructure.Credentials;
using Mindweave.Infrastructure.Logging;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Diagnostics;

/// <summary>
/// What the log says (D2, low findings observability-5, -6): keys without a known shape are scrubbed once the key store
/// has passed them, a storage pause is one line and a count, and the start line names the build without a path.
/// </summary>
public sealed class LogLinesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mindweave-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_key_without_a_known_shape_is_scrubbed_once_the_key_store_passed_it()
    {
        const string written = "plainword-77-written-key";
        const string read = "zz9-ordinary-looking-read-key";
        var secrets = new KnownSecrets();
        var inner = new MemoryCredentialStore();
        await inner.WriteAsync("model", read, Ct);
        var services = new ServiceCollection();
        services.AddSingleton(secrets);
        services.AddSingleton<ICredentialStore>(inner);
        CredentialStores.RecordKeys(services);
        using (var provider = services.BuildServiceProvider())
        {
            var store = provider.GetRequiredService<ICredentialStore>();
            Assert.Equal(read, await store.ReadAsync("model", Ct));
            await store.WriteAsync("search", written, Ct);
        }

        using (var log = new FileLoggerProvider(_directory, TimeProvider.System, secrets))
        {
#pragma warning disable CA1848, CA2254 // An arbitrary message, as a service's error text would be.
            log.CreateLogger("test").LogWarning(new InvalidOperationException("echo " + read), "service said: " + written + " is wrong");
#pragma warning restore CA1848, CA2254
        }

        var text = await File.ReadAllTextAsync(Directory.EnumerateFiles(_directory).Single(), Ct);
        Assert.DoesNotContain(read, text, StringComparison.Ordinal);
        Assert.DoesNotContain(written, text, StringComparison.Ordinal);
        Assert.Contains("service said: [redacted] is wrong", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_longer_key_containing_a_shorter_one_is_removed_whole()
    {
        var secrets = new KnownSecrets();
        secrets.Add("abcdefgh12");
        secrets.Add("abcdefgh12-and-more");
        secrets.Add("short"); // Too short to be told from ordinary text: kept as it is.
        Assert.Equal("x [redacted] y [redacted] short", secrets.Scrub("x abcdefgh12-and-more y abcdefgh12 short"));
    }

    [Fact]
    public async Task A_storage_pause_is_one_line_then_a_count_when_it_ends()
    {
        var logger = new ListLogger();
        var failing = 3;
        var probe = new Probe(() => Interlocked.Decrement(ref failing) >= 0 ? throw Full() : Task.CompletedTask);
        await using var health = new StorageHealth(probe, new StorageHealthOptions { RetryInterval = TimeSpan.FromMilliseconds(10) }, logger);
        health.Start();

        health.Report(Full());
        health.Report(Full());
        health.Report(Full());
        await Chat.ChatHarness.UntilAsync(() => !health.IsBlocked && logger.Lines.Count >= 2); // Logged just after the block lifts.

        var lines = logger.Lines;
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("Error Core write failed", lines[0], StringComparison.Ordinal);
        Assert.Contains("[exception]", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Information Storage takes writes again", lines[1], StringComparison.Ordinal);
        Assert.Contains("2 more failed writes, 3 failed checks", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_during_a_pause_says_so_with_the_counts()
    {
        var logger = new ListLogger();
        var health = new StorageHealth(new Probe(() => throw Full()), new StorageHealthOptions { RetryInterval = TimeSpan.FromHours(1) }, logger);
        health.Start();
        health.Report(Full());
        await health.DisposeAsync();

        Assert.Equal(2, logger.Lines.Count);
        Assert.StartsWith("Warning Closing while storage is still paused", logger.Lines[1], StringComparison.Ordinal);
        Assert.Contains("0 more failed writes, 0 failed checks", logger.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_start_line_names_build_channel_system_and_schema_and_no_folder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var environment = new AppEnvironment("dev", "1.2.3", home, Path.Combine(home, "db"), home, home, 20, "abcdef1");
        var line = Installation.Describe(environment);
        Assert.StartsWith("Mindweave 1.2.3 (abcdef1), channel dev, ", line, StringComparison.Ordinal);
        Assert.Contains(Installation.System(), line, StringComparison.Ordinal);
        Assert.Contains(", schema 20, interface ", line, StringComparison.Ordinal);
        Assert.DoesNotContain(home, line, StringComparison.Ordinal);
    }

    private static StorageWriteException Full() => new(StorageFailureKind.Full, "simulated full disk", null);

    private sealed class Probe(Func<Task> behavior) : IStorageProbe
    {
        public Task ProbeWriteAsync(CancellationToken cancellationToken) => behavior();
    }

    /// <summary>Each entry as "Level message", with "[exception]" when one was given.</summary>
    private sealed class ListLogger : ILogger<StorageHealth>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Add($"{logLevel} {formatter(state, exception)}{(exception is null ? "" : " [exception]")}");
            }
        }
    }
}
