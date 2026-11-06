namespace OnAirNext.Engine;

public sealed record CrossfadeConfiguration(string StationId,long Version,double Seconds);
public sealed record CrossfadeProfile(CrossfadeConfiguration Configuration,EqualPowerCrossfade? Curve);
public interface ICrossfadeSource
{
    CrossfadeProfile Snapshot();
    event Action? Changed;
}

internal sealed class CrossfadeSource(CrossfadeProfile initial):ICrossfadeSource
{
    private CrossfadeProfile _current=initial;
    public event Action? Changed;
    public CrossfadeProfile Snapshot()=>Volatile.Read(ref _current);
    public void Publish(CrossfadeProfile next){Volatile.Write(ref _current,next);Changed?.Invoke();}
}

// Curves are constructed on the control lane before a durable setting is
// published to an active station; a completed save never reopens an output.
public sealed class StationCrossfades
{
    private readonly EventJournal _journal;
    private readonly Dictionary<string,StationSettings> _stations;
    private readonly Dictionary<string,CrossfadeSource> _sources=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changes=new(1,1);
    private bool _initialized;

    public StationCrossfades(EventJournal journal,IEnumerable<StationSettings> stations)
    {
        _journal=journal;_stations=stations.ToDictionary(s=>s.Id,StringComparer.Ordinal);
        if(_stations.Count is <1 or >8||_stations.Keys.Any(string.IsNullOrWhiteSpace))throw new ArgumentException("Choose the installed stations.");
    }
    private static CrossfadeProfile Prepare(CrossfadeConfiguration value)
    {
        EventJournal.ValidateCrossfadeSeconds(value.Seconds);
        return new(value,value.Seconds==0?null:new EqualPowerCrossfade(checked((int)Math.Round(value.Seconds*AudioFormat.SampleRate,MidpointRounding.AwayFromZero))));
    }
    public async Task InitializeAsync()
    {
        if(_initialized)throw new InvalidOperationException("Crossfade settings are already initialized.");
        foreach(var station in _stations.Values)
        {
            var saved=await _journal.InitializeCrossfadeAsync(station.Id,station.CrossfadeSeconds);
            _sources.Add(station.Id,new(Prepare(saved)));
        }
        _initialized=true;
    }
    private CrossfadeSource Require(string station)
        =>_initialized&&_sources.TryGetValue(station,out var source)?source:throw new ArgumentException("Choose an initialized station.");
    public ICrossfadeSource Source(string station)=>Require(station);
    public CrossfadeConfiguration Snapshot(string station)=>Require(station).Snapshot().Configuration;
    public async Task<CrossfadeConfiguration> SaveAsync(string station,long expectedVersion,double seconds,string command)
    {
        var source=Require(station);EventJournal.ValidateCrossfadeSeconds(seconds);
        if(expectedVersion is <0 or long.MaxValue)throw new ArgumentException("Review the current crossfade version.");
        // Trigonometric table construction never runs on the station PCM lane.
        var prepared=await Task.Run(()=>Prepare(new(station,expectedVersion+1,seconds)));
        await _changes.WaitAsync();
        try
        {
            var saved=await _journal.SaveCrossfadeAsync(station,expectedVersion,seconds,command);
            if(saved.Version>source.Snapshot().Configuration.Version)source.Publish(prepared with {Configuration=saved});
            return saved;
        }
        finally{_changes.Release();}
    }
}
