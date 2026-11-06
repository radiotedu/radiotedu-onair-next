using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed record ScheduledOccurrenceState(string Key, string StationId, string AttemptId,
    string State, int Attempts, DateTimeOffset UpdatedAtUtc);

public sealed partial class EventJournal
{
    public Task InitializeProgrammePlaybackAsync() => TransactionAsync((connection, transaction) =>
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS scheduled_playout_occurrences(
                key TEXT PRIMARY KEY, station_id TEXT NOT NULL, attempt_id TEXT NOT NULL,
                state TEXT NOT NULL, attempts INTEGER NOT NULL, updated_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS programme_music_progress(
                key TEXT PRIMARY KEY, station_id TEXT NOT NULL, next_position INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS programme_music_attempts(
                attempt_id TEXT PRIMARY KEY, key TEXT NOT NULL, station_id TEXT NOT NULL,
                position INTEGER NOT NULL, state TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS programme_music_attempts_occurrence ON programme_music_attempts(key,position);
            """;
        command.ExecuteNonQuery(); return true;
    });

    public Task<ScheduledOccurrenceState?> ScheduledOccurrenceAsync(string key, string station)
    {
        ValidateScheduledKey(key, station);
        return TransactionAsync((connection, transaction) => ReadScheduledOccurrence(connection, transaction, key));
    }

    private static void RecordScheduledAttempt(SqliteConnection connection, SqliteTransaction transaction, PlaybackEvent entry)
    {
        var key = entry.ScheduledOccurrenceKey;
        if (key is null) return;
        // This ledger owns single-shot timed insertions, not the music window's
        // many song occurrences. Ordinary special-day music must omit the key.
        if (entry.Kind is not (ContentKind.Program or ContentKind.Sweeper))
            throw new ArgumentException("Only timed insertion attempts use a scheduled occurrence key.");
        ValidateScheduledKey(key, entry.StationId);
        if (string.IsNullOrWhiteSpace(entry.OccurrenceId) || entry.OccurrenceId.Length > 128 || entry.OccurrenceId.Any(char.IsControl))
            throw new ArgumentException("A scheduled attempt needs a valid playback occurrence identity.");
        var prior = ReadScheduledOccurrence(connection, transaction, key);
        var state = entry.Type switch
        {
            "started" => "Playing",
            "ended" or "skipped" => "Completed",
            "interrupted" => "Interrupted",
            "failed" or "decode_failed" => "Failed",
            _ => throw new ArgumentException("Unsupported scheduled playback event.")
        };
        if (prior?.State == "Completed")
            throw new InvalidOperationException("This scheduled occurrence has already completed.");
        if (state == "Failed" && prior?.State == "Failed" && prior.AttemptId == entry.OccurrenceId)
            return; // A decoder failure may follow the failed terminal for the same attempt.
        if (state == "Playing")
        {
            if (prior?.State == "Playing") throw new InvalidOperationException("This scheduled occurrence already has an active attempt.");
        }
        else if (prior is null)
        {
            if (state != "Failed") throw new InvalidOperationException("A scheduled terminal event needs a started attempt.");
        }
        else if (prior.AttemptId != entry.OccurrenceId || prior.State != "Playing")
            throw new InvalidOperationException("The scheduled terminal event does not match its active attempt.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scheduled_playout_occurrences(key,station_id,attempt_id,state,attempts,updated_utc)
            VALUES($key,$station,$attempt,$state,$attempts,$at)
            ON CONFLICT(key) DO UPDATE SET attempt_id=excluded.attempt_id,state=excluded.state,
                attempts=excluded.attempts,updated_utc=excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$station", entry.StationId);
        command.Parameters.AddWithValue("$attempt", entry.OccurrenceId); command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$attempts", (prior?.Attempts ?? 0) + (state == "Playing" || prior is null ? 1 : 0));
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static ScheduledOccurrenceState? ReadScheduledOccurrence(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT station_id,attempt_id,state,attempts,updated_utc FROM scheduled_playout_occurrences WHERE key=$key";
        command.Parameters.AddWithValue("$key", key); using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = reader.GetString(2); var attempts = reader.GetInt32(3);
        if (state is not ("Playing" or "Completed" or "Interrupted" or "Failed") || attempts < 1 ||
            !DateTimeOffset.TryParseExact(reader.GetString(4), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated))
            throw new InvalidDataException("Stored scheduled occurrence state is invalid.");
        var station = reader.GetString(0); ValidateScheduledKey(key, station);
        return new(key, station, reader.GetString(1), state, attempts, updated);
    }

    private static void ValidateScheduledKey(string key, string station)
    {
        if (key is null || key.Length > 256 || string.IsNullOrWhiteSpace(station) || station.Length > 64 || station.Contains('/'))
            throw new ArgumentException("Choose a valid station and scheduled occurrence.");
        var parts = key.Split('/');
        if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "N", out _) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1 ||
            parts[2] != station || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            throw new ArgumentException("The scheduled occurrence does not match this station.");
    }
}
