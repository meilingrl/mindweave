using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Desktop.SummaryNetwork;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Saving the explorations' places hitting a real full disk (architecture §11.4): the save is rolled back whole and not
/// reported, the picture stays in memory, content storage is not paused, and the next load after space returns saves them.
/// </summary>
public class LayoutDiskFullTests
{
    /// <summary>Enough explorations that their rows need new pages (about 40 rows fit in one).</summary>
    private const int Explorations = 150;

    [AvaloniaFact]
    public async Task Places_that_do_not_fit_are_not_saved_and_not_reported_and_the_next_load_saves_them()
    {
        await using var app = DesktopHarness.Create();
        var workspace = await app.StartWithWorkspaceAsync("火车历史");
        var database = app.Services.GetRequiredService<SqliteDatabase>();
        await SeedConversationsAsync(database, workspace.Id.ToString());
        var session = app.Services.GetRequiredService<SummaryNetworkSessions>().Get(workspace.Id);

        await StoragePauseUiTests.FillDiskAsync(database);
        await session.LoadAsync(CancellationToken.None);

        Assert.Equal(Explorations, session.Positions.Count);
        Assert.Equal(0, await PlaceRowsAsync(database));
        Assert.False(app.Services.GetRequiredService<Application.Storage.StorageHealth>().IsBlocked);
        await AssertIntactAsync(database);

        await StoragePauseUiTests.FreeDiskAsync(database);
        await session.LoadAsync(CancellationToken.None);
        Assert.Equal(Explorations, await PlaceRowsAsync(database));
        await AssertIntactAsync(database);
    }

    private static Task<int> SeedConversationsAsync(SqliteDatabase database, string workspace) =>
        database.WriteAsync(
            (c, t) =>
            {
                for (var i = 0; i < Explorations; i++)
                {
                    c.Execute(t, "INSERT INTO conversation (id, workspace_id, title, title_source, created_at, updated_at) VALUES ($id, $ws, $title, 'manual', $at, $at);",
                        ("$id", Guid.CreateVersion7().ToString()), ("$ws", workspace), ("$title", $"会话{i}"), ("$at", 1000 + i));
                }

                return 0;
            },
            CancellationToken.None);

    private static Task<long> PlaceRowsAsync(SqliteDatabase database) =>
        database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM exploration_place;"), CancellationToken.None);

    private static Task<int> AssertIntactAsync(SqliteDatabase database) =>
        database.ReadAsync(
            c =>
            {
                Assert.Equal("ok", c.Scalar<string>(null, "PRAGMA integrity_check;"));
                using var command = c.Command(null, "PRAGMA foreign_key_check;");
                using var reader = command.ExecuteReader();
                Assert.False(reader.Read());
                return 0;
            },
            CancellationToken.None);
}
