using Microsoft.Data.Sqlite;

namespace OnAirNext.Engine;

public sealed partial class EventJournal
{
    internal Task RegisterProgrammeInventoriesAsync(IReadOnlyCollection<string> stations)
        => TransactionAsync((connection,transaction)=>
    {
        foreach(var station in stations)
        {
            var inventory=StationProgrammes.InventoryKey(station);
            if(ReadDesired(connection,transaction,inventory) is not null)
                throw new InvalidDataException("A program library cannot also be a broadcast channel.");
            using(var existing=LibraryCommand(connection,transaction,"SELECT station_id,kind FROM library_inventory_roles WHERE inventory_id=$inventory",("$inventory",inventory)))
            using(var reader=existing.ExecuteReader())
                if(reader.Read()&&(reader.GetString(0)!=station||reader.GetString(1)!=ContentKind.Program.ToString()))
                    throw new InvalidDataException("The saved program library belongs to another channel or role.");
            using var add=LibraryCommand(connection,transaction,"INSERT OR IGNORE INTO library_inventory_roles(inventory_id,station_id,kind) VALUES($inventory,$station,'Program')",
                ("$inventory",inventory),("$station",station));add.ExecuteNonQuery();
        }
        return true;
    });

    internal Task RegisterBrandingInventoriesAsync(IReadOnlyCollection<string> stations)
    {
        if(stations.Count>8)throw new ArgumentException("Branding supports the eight configured programme channels.");
        var kinds=StationBranding.InventoryKinds(stations);
        return TransactionAsync((connection,transaction)=>
        {
            foreach(var station in stations)
                foreach(var kind in new[]{ContentKind.Jingle,ContentKind.Sweeper})
                {
                    var inventory=StationBranding.InventoryKey(station,kind);
                    using(var existing=LibraryCommand(connection,transaction,"SELECT station_id,kind FROM library_inventory_roles WHERE inventory_id=$inventory",("$inventory",inventory)))
                    using(var reader=existing.ExecuteReader())
                        if(reader.Read()&&(reader.GetString(0)!=station||reader.GetString(1)!=kind.ToString()))
                            throw new InvalidDataException("A saved branding inventory belongs to a different channel or role.");
                    using var add=LibraryCommand(connection,transaction,"INSERT OR IGNORE INTO library_inventory_roles(inventory_id,station_id,kind) VALUES($inventory,$station,$kind)",
                        ("$inventory",inventory),("$station",station),("$kind",kinds[inventory].ToString()));
                    add.ExecuteNonQuery();
                }
            return true;
        });
    }

    private static void RequireLibraryInventory(SqliteConnection connection,SqliteTransaction transaction,string inventory)
    {
        if(ReadDesired(connection,transaction,inventory) is not null)return;
        string? owner=null,role=null;
        using(var query=LibraryCommand(connection,transaction,"SELECT station_id,kind FROM library_inventory_roles WHERE inventory_id=$inventory",("$inventory",inventory)))
        using(var reader=query.ExecuteReader())if(reader.Read()){owner=reader.GetString(0);role=reader.GetString(1);}
        if(owner is not null&&Enum.TryParse<ContentKind>(role,out var kind)&&ReadDesired(connection,transaction,owner) is not null&&
            (kind==ContentKind.Program&&StationProgrammes.InventoryKey(owner)==inventory||
             (kind is ContentKind.Jingle or ContentKind.Sweeper)&&StationBranding.InventoryKey(owner,kind)==inventory))return;
        throw new ArgumentException("Unknown library inventory or programme channel.");
    }
}
