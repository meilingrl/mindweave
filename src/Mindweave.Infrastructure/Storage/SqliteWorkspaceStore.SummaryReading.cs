using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Summaries;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Storage;

public sealed partial class SqliteWorkspaceStore
{
    public Task<SummaryReadingSnapshot?> LoadSummaryReadingAsync(Guid versionId) => ReadSummaryAsync((c, t) => ReadSummaryReading(c, t, versionId));
    private static SummaryReadingSnapshot? ReadSummaryReading(SqliteConnection c, SqliteTransaction t, Guid versionId)
    {
        using var q = ChatCommand(c, t, """
            SELECT r.epoch,r.revision,r.is_read,r.suppress_automatic,r.text_hash,r.text_length,r.seen_json,r.visible_dwell_ticks,
                r.observation_sequence,r.last_observation_ticks,r.was_active_visible,r.scalar_offset,r.offset_within_block,r.renderer_profile
            FROM summary_read_state r JOIN summary_version v ON v.id=r.version_id WHERE r.version_id=$id
            """, ("$id", versionId));
        using var r = q.ExecuteReader(); if (!r.Read()) return null;
        return new(new(versionId, r.GetInt64(0), r.GetInt64(1), r.GetBoolean(2), r.GetBoolean(3), r.GetString(4), r.GetInt32(5),
            SummaryJson<ImmutableArray<ReadableInterval>>(r.GetString(6)), TimeSpan.FromTicks(r.GetInt64(7)), r.GetInt64(8),
            r.IsDBNull(9) ? null : TimeSpan.FromTicks(r.GetInt64(9)), r.GetBoolean(10)),
            r.IsDBNull(11) ? null : new(r.GetInt32(11), r.GetDouble(12), r.GetString(13)));
    }
    private static void WriteSummaryReading(SqliteConnection c, SqliteTransaction t, SummaryReadingSnapshot snapshot)
    {
        var s = snapshot.State; var location = snapshot.Location;
        using var q = ChatCommand(c, t, """
            UPDATE summary_read_state SET epoch=$epoch,revision=$revision,is_read=$read,suppress_automatic=$suppress,text_hash=$hash,text_length=$length,
                seen_json=$seen,visible_dwell_ticks=$dwell,observation_sequence=$sequence,last_observation_ticks=$last,was_active_visible=$active,
                scalar_offset=$offset,offset_within_block=$block,renderer_profile=$renderer WHERE version_id=$id
            """, ("$id", s.VersionId), ("$epoch", s.Epoch), ("$revision", s.Revision), ("$read", s.IsRead), ("$suppress", s.SuppressAutomatic),
            ("$hash", s.TextHash), ("$length", s.TextLength), ("$seen", JsonSerializer.Serialize(s.Seen)), ("$dwell", s.VisibleDwell.Ticks),
            ("$sequence", s.ObservationSequence), ("$last", s.LastObservationTime?.Ticks), ("$active", s.WasActiveVisible),
            ("$offset", location?.ScalarOffset), ("$block", location?.OffsetWithinBlock), ("$renderer", location?.RendererProfile));
        if (q.ExecuteNonQuery() != 1) throw new InvalidDataException("Summary read metadata is missing.");
    }
    public Task<SummaryOutcome<SummaryReadingSnapshot>> BeginSummaryReadingAsync(Guid versionId, long expectedRevision, string readableText) =>
        WriteAsync<SummaryOutcome<SummaryReadingSnapshot>>((c, t) =>
        {
            var previous = ReadSummaryReading(c, t, versionId);
            if (previous is null)
            {
                using var exists = ChatCommand(c, t, "SELECT 1 FROM summary_version WHERE id=$id", ("$id", versionId));
                if (exists.ExecuteScalar() is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryVersionMissing");
            }
            if ((previous?.State.Revision ?? 0) != expectedRevision) return new(SummaryOutcomeKind.Conflict, Code: "ReadingRevisionChanged");
            SummaryReadState state;
            try { state = SummaryReadPolicy.Open(versionId, readableText, previous?.State); }
            catch (SummaryProtocolException) { return new(SummaryOutcomeKind.Rejected, Code: "InvalidReadableText"); }
            var location = state.TextHash == previous?.State.TextHash ? previous.Location : null;
            // Only explicit Begin may initialize absent metadata, after checking the full version inside this writer transaction.
            if (previous is null)
            {
                using var initial = ChatCommand(c, t, """
                    INSERT INTO summary_read_state(version_id,epoch,revision,is_read,suppress_automatic,text_hash,text_length,seen_json,visible_dwell_ticks,observation_sequence,was_active_visible)
                    VALUES($id,0,0,0,0,'',0,'[]',0,0,0)
                    """, ("$id", versionId)); initial.ExecuteNonQuery();
            }
            var result = new SummaryReadingSnapshot(state, location); WriteSummaryReading(c, t, result);
            return new(SummaryOutcomeKind.Applied, result);
        });
    public Task<SummaryOutcome<SummaryReadingSnapshot>> SaveSummaryReadingAsync(Guid versionId, SummaryReadObservation observation, SummaryReadLocation? location) =>
        WriteAsync<SummaryOutcome<SummaryReadingSnapshot>>((c, t) =>
        {
            var previous = ReadSummaryReading(c, t, versionId); if (previous is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryVersionMissing");
            if (location is not null && (location.ScalarOffset < 0 || location.ScalarOffset > previous.State.TextLength ||
                !double.IsFinite(location.OffsetWithinBlock) || location.OffsetWithinBlock < 0 || string.IsNullOrWhiteSpace(location.RendererProfile) ||
                location.RendererProfile.Length > 256 || location.RendererProfile.Any(char.IsControl)))
                return new(SummaryOutcomeKind.Rejected, Code: "InvalidReadingLocation");
            var decision = SummaryReadPolicy.Observe(previous.State, observation);
            if (decision.Kind != SummaryOutcomeKind.Applied) return new(decision.Kind, previous, decision.Code);
            var result = new SummaryReadingSnapshot(decision.Value!, location ?? previous.Location); WriteSummaryReading(c, t, result);
            return new(SummaryOutcomeKind.Applied, result);
        });
    public Task<SummaryOutcome<SummaryReadingSnapshot>> SetSummaryReadAsync(Guid versionId, long expectedEpoch, long expectedRevision, bool isRead) =>
        WriteAsync<SummaryOutcome<SummaryReadingSnapshot>>((c, t) =>
        {
            var previous = ReadSummaryReading(c, t, versionId); if (previous is null) return new(SummaryOutcomeKind.NotFound, Code: "SummaryVersionMissing");
            if (previous.State.Epoch != expectedEpoch || previous.State.Revision != expectedRevision) return new(SummaryOutcomeKind.Obsolete, Code: "StaleReadingObservation");
            var result = previous with { State = SummaryReadPolicy.SetManual(previous.State, isRead) }; WriteSummaryReading(c, t, result);
            return new(SummaryOutcomeKind.Applied, result);
        });
}
