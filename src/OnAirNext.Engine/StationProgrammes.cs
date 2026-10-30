namespace OnAirNext.Engine;

// Recorded material shares the library's single bounded worker, but never a
// station's automatic music rotation. Queue ownership remains with the channel.
public static class StationProgrammes
{
    public static string InventoryKey(string station)
    {
        if(string.IsNullOrWhiteSpace(station)||station.Length>64||
            station.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '-' and not '_'))
            throw new ArgumentException("Choose a configured program channel.");
        return "programmes-"+station;
    }
    public static IReadOnlyDictionary<string,ContentKind> InventoryKinds(IEnumerable<string> stations)
    {
        var ids=stations.ToArray();
        if(ids.Length>8||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length)
            throw new ArgumentException("Program libraries belong to the eight distinct configured channels.");
        var result=new Dictionary<string,ContentKind>(StringComparer.Ordinal);
        foreach(var station in ids)
        {
            var key=InventoryKey(station);
            if(ids.Contains(key,StringComparer.Ordinal)||!result.TryAdd(key,ContentKind.Program))
                throw new ArgumentException("Program libraries must be separate from broadcast channels.");
        }
        return result;
    }
    public static string? ResolveQueueInventory(string station,QueueLibraryReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.InventoryId is null?station:source.InventoryId==InventoryKey(station)?source.InventoryId:null;
    }

    public static async Task InitializeAsync(EventJournal journal,IReadOnlyCollection<string> stations)
    {
        _=InventoryKinds(stations);
        await journal.InitializeLibraryAsync();await journal.RegisterProgrammeInventoriesAsync(stations);
    }
}
