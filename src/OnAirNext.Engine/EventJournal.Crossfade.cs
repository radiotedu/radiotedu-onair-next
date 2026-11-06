using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed partial class EventJournal
{
    // The incoming start and this ownership marker commit together. A normal
    // checkpoint names only the dominant item, so an interrupted outgoing tail
    // must not be silently reported as a completed song after a restart.
    private static void RecordCrossfadeOwnership(SqliteConnection connection, SqliteTransaction transaction, PlaybackEvent entry)
    {
        if (entry.Type == "started" && entry.OverlapWithOccurrenceId is { } outgoing)
        {
            if (entry.Kind != ContentKind.Music || !Guid.TryParseExact(outgoing, "N", out _) || outgoing == entry.OccurrenceId)
                throw new InvalidDataException("Crossfade ownership is invalid.");
            using var started = PublicationCommand(connection, transaction, """
                SELECT COUNT(*) FROM playback_events WHERE station_id=$station AND occurrence_id=$outgoing AND event_type='started'
                """, ("$station", entry.StationId), ("$outgoing", outgoing));
            if (Convert.ToInt32(started.ExecuteScalar()) != 1) throw new InvalidDataException("The outgoing crossfade start is missing.");
            using var add = PublicationCommand(connection, transaction, """
                INSERT INTO playout_overlaps(station_id,outgoing_occurrence,incoming_occurrence) VALUES($station,$outgoing,$incoming)
                """, ("$station", entry.StationId), ("$outgoing", outgoing), ("$incoming", entry.OccurrenceId));
            add.ExecuteNonQuery();
        }
        if (entry.Type is "ended" or "skipped" or "failed" or "decode_failed" or "interrupted")
        {
            using var clear = PublicationCommand(connection, transaction,
                "DELETE FROM playout_overlaps WHERE station_id=$station AND (outgoing_occurrence=$outgoing OR incoming_occurrence=$outgoing)",
                ("$station", entry.StationId), ("$outgoing", entry.OccurrenceId));
            clear.ExecuteNonQuery();
        }
    }

    private void RecoverCrossfadeOwnership(SqliteConnection connection, SqliteTransaction transaction,
        string station, PlayoutCheckpoint checkpoint)
    {
        using var query = PublicationCommand(connection, transaction, """
            SELECT e.payload FROM playout_overlaps o
            JOIN playback_events e ON e.station_id=o.station_id AND e.occurrence_id=o.outgoing_occurrence AND e.event_type='started'
            WHERE o.station_id=$station ORDER BY e.sequence LIMIT 17
            """, ("$station", station));
        var starts = new List<PlaybackEvent>();
        using (var reader = query.ExecuteReader())
            while (reader.Read()) starts.Add(JsonSerializer.Deserialize<PlaybackEvent>(reader.GetString(0))
                ?? throw new InvalidDataException("An overlapping source has no valid start record."));
        using (var count = PublicationCommand(connection, transaction,
            "SELECT COUNT(*) FROM playout_overlaps WHERE station_id=$station", ("$station", station)))
            if (starts.Count > 16 || Convert.ToInt32(count.ExecuteScalar()) != starts.Count)
                throw new InvalidDataException("Overlapping source ownership requires inspection.");
        foreach (var start in starts)
        {
            if (checkpoint.State == "Playing" && checkpoint.Announced && checkpoint.OccurrenceId == start.OccurrenceId)
                throw new InvalidDataException("The dominant checkpoint disagrees with crossfade ownership.");
            var interrupted = start with { EventId = Guid.NewGuid().ToString("N"), Type = "interrupted",
                AtUtc = checkpoint.SavedAtUtc < start.AtUtc ? start.AtUtc : checkpoint.SavedAtUtc,
                SamplePosition = Math.Max(start.SamplePosition, checkpoint.ProgramSamples), TimingQuality = "last_committed_checkpoint",
                Detail = "An overlapping source was interrupted by a process restart; its uncommitted tail is unknown.",
                OverlapWithOccurrenceId = null };
            if (WriteEvent(connection, transaction, interrupted))
            {
                RecordQueueAttempt(connection, transaction, interrupted);
                RecordProgrammeMusicAttempt(connection, transaction, interrupted);
            }
            RecordCrossfadeOwnership(connection, transaction, interrupted);
        }
    }
}
