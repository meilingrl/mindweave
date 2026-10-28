using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Export;
using Mindweave.Infrastructure.Export;

namespace Mindweave.Infrastructure.Tests.Export;

/// <summary>
/// Writing an export (§8.3): a temporary file in the same folder replaces the target, so the target is either the
/// old file or the complete new one; every failure removes the temporary file and leaves the old file as it was.
/// </summary>
public sealed class AtomicFileWriterTests : IDisposable
{
    private const int SharingViolation = unchecked((int)0x80070020);

    private static readonly byte[] New = Encoding.UTF8.GetBytes("# 新\n");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mindweave-export-tests", Guid.NewGuid().ToString("N"));

    public AtomicFileWriterTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Target => Path.Combine(_dir, "总结.md");

    [Fact]
    public async Task A_new_file_is_written_with_exactly_the_content_and_no_temporary_file_is_left()
    {
        var mode = await Writer().WriteAsync(Local(Target), New, Ct);

        Assert.Equal(ExportWriteMode.Atomic, mode);
        Assert.Equal(New, await File.ReadAllBytesAsync(Target, Ct));
        Assert.Equal([Target], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task An_existing_file_is_replaced()
    {
        await File.WriteAllTextAsync(Target, "旧的内容，比新的长得多得多得多", Ct);

        await Writer().WriteAsync(Local(Target), New, Ct);

        Assert.Equal(New, await File.ReadAllBytesAsync(Target, Ct));
        Assert.Equal([Target], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_failed_replace_keeps_the_old_file_and_removes_the_temporary_one()
    {
        await File.WriteAllTextAsync(Target, "旧", Ct);
        var writer = Writer(move: (_, _) => throw new IOException("磁盘已满"));

        var error = await Assert.ThrowsAsync<ExportWriteException>(() => writer.WriteAsync(Local(Target), New, Ct));

        Assert.Equal(ExportFailure.Failed, error.Failure);
        Assert.Equal("旧", await File.ReadAllTextAsync(Target, Ct));
        Assert.Equal([Target], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_briefly_locked_target_is_retried_and_then_replaced()
    {
        await File.WriteAllTextAsync(Target, "旧", Ct);
        var attempts = 0;
        var writer = Writer(move: (temp, target) =>
        {
            if (++attempts < 3)
            {
                throw new IOException("in use", SharingViolation);
            }

            File.Move(temp, target, overwrite: true);
        });

        await writer.WriteAsync(Local(Target), New, Ct);

        Assert.Equal(3, attempts);
        Assert.Equal(New, await File.ReadAllBytesAsync(Target, Ct));
        Assert.Equal([Target], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_target_that_stays_locked_fails_as_in_use_after_the_retry_window()
    {
        await File.WriteAllTextAsync(Target, "旧", Ct);
        var writer = Writer(move: (_, _) => throw new IOException("in use", SharingViolation));
        var started = Stopwatch.GetTimestamp();

        var error = await Assert.ThrowsAsync<ExportWriteException>(() => writer.WriteAsync(Local(Target), New, Ct));

        Assert.Equal(ExportFailure.InUse, error.Failure);
        Assert.True(Stopwatch.GetElapsedTime(started) >= writer.RetryWindow);
        Assert.Equal("旧", await File.ReadAllTextAsync(Target, Ct));
        Assert.Equal([Target], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_missing_folder_fails_without_creating_anything()
    {
        var target = Path.Combine(_dir, "不存在", "总结.md");

        var error = await Assert.ThrowsAsync<ExportWriteException>(() => Writer().WriteAsync(Local(target), New, Ct));

        Assert.Equal(ExportFailure.Failed, error.Failure);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_a_lock_removes_the_temporary_file()
    {
        await File.WriteAllTextAsync(Target, "旧", Ct);
        using var cancel = new CancellationTokenSource();
        var writer = Writer(move: (_, _) =>
        {
            cancel.Cancel();
            throw new IOException("in use", SharingViolation);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(Local(Target), New, cancel.Token));

        Assert.Equal("旧", await File.ReadAllTextAsync(Target, Ct));
        Assert.Equal([Target], Directory.GetFiles(_dir));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_read_only_folder_fails_as_not_writable_and_keeps_the_old_file()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A read-only attribute on a Windows folder does not prevent creating files in it.");
        await File.WriteAllTextAsync(Target, "旧", Ct);
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var error = await Assert.ThrowsAsync<ExportWriteException>(() => Writer().WriteAsync(Local(Target), New, Ct));
            Assert.Equal(ExportFailure.NotWritable, error.Failure);
        }
        finally
        {
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal("旧", await File.ReadAllTextAsync(Target, Ct));
        Assert.Equal([Target], Directory.GetFiles(_dir));
    }

    /// <summary>Another program holds the target open without sharing (Windows enforces it; macOS does not).</summary>
    [Fact]
    public async Task A_target_held_open_by_another_program_is_retried_then_reported_as_in_use()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows enforces exclusive file handles.");
        await File.WriteAllTextAsync(Target, "旧", Ct);
        var writer = new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance);
        Assert.Equal(TimeSpan.FromSeconds(1.5), writer.RetryWindow);
        var started = Stopwatch.GetTimestamp();

        ExportWriteException error;
        using (new FileStream(Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            error = await Assert.ThrowsAsync<ExportWriteException>(() => writer.WriteAsync(Local(Target), New, Ct));
        }

        Assert.Equal(ExportFailure.InUse, error.Failure);
        Assert.True(Stopwatch.GetElapsedTime(started) >= writer.RetryWindow);
        Assert.Equal("旧", await File.ReadAllTextAsync(Target, Ct));
        Assert.Equal([Target], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_target_released_within_the_retry_window_is_replaced()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows enforces exclusive file handles.");
        await File.WriteAllTextAsync(Target, "旧", Ct);
        var held = new FileStream(Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(300, Ct);
                await held.DisposeAsync();
            },
            Ct);

        await new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance).WriteAsync(Local(Target), New, Ct);
        await release;

        Assert.Equal(New, await File.ReadAllBytesAsync(Target, Ct));
        Assert.Equal([Target], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_read_only_target_file_on_windows_fails_as_not_writable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Elsewhere a read-only file can be replaced by renaming over it.");
        await File.WriteAllTextAsync(Target, "旧", Ct);
        File.SetAttributes(Target, FileAttributes.ReadOnly);
        try
        {
            var error = await Assert.ThrowsAsync<ExportWriteException>(() => Writer().WriteAsync(Local(Target), New, Ct));
            Assert.Equal(ExportFailure.NotWritable, error.Failure);
        }
        finally
        {
            File.SetAttributes(Target, FileAttributes.Normal);
        }

        Assert.Equal("旧", await File.ReadAllTextAsync(Target, Ct));
        Assert.Equal([Target], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Without_a_local_path_the_picked_file_is_overwritten_through_its_stream()
    {
        await File.WriteAllTextAsync(Target, "旧的内容，比新的长得多得多得多", Ct);
        var destination = new ExportDestination("总结.md", null, _ => Task.FromResult<Stream>(new FileStream(Target, FileMode.Open, FileAccess.ReadWrite)));

        var mode = await Writer().WriteAsync(destination, New, Ct);

        Assert.Equal(ExportWriteMode.Direct, mode);
        Assert.Equal(New, await File.ReadAllBytesAsync(Target, Ct));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ExportDestination Local(string path) => new(Path.GetFileName(path), path, null);

    private static AtomicFileWriter Writer(Action<string, string>? move = null) =>
        move is null
            ? new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance) { RetryWindow = TimeSpan.FromMilliseconds(300), RetryInterval = TimeSpan.FromMilliseconds(30) }
            : new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance) { RetryWindow = TimeSpan.FromMilliseconds(300), RetryInterval = TimeSpan.FromMilliseconds(30), Move = move };
}
