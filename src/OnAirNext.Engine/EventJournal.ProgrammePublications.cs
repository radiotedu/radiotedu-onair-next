using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed partial class EventJournal
{
    public Task InitializeProgrammePublicationsAsync() => TransactionAsync((connection, transaction) =>
    {
        using var command = PublicationCommand(connection, transaction, """
            CREATE TABLE IF NOT EXISTS programme_publication_heads(id TEXT PRIMARY KEY,version INTEGER NOT NULL,active INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS programme_publication_versions(id TEXT NOT NULL,version INTEGER NOT NULL,payload TEXT NOT NULL,previous_hash TEXT NOT NULL,entry_hash TEXT NOT NULL,PRIMARY KEY(id,version));
            CREATE TABLE IF NOT EXISTS programme_publication_commands(id TEXT PRIMARY KEY,request_hash TEXT NOT NULL,publication_id TEXT NOT NULL,version INTEGER NOT NULL);
            CREATE TRIGGER IF NOT EXISTS programme_publication_version_no_update BEFORE UPDATE ON programme_publication_versions BEGIN SELECT RAISE(ABORT,'Programme publication history is immutable'); END;
            CREATE TRIGGER IF NOT EXISTS programme_publication_version_no_delete BEFORE DELETE ON programme_publication_versions BEGIN SELECT RAISE(ABORT,'Programme publication history is immutable'); END;
            CREATE TRIGGER IF NOT EXISTS programme_publication_command_no_update BEFORE UPDATE ON programme_publication_commands BEGIN SELECT RAISE(ABORT,'Programme publication receipts are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS programme_publication_command_no_delete BEFORE DELETE ON programme_publication_commands BEGIN SELECT RAISE(ABORT,'Programme publication receipts are immutable'); END;
            """);
        command.ExecuteNonQuery(); return true;
    });

    public async Task<ProgrammePublication> PublishPreparedProgrammeAsync(PreparedProgramme prepared, long expectedVersion,
        string commandId, string actor, IReadOnlySet<string> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);
        var known = stations.ToHashSet(StringComparer.Ordinal); RequireProgrammeActor(actor);
        var normalized = ProgrammePublicationValidation.Normalize(prepared, known, checkFiles: false);
        var command = ProgrammeCommandId(commandId);
        if (expectedVersion is < 0 or long.MaxValue) throw new ArgumentException("Review the current publication version.");
        var requestHash = PublicationHash(JsonSerializer.Serialize(new { Action = "publish", Programme = normalized, ExpectedVersion = expectedVersion, Actor = actor }));
        var prior = await TransactionAsync((connection, transaction) => PublicationReceipt(connection, transaction, command, requestHash, known));
        if (prior is not null) return prior;
        _ = ProgrammePublicationValidation.Normalize(normalized, known, checkFiles: true);
        return await TransactionAsync((connection, transaction) =>
        {
            if (PublicationReceipt(connection, transaction, command, requestHash, known) is { } receipt) return receipt;
            var draft = ProgrammeHead(connection, transaction, normalized.Draft.Rule.Id, known);
            if (draft is null || draft.Archived || JsonSerializer.Serialize(draft.Draft) != JsonSerializer.Serialize(normalized.Draft))
                throw new InvalidOperationException("The saved programme changed while its audio was prepared. Review it again.");
            var previous = PublicationHead(connection, transaction, normalized.Draft.Rule.Id, known);
            if ((previous?.Version ?? 0) != expectedVersion) throw new InvalidOperationException("This publication changed. Reload it before publishing.");
            if (previous?.Active != true)
            {
                using var count = PublicationCommand(connection, transaction, "SELECT COUNT(*) FROM programme_publication_heads WHERE active=1");
                if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) >= ProgrammeCalendar.MaximumRules)
                    throw new InvalidOperationException("At most 256 programmes can be published.");
            }
            return WritePublication(connection, transaction, new(normalized, expectedVersion + 1, true, actor, DateTimeOffset.UtcNow), previous, command, requestHash);
        });
    }

    public Task<ProgrammePublication> UnpublishProgrammeAsync(string id, long expectedVersion, string commandId,
        string actor, IReadOnlySet<string> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);
        var key = ProgrammeId(id); var command = ProgrammeCommandId(commandId); RequireProgrammeActor(actor);
        var known = stations.ToHashSet(StringComparer.Ordinal);
        if (expectedVersion is < 1 or long.MaxValue) throw new ArgumentException("Review the current publication version.");
        var requestHash = PublicationHash(JsonSerializer.Serialize(new { Action = "unpublish", Id = key, ExpectedVersion = expectedVersion, Actor = actor }));
        return TransactionAsync((connection, transaction) =>
        {
            if (PublicationReceipt(connection, transaction, command, requestHash, known) is { } receipt) return receipt;
            var previous = PublicationHead(connection, transaction, key, known) ?? throw new InvalidOperationException("This publication is unavailable.");
            if (previous.Version != expectedVersion || !previous.Active) throw new InvalidOperationException("This publication changed or is already inactive.");
            return WritePublication(connection, transaction, previous with { Version = expectedVersion + 1, Active = false, Actor = actor, AtUtc = DateTimeOffset.UtcNow }, previous, command, requestHash);
        });
    }

    public Task<IReadOnlyList<ProgrammePublication>> ActiveProgrammePublicationsAsync(IReadOnlySet<string> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);
        var known = stations.ToHashSet(StringComparer.Ordinal);
        return TransactionAsync<IReadOnlyList<ProgrammePublication>>((connection, transaction) =>
        {
            using var command = PublicationCommand(connection, transaction, "SELECT id,version FROM programme_publication_heads WHERE active=1 ORDER BY id LIMIT 257");
            var keys = new List<(string Id, long Version)>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) keys.Add((reader.GetString(0), reader.GetInt64(1)));
            if (keys.Count > ProgrammeCalendar.MaximumRules) throw new InvalidDataException("Too many active programme publications.");
            var values = keys.Select(key => ReadPublication(connection, transaction, key.Id, key.Version, known)).ToArray();
            if (values.Any(value => !value.Active)) throw new InvalidDataException("An active publication head points to an inactive revision.");
            return values;
        });
    }

    public Task<ProgrammePublication?> ProgrammePublicationAsync(string id, IReadOnlySet<string> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);
        var key = ProgrammeId(id); var known = stations.ToHashSet(StringComparer.Ordinal);
        return TransactionAsync((connection, transaction) => PublicationHead(connection, transaction, key, known));
    }

    private static ProgrammePublication WritePublication(SqliteConnection c, SqliteTransaction t, ProgrammePublication value,
        ProgrammePublication? previous, string commandId, string requestHash)
    {
        var previousHash = "";
        if (previous is not null)
        {
            using var query = PublicationCommand(c, t, "SELECT entry_hash FROM programme_publication_versions WHERE id=$id AND version=$version", ("$id", value.Id), ("$version", previous.Version));
            previousHash = query.ExecuteScalar() as string ?? throw new InvalidDataException("The previous publication revision is missing.");
        }
        var payload = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(payload) > 4 * 1024 * 1024) throw new ArgumentException("The prepared programme is too large to publish.");
        var hash = ProgrammeDraftHash(previousHash, payload);
        using (var add = PublicationCommand(c, t, "INSERT INTO programme_publication_versions(id,version,payload,previous_hash,entry_hash) VALUES($id,$version,$payload,$previous,$hash)",
            ("$id", value.Id), ("$version", value.Version), ("$payload", payload), ("$previous", previousHash), ("$hash", hash))) add.ExecuteNonQuery();
        using (var head = PublicationCommand(c, t, "INSERT INTO programme_publication_heads(id,version,active) VALUES($id,$version,$active) ON CONFLICT(id) DO UPDATE SET version=excluded.version,active=excluded.active",
            ("$id", value.Id), ("$version", value.Version), ("$active", value.Active ? 1 : 0))) head.ExecuteNonQuery();
        using var receipt = PublicationCommand(c, t, "INSERT INTO programme_publication_commands(id,request_hash,publication_id,version) VALUES($command,$request,$id,$version)",
            ("$command", commandId), ("$request", requestHash), ("$id", value.Id), ("$version", value.Version));
        receipt.ExecuteNonQuery(); return value;
    }

    private static ProgrammePublication? PublicationHead(SqliteConnection c, SqliteTransaction t, string id, IReadOnlySet<string> known)
    {
        using var query = PublicationCommand(c, t, "SELECT version,active FROM programme_publication_heads WHERE id=$id", ("$id", id));
        long version; bool active;
        using (var reader = query.ExecuteReader()) { if (!reader.Read()) return null; version = reader.GetInt64(0); active = reader.GetInt64(1) != 0; }
        var value = ReadPublication(c, t, id, version, known);
        if (value.Active != active) throw new InvalidDataException("The publication head does not match its revision.");
        return value;
    }

    private static ProgrammePublication ReadPublication(SqliteConnection c, SqliteTransaction t, string id, long version, IReadOnlySet<string> known)
    {
        using var query = PublicationCommand(c, t, "SELECT payload,previous_hash,entry_hash FROM programme_publication_versions WHERE id=$id AND version=$version", ("$id", id), ("$version", version));
        using var reader = query.ExecuteReader(); if (!reader.Read()) throw new InvalidDataException("The programme publication revision is missing.");
        var payload = reader.GetString(0);
        if (Encoding.UTF8.GetByteCount(payload) > 4 * 1024 * 1024 || ProgrammeDraftHash(reader.GetString(1), payload) != reader.GetString(2))
            throw new InvalidDataException("The programme publication failed its integrity check.");
        var value = JsonSerializer.Deserialize<ProgrammePublication>(payload) ?? throw new InvalidDataException("The programme publication is empty.");
        _ = ProgrammePublicationValidation.Normalize(value.Programme, known, checkFiles: false);
        if (value.Id != id || value.Version != version || value.AtUtc == default) throw new InvalidDataException("The programme publication identity is invalid.");
        RequireProgrammeActor(value.Actor); return value;
    }

    private static ProgrammePublication? PublicationReceipt(SqliteConnection c, SqliteTransaction t, string commandId, string requestHash, IReadOnlySet<string> known)
    {
        using var query = PublicationCommand(c, t, "SELECT request_hash,publication_id,version FROM programme_publication_commands WHERE id=$id", ("$id", commandId));
        string id; long version;
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) return null;
            if (reader.GetString(0) != requestHash) throw new InvalidOperationException("This command ID belongs to a different publication change.");
            id = reader.GetString(1); version = reader.GetInt64(2);
        }
        return ReadPublication(c, t, id, version, known);
    }

    private static string PublicationHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static SqliteCommand PublicationCommand(SqliteConnection c, SqliteTransaction t, string sql, params (string Key, object Value)[] values)
    {
        var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value);
        return command;
    }
}
