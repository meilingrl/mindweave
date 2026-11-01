using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Mindweave.Application.Conversations;
using Mindweave.Desktop.Network;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// A layout save hitting a real full disk (architecture §11.4): the save is rolled back whole, it is a layout-save
/// failure that keeps the arrangement in memory, content storage is not paused, and saving again after space returns
/// writes the whole layout.
/// </summary>
public class LayoutDiskFullTests
{
    /// <summary>Enough nodes that their layout rows need new pages (about 40 rows fit in one).</summary>
    private const int Nodes = 150;

    [AvaloniaFact]
    public async Task A_layout_save_that_does_not_fit_fails_alone_keeps_the_arrangement_and_succeeds_after_space_returns()
    {
        await using var app = DesktopHarness.Create();
        var workspace = await app.StartWithWorkspaceAsync("火车历史");
        var database = app.Services.GetRequiredService<SqliteDatabase>();
        await SeedSummariesAsync(database, workspace.Id.ToString());
        var session = app.Services.GetRequiredService<NetworkSessions>().Get(workspace.Id);

        // Full before the network is opened: the automatic places that do not fit are not saved (and not reported).
        await StoragePauseUiTests.FillDiskAsync(database);
        await session.LoadAsync(CancellationToken.None);
        Assert.Equal(Nodes, session.Positions.Count);
        var placedBefore = await LayoutRowsAsync(database);
        Assert.InRange(placedBefore, 0, Nodes - 1);

        session.Arrange();
        var arranged = session.Positions.ToDictionary(p => p.Key, p => p.Value);
        Assert.False(await session.SaveAsync(CancellationToken.None));

        Assert.True(session.SaveFailed);
        Assert.True(session.IsDirty);
        Assert.Equal(arranged, session.Positions);
        Assert.Equal(placedBefore, await LayoutRowsAsync(database));
        Assert.False(app.Services.GetRequiredService<Application.Storage.StorageHealth>().IsBlocked);
        await AssertIntactAsync(database);

        await StoragePauseUiTests.FreeDiskAsync(database);
        Assert.True(await session.SaveAsync(CancellationToken.None));
        Assert.False(session.SaveFailed);
        Assert.False(session.IsDirty);
        Assert.Equal(Nodes, await LayoutRowsAsync(database));
        var saved = await app.Services.GetRequiredService<Application.Network.NetworkService>().GetLayoutAsync(workspace.Id, CancellationToken.None);
        Assert.Equal(arranged, saved.ToDictionary(p => p.Key, p => p.Value));
        await AssertIntactAsync(database);
    }

    /// <summary>Conversations with one saved summary version each, written directly (generating 150 summaries is slow).</summary>
    private static Task<int> SeedSummariesAsync(SqliteDatabase database, string workspace) =>
        database.WriteAsync(
            (c, t) =>
            {
                for (var i = 0; i < Nodes; i++)
                {
                    var (conversation, node, version) = (Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString());
                    c.Execute(t, "INSERT INTO conversation (id, workspace_id, title, title_source, created_at, updated_at) VALUES ($id, $ws, $title, 'manual', $at, $at);",
                        ("$id", conversation), ("$ws", workspace), ("$title", $"会话{i}"), ("$at", 1000 + i));
                    c.Execute(t, """
                        INSERT INTO summary_node (id, workspace_id, conversation_id, current_version_id, base_title, display_name, name_source, created_at, updated_at)
                        VALUES ($id, $ws, $conv, $v, $title, $title, 'base', $at, $at);
                        """, ("$id", node), ("$ws", workspace), ("$conv", conversation), ("$v", version), ("$title", $"总结{i}"), ("$at", 1000 + i));
                    c.Execute(t, """
                        INSERT INTO summary_version (id, node_id, seq, title, body, to_seq, origin, created_at, short_title)
                        VALUES ($id, $node, 1, $title, '正文', 1, 'generated', $at, $title);
                        """, ("$id", version), ("$node", node), ("$title", $"总结{i}"), ("$at", 1000 + i));
                }

                return 0;
            },
            CancellationToken.None);

    private static Task<long> LayoutRowsAsync(SqliteDatabase database) =>
        database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM node_layout;"), CancellationToken.None);

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
