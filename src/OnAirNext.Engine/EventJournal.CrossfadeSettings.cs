using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed partial class EventJournal
{
    internal static void ValidateCrossfadeSeconds(double seconds)
    {
        if(!double.IsFinite(seconds)||seconds<0||seconds>15||seconds>0&&seconds<.5)
            throw new ArgumentException("Choose no crossfade or a duration between 0.5 and 15 seconds.");
    }
    public Task<CrossfadeConfiguration> InitializeCrossfadeAsync(string station,double initialSeconds)
    {
        if(string.IsNullOrWhiteSpace(station)||station.Length>64||station.Any(char.IsControl))throw new ArgumentException("Choose a station.");
        ValidateCrossfadeSeconds(initialSeconds);
        return TransactionAsync((c,t)=>
        {
            using var create=PublicationCommand(c,t,"""
                CREATE TABLE IF NOT EXISTS station_crossfade(station_id TEXT PRIMARY KEY,version INTEGER NOT NULL,seconds REAL NOT NULL);
                CREATE TABLE IF NOT EXISTS station_crossfade_commands(id TEXT PRIMARY KEY,station_id TEXT NOT NULL,expected_version INTEGER NOT NULL,seconds REAL NOT NULL,result_version INTEGER NOT NULL);
                INSERT OR IGNORE INTO station_crossfade(station_id,version,seconds) VALUES($station,0,$seconds);
                """,("$station",station),("$seconds",initialSeconds));
            create.ExecuteNonQuery();return ReadCrossfade(c,t,station);
        });
    }
    public Task<CrossfadeConfiguration> SaveCrossfadeAsync(string station,long expectedVersion,double seconds,string command)
    {
        if(string.IsNullOrWhiteSpace(station)||station.Length>64||station.Any(char.IsControl)||expectedVersion is <0 or long.MaxValue||
            !Guid.TryParse(command,out var parsed))throw new ArgumentException("Review the station and current crossfade version.");
        ValidateCrossfadeSeconds(seconds);var key=parsed.ToString("N");
        return TransactionAsync((c,t)=>
        {
            using(var prior=PublicationCommand(c,t,"SELECT station_id,expected_version,seconds,result_version FROM station_crossfade_commands WHERE id=$id",("$id",key)))
            using(var reader=prior.ExecuteReader())if(reader.Read())
            {
                if(reader.GetString(0)!=station||reader.GetInt64(1)!=expectedVersion||reader.GetDouble(2)!=seconds)
                    throw new InvalidOperationException("This command ID belongs to a different crossfade change.");
                return new(station,reader.GetInt64(3),seconds);
            }
            var current=ReadCrossfade(c,t,station);
            if(current.Version!=expectedVersion)throw new InvalidOperationException("Crossfade settings changed. Reload before saving.");
            using(var update=PublicationCommand(c,t,"UPDATE station_crossfade SET version=$version,seconds=$seconds WHERE station_id=$station AND version=$expected",
                ("$version",expectedVersion+1),("$seconds",seconds),("$station",station),("$expected",expectedVersion)))
                if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("Crossfade settings changed. Reload before saving.");
            using var receipt=PublicationCommand(c,t,"INSERT INTO station_crossfade_commands(id,station_id,expected_version,seconds,result_version) VALUES($id,$station,$expected,$seconds,$result)",
                ("$id",key),("$station",station),("$expected",expectedVersion),("$seconds",seconds),("$result",expectedVersion+1));
            receipt.ExecuteNonQuery();return new(station,expectedVersion+1,seconds);
        });
    }
    private static CrossfadeConfiguration ReadCrossfade(SqliteConnection c,SqliteTransaction t,string station)
    {
        using var query=PublicationCommand(c,t,"SELECT version,seconds FROM station_crossfade WHERE station_id=$station",("$station",station));
        using var reader=query.ExecuteReader();if(!reader.Read())throw new InvalidOperationException("The station crossfade configuration is unavailable.");
        var version=reader.GetInt64(0);var seconds=reader.GetDouble(1);ValidateCrossfadeSeconds(seconds);
        if(version<0)throw new InvalidDataException("The station crossfade revision is invalid.");
        return new(station,version,seconds);
    }
}
