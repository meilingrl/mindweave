using Mindweave.Core.Workspaces;

namespace Mindweave.Core.Tests;

public class WorkspaceNameTests
{
    [Theory]
    [InlineData("  火车 历史  ", "火车 历史")]
    [InlineData("a\t\n  b", "a b")]
    [InlineData("Café", "Café")]
    public void Normalizes_whitespace_and_unicode(string raw, string expected)
    {
        Assert.True(WorkspaceName.TryCreate(raw, out var name, out _));
        Assert.Equal(expected, name!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t　 ")]
    public void Rejects_blank(string? raw)
    {
        Assert.False(WorkspaceName.TryCreate(raw, out _, out var error));
        Assert.Equal(WorkspaceNameError.Empty, error);
    }

    [Fact]
    public void Length_counts_text_elements_not_utf16_units()
    {
        var emoji = string.Concat(Enumerable.Repeat("👍🏽", WorkspaceName.MaxLength));
        Assert.True(WorkspaceName.TryCreate(emoji, out _, out _));
        Assert.False(WorkspaceName.TryCreate(emoji + "x", out _, out var error));
        Assert.Equal(WorkspaceNameError.TooLong, error);
    }

    [Fact]
    public void New_ids_are_uuid_v7_and_round_trip()
    {
        // Ordering across milliseconds comes from the v7 timestamp prefix; within one millisecond .NET does not promise it.
        var id = WorkspaceId.New();
        Assert.Equal(7, id.Value.Version);
        Assert.Equal(id, WorkspaceId.Parse(id.ToString()));
        Assert.Equal(id.ToString(), id.ToString().ToLowerInvariant());
    }
}
