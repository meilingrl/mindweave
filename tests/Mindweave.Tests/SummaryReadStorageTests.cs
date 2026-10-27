using System.Collections.Immutable;
using Mindweave.Core.Summaries;
using Xunit;

namespace Mindweave.Tests;

public sealed class SummaryReadStorageTests
{
    private static SummaryReadObservation Observation(SummaryReadState s, long sequence, double seconds, bool visible = true, bool fits = false) =>
        new(s.Epoch, s.Revision, sequence, TimeSpan.FromSeconds(seconds), true, visible, fits, s.TextHash, [new(0, s.TextLength)]);
    [Fact]
    public async Task Open_load_manual_unread_and_new_epoch_preserve_per_version_read_semantics()
    {
        await using var f = await SummaryStorageFixture.Open(); var initial = (await f.Versions.LoadSummaryReadingAsync(f.First))!;
        Assert.False(initial.State.IsRead); Assert.Equal("", initial.State.TextHash);
        var opened = (await f.Versions.BeginSummaryReadingAsync(f.First, 0, " actual  readable 😀 text ")).Value!;
        Assert.False(opened.State.IsRead); Assert.Equal(22, opened.State.TextLength);
        var read = (await f.Versions.SaveSummaryReadingAsync(f.First, Observation(opened.State, 1, 0), new(2, .5, "test"))).Value!;
        Assert.True(read.State.IsRead); Assert.Equal(2, read.Location!.ScalarOffset);
        var unread = (await f.Versions.SetSummaryReadAsync(f.First, read.State.Epoch, read.State.Revision, false)).Value!;
        var suppressed = (await f.Versions.SaveSummaryReadingAsync(f.First, Observation(unread.State, 2, 5), null)).Value!;
        Assert.False(suppressed.State.IsRead); Assert.True(suppressed.State.SuppressAutomatic);
        var next = (await f.Versions.BeginSummaryReadingAsync(f.First, suppressed.State.Revision, "actual readable 😀 text")).Value!;
        Assert.False(next.State.IsRead); Assert.False(next.State.SuppressAutomatic);
        Assert.Equal(SummaryOutcomeKind.Obsolete, (await f.Versions.SaveSummaryReadingAsync(f.First, Observation(suppressed.State, 3, 6), null)).Kind);
        Assert.True((await f.Versions.SaveSummaryReadingAsync(f.First, Observation(next.State, 1, 7), null)).Value!.State.IsRead);
        var candidate = await f.Generate("Candidate"); Assert.False((await f.Versions.LoadSummaryReadingAsync(candidate.VersionId))!.State.IsRead);
        var cr = (await f.Versions.BeginSummaryReadingAsync(candidate.VersionId, 0, "candidate body")).Value!;
        await f.Versions.SetSummaryReadAsync(candidate.VersionId, cr.State.Epoch, cr.State.Revision, true);
        Assert.True((await f.Versions.AdoptSummaryCandidateAsync((await f.Snapshot()).State.Token)).Value!.CurrentIsRead);
    }
    [Fact]
    public async Task Read_CAS_rejects_older_newer_revision_hash_sequence_and_invalid_location_without_mutation()
    {
        await using var f = await SummaryStorageFixture.Open(); var state = (await f.Versions.BeginSummaryReadingAsync(f.First, 0, "abc")).Value!.State;
        var before = f.DatabaseSnapshot();
        foreach (var observation in new[] { Observation(state, 1, 0) with { ExpectedRevision = 0 }, Observation(state, 1, 0) with { ExpectedRevision = 9 }, Observation(state, 0, 0), Observation(state, 1, 0) with { TextHash = "wrong" } })
            Assert.Equal(SummaryOutcomeKind.Obsolete, (await f.Versions.SaveSummaryReadingAsync(f.First, observation, null)).Kind);
        foreach (var location in new[] { new SummaryReadLocation(-1, 0, "test"), new(4, 0, "test"), new(1, double.NaN, "test"), new(1, double.PositiveInfinity, "test"), new(1, -1, "test"), new(1, 0, "") })
            Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Versions.SaveSummaryReadingAsync(f.First, Observation(state, 1, 0), location)).Kind);
        Assert.Equal(SummaryOutcomeKind.Rejected, (await f.Versions.SaveSummaryReadingAsync(f.First, Observation(state, 1, 0) with { Intervals = [new(0, 4)] }, null)).Kind);
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Versions.BeginSummaryReadingAsync(f.First, 0, "abc")).Kind);
        Assert.Equal(before, f.DatabaseSnapshot());
    }
    [Fact]
    public async Task Short_body_requires_continuous_visible_dwell_and_deleted_version_never_revives()
    {
        await using var f = await SummaryStorageFixture.Open(); var s = (await f.Versions.BeginSummaryReadingAsync(f.First, 0, "abc")).Value!.State;
        foreach (var (seconds, visible) in new[] { (0, true), (2, true), (3, false), (30, true), (31, true) })
        {
            s = (await f.Versions.SaveSummaryReadingAsync(f.First, Observation(s, s.ObservationSequence + 1, seconds, visible, true), null)).Value!.State;
            Assert.False(s.IsRead);
        }
        s = (await f.Versions.SaveSummaryReadingAsync(f.First, Observation(s, 6, 33, true, true), null)).Value!.State; Assert.True(s.IsRead);
        var p = (await f.Versions.PreviewDeleteSummaryVersionsAsync(f.Node, [f.First])).Value!; await f.Versions.DeleteSummaryVersionsAsync(p);
        Assert.Null(await f.Versions.LoadSummaryReadingAsync(f.First));
        Assert.Equal(SummaryOutcomeKind.NotFound, (await f.Versions.BeginSummaryReadingAsync(f.First, s.Revision, "abc")).Kind);
        Assert.Equal(SummaryOutcomeKind.NotFound, (await f.Versions.SaveSummaryReadingAsync(f.First, Observation(s, 7, 34), null)).Kind);
        Assert.Equal(SummaryOutcomeKind.NotFound, (await f.Versions.SetSummaryReadAsync(f.First, s.Epoch, s.Revision, false)).Kind);
        Assert.Equal(0L, f.Sql("SELECT count(*) FROM summary_read_state"));
    }
    [Fact]
    public async Task Begin_with_absent_read_metadata_creates_state_only_for_existing_full_version()
    {
        await using var f = await SummaryStorageFixture.Open();
        f.Sql("DELETE FROM summary_read_state WHERE version_id=$id", ("$id", f.First));
        var before = f.DatabaseSnapshot();
        Assert.Equal(SummaryOutcomeKind.Conflict, (await f.Versions.BeginSummaryReadingAsync(f.First, 8, "abc")).Kind);
        Assert.Equal(before, f.DatabaseSnapshot());
        var opened = await f.Versions.BeginSummaryReadingAsync(f.First, 0, "abc");
        Assert.Equal(SummaryOutcomeKind.Applied, opened.Kind); Assert.Equal(1, opened.Value!.State.Epoch); Assert.Equal(1, opened.Value.State.Revision);
        Assert.False(opened.Value.State.IsRead);
        Assert.Equal(SummaryOutcomeKind.NotFound, (await f.Versions.BeginSummaryReadingAsync(Guid.NewGuid(), 0, "abc")).Kind);
        Assert.Equal(1L, f.Sql("SELECT count(*) FROM summary_read_state"));
    }

}
