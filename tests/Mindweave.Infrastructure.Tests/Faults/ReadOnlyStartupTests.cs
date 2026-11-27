using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Faults;

/// <summary>
/// Health check 2026-11-12, item 4: a database that opens but cannot be written must lead to the recovery page, never
/// to a start that throws after the window is up.
/// </summary>
[Trait("Category", "FaultInjection")]
public sealed class ReadOnlyStartupTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        SetReadOnly(_dir.Paths.DatabasePath, readOnly: false);
        _dir.Dispose();
    }

    [Fact]
    public async Task A_read_only_database_file_is_reported_as_an_access_problem_not_as_ready_or_damaged()
    {
        await SeedAsync();
        SetReadOnly(_dir.Paths.DatabasePath, readOnly: true);
        await using var database = _dir.Database();

        var result = await database.OpenAsync(Ct);

        Assert.Equal(DatabaseOpenStatus.IoError, result.Status);
        Assert.Contains("read-only", result.ErrorDetail, StringComparison.OrdinalIgnoreCase);
        Assert.False(database.IsOpen);
    }

    [Fact]
    public async Task Starting_on_a_read_only_database_file_leads_to_the_recovery_page()
    {
        await SeedAsync();
        SetReadOnly(_dir.Paths.DatabasePath, readOnly: true);
        await using var database = _dir.Database();
        var startup = Coordinator(database, new SqliteConversationStore(database));

        var outcome = await startup.StartAsync(Ct);

        var recovery = Assert.IsType<StartupOutcome.NeedsRecovery>(outcome);
        Assert.Equal(DatabaseOpenStatus.IoError, recovery.Result.Status);
    }

    [Fact]
    public async Task A_write_refused_after_the_database_opened_leads_to_the_recovery_page()
    {
        await SeedAsync();
        await using var database = _dir.Database();
        var refusing = RefusingRecovery.Wrap(new SqliteConversationStore(database));
        var startup = Coordinator(database, refusing);

        var outcome = await startup.StartAsync(Ct);

        var recovery = Assert.IsType<StartupOutcome.NeedsRecovery>(outcome);
        Assert.Equal(DatabaseOpenStatus.IoError, recovery.Result.Status);
        Assert.Contains("disk full", recovery.Result.ErrorDetail, StringComparison.Ordinal);
    }

    private static StartupCoordinator Coordinator(SqliteDatabase database, IConversationStore conversations) =>
        new(database, new SqliteWorkspaceStore(database), conversations, new SystemClock(), new SqliteLocationStore(database, new SystemClock()), NullLogger<StartupCoordinator>.Instance, new NoResources());

    private async Task SeedAsync()
    {
        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        await new SqliteWorkspaceStore(database).AddAsync(new Workspace(WorkspaceId.New(), WorkspaceName.FromStored("seed"), DateTimeOffset.UtcNow), Ct);
        await database.CloseAsync();
    }

    private static void SetReadOnly(string path, bool readOnly)
    {
        if (!File.Exists(path))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(path);
            File.SetAttributes(path, readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(path, readOnly ? UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>A conversation store whose start-up recovery write is refused as on a full disk; everything else passes through.</summary>
    public class RefusingRecovery : DispatchProxy
    {
        private IConversationStore _inner = null!;

        public static IConversationStore Wrap(IConversationStore inner)
        {
            var proxy = Create<IConversationStore, RefusingRecovery>();
            ((RefusingRecovery)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IConversationStore.RecoverUnfinishedAsync))
            {
                throw new StorageWriteException(StorageFailureKind.Full, "disk full", null);
            }

            return targetMethod.Invoke(_inner, args);
        }
    }
}
