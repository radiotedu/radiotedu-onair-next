using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace OnAirNext.Engine;

public sealed record ProgrammeMusicProgress(string Key, string StationId, long NextPosition, int PlayingAttempts);

public sealed partial class EventJournal
{
    public Task<ProgrammeMusicProgress> ProgrammeMusicProgressAsync(string key, string station)
    {
        ValidateScheduledKey(key, station);
        return TransactionAsync((connection, transaction) =>
        {
            var next = ReadMusicPosition(connection, transaction, key, station);
            using var count = ProgrammeDraftCommand(connection, transaction,
                "SELECT COUNT(*) FROM programme_music_attempts WHERE key=$key AND station_id=$station AND state='Playing'",
                ("$key", key), ("$station", station));
            return new ProgrammeMusicProgress(key, station, next, Convert.ToInt32(count.ExecuteScalar()));
        });
    }

    // Called in the same transaction as the audible event and checkpoint.
    // Starting, rather than ending, advances the cursor: two tracks may be
    // simultaneously audible during a crossfade, including across a crash.
    private static void RecordProgrammeMusicAttempt(SqliteConnection connection, SqliteTransaction transaction, PlaybackEvent entry)
    {
        if (entry.ProgrammeMusicKey is null && entry.ProgrammeMusicPosition is null)
        {
            if (entry.SuppressStationBranding) throw new ArgumentException("Only scheduled music may suppress station branding.");
            return;
        }
        if (entry.ProgrammeMusicKey is not { } key || entry.ProgrammeMusicPosition is not { } position ||
            position is < 0 or long.MaxValue || entry.Kind != ContentKind.Music || entry.QueueEntryId is not null ||
            entry.ScheduledOccurrenceKey is not null || string.IsNullOrWhiteSpace(entry.OccurrenceId) ||
            entry.OccurrenceId.Length > 128 || entry.OccurrenceId.Any(char.IsControl))
            throw new ArgumentException("The scheduled music attempt is invalid.");
        ValidateScheduledKey(key, entry.StationId);
        var next = ReadMusicPosition(connection, transaction, key, entry.StationId);
        var existing = ReadMusicAttempt(connection, transaction, entry.OccurrenceId);
        if (existing is not null && (existing.Value.Key != key || existing.Value.Station != entry.StationId || existing.Value.Position != position))
            throw new InvalidOperationException("This playback attempt belongs to another programme position.");

        if (entry.Type == "started")
        {
            if (existing is not null) throw new InvalidOperationException("This music attempt already exists.");
            var previous = entry.PreviousOccurrenceId is { } previousId ? ReadMusicAttempt(connection, transaction, previousId) : null;
            var retry = previous is { State: "Interrupted" } && previous.Value.Key == key &&
                previous.Value.Station == entry.StationId && previous.Value.Position == position && next == position + 1 &&
                IsLatestMusicAttempt(connection, transaction, key, position, entry.PreviousOccurrenceId!);
            if (!retry && next != position) throw new InvalidOperationException("The programme music position changed before this attempt started.");
            InsertMusicAttempt(connection, transaction, entry, position, "Playing");
            if (!retry) WriteMusicPosition(connection, transaction, key, entry.StationId, position + 1);
            return;
        }

        if (entry.Type == "decode_failed" && existing is null)
        {
            if (next != position) throw new InvalidOperationException("The failed music position is no longer current.");
            InsertMusicAttempt(connection, transaction, entry, position, "Failed");
            WriteMusicPosition(connection, transaction, key, entry.StationId, position + 1);
            return;
        }
        if (entry.Type == "decode_failed" && existing is { State: "Failed" }) return;
        var state = entry.Type switch
        {
            "ended" or "skipped" => "Completed",
            "interrupted" => "Interrupted",
            "failed" or "decode_failed" => "Failed",
            _ => throw new ArgumentException("Unsupported scheduled music event.")
        };
        if (existing is not { State: "Playing" }) throw new InvalidOperationException("The music terminal does not match a playing attempt.");
        using var update = ProgrammeDraftCommand(connection, transaction,
            "UPDATE programme_music_attempts SET state=$state WHERE attempt_id=$attempt AND state='Playing'",
            ("$state", state), ("$attempt", entry.OccurrenceId));
        if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("The scheduled music attempt could not be updated.");
    }

    private static long ReadMusicPosition(SqliteConnection connection, SqliteTransaction transaction, string key, string station)
    {
        using var command = ProgrammeDraftCommand(connection, transaction,
            "SELECT station_id,next_position FROM programme_music_progress WHERE key=$key", ("$key", key));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return 0;
        if (reader.GetString(0) != station || reader.GetInt64(1) < 0) throw new InvalidDataException("The programme music position is inconsistent.");
        return reader.GetInt64(1);
    }

    private static (string Key, string Station, long Position, string State)? ReadMusicAttempt(SqliteConnection connection,
        SqliteTransaction transaction, string attempt)
    {
        using var command = ProgrammeDraftCommand(connection, transaction,
            "SELECT key,station_id,position,state FROM programme_music_attempts WHERE attempt_id=$attempt", ("$attempt", attempt));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = reader.GetString(3); var position = reader.GetInt64(2);
        if (position < 0 || state is not ("Playing" or "Completed" or "Interrupted" or "Failed"))
            throw new InvalidDataException("The saved programme music attempt is inconsistent.");
        return (reader.GetString(0), reader.GetString(1), position, state);
    }

    private static void InsertMusicAttempt(SqliteConnection connection, SqliteTransaction transaction, PlaybackEvent entry, long position, string state)
    {
        using var insert = ProgrammeDraftCommand(connection, transaction,
            "INSERT INTO programme_music_attempts(attempt_id,key,station_id,position,state) VALUES($attempt,$key,$station,$position,$state)",
            ("$attempt", entry.OccurrenceId), ("$key", entry.ProgrammeMusicKey!), ("$station", entry.StationId),
            ("$position", position), ("$state", state));
        insert.ExecuteNonQuery();
    }

    private static bool IsLatestMusicAttempt(SqliteConnection connection, SqliteTransaction transaction, string key, long position, string attempt)
    {
        using var command = ProgrammeDraftCommand(connection, transaction,
            "SELECT attempt_id FROM programme_music_attempts WHERE key=$key AND position=$position ORDER BY rowid DESC LIMIT 1",
            ("$key", key), ("$position", position));
        return command.ExecuteScalar() as string == attempt;
    }

    // The ordinary checkpoint names one dominant item. If a crash occurs
    // during overlap, explicitly close every other audible attempt as unknown
    // rather than leaving it Playing forever or inventing a completion.
    private void RecoverOtherProgrammeMusicAttempts(SqliteConnection connection, SqliteTransaction transaction,
        string station, string? currentAttempt, PlayoutCheckpoint checkpoint)
    {
        using (var exists = ProgrammeDraftCommand(connection, transaction,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='programme_music_attempts'"))
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return;
        using var query = ProgrammeDraftCommand(connection, transaction, """
            SELECT e.payload FROM programme_music_attempts a
            JOIN playback_events e ON e.occurrence_id=a.attempt_id AND e.event_type='started'
            WHERE a.station_id=$station AND a.state='Playing' AND a.attempt_id<>$current
            ORDER BY e.sequence LIMIT 17
            """, ("$station", station), ("$current", currentAttempt ?? ""));
        var starts = new List<PlaybackEvent>();
        using (var reader = query.ExecuteReader())
            while (reader.Read()) starts.Add(JsonSerializer.Deserialize<PlaybackEvent>(reader.GetString(0))
                ?? throw new InvalidDataException("An overlapping programme attempt has no start record."));
        if (starts.Count > 16) throw new InvalidDataException("Too many unfinished programme music attempts require inspection.");
        using (var count = ProgrammeDraftCommand(connection, transaction,
            "SELECT COUNT(*) FROM programme_music_attempts WHERE station_id=$station AND state='Playing' AND attempt_id<>$current",
            ("$station", station), ("$current", currentAttempt ?? "")))
            if (Convert.ToInt32(count.ExecuteScalar()) != starts.Count)
                throw new InvalidDataException("An unfinished programme music attempt has no unique start record.");
        foreach (var start in starts)
        {
            var interrupted = start with { EventId = Guid.NewGuid().ToString("N"), Type = "interrupted", AtUtc = checkpoint.SavedAtUtc,
                SamplePosition = Math.Max(start.SamplePosition, checkpoint.ProgramSamples), TimingQuality = "last_committed_checkpoint",
                Detail = "An overlapping music attempt was interrupted by a process restart. The uncommitted tail is unknown." };
            if (WriteEvent(connection, transaction, interrupted)) RecordProgrammeMusicAttempt(connection, transaction, interrupted);
        }
    }

    private static void WriteMusicPosition(SqliteConnection connection, SqliteTransaction transaction, string key, string station, long position)
    {
        using var update = ProgrammeDraftCommand(connection, transaction,
            "INSERT INTO programme_music_progress(key,station_id,next_position) VALUES($key,$station,$position) ON CONFLICT(key) DO UPDATE SET next_position=excluded.next_position",
            ("$key", key), ("$station", station), ("$position", position));
        update.ExecuteNonQuery();
    }
}
