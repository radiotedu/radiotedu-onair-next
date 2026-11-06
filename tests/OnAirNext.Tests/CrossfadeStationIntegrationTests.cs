using System.Runtime.InteropServices;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

[CollectionDefinition("Crossfade realtime playback",DisableParallelization=true)]
public sealed class CrossfadeRealtimeCollection { }

[Collection("Crossfade realtime playback")]
public sealed class CrossfadeStationIntegrationTests
{
    private sealed class Decoder(float sample,Task? gate=null,int blocks=200):IFrameDecoder
    {
        private int _remaining=blocks;
        public Task Ready=>Task.CompletedTask;
        public async ValueTask<PcmBlock?> ReadAsync(CancellationToken token)
        {
            if(gate is not null)await gate.WaitAsync(token);
            if(_remaining--<=0)return null;
            var block=PcmBlock.Rent();MemoryMarshal.Cast<byte,float>(block.Memory.Span).Fill(sample);return block;
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class Output:IProgramOutput
    {
        private int _starts;private long _frames;
        public int Starts=>Volatile.Read(ref _starts);public long Frames=>Interlocked.Read(ref _frames);
        public readonly List<float> Left=[];
        public void Start()=>Interlocked.Increment(ref _starts);
        public bool Publish(PcmBlock block)
        {
            var samples=MemoryMarshal.Cast<byte,float>(block.Memory.Span[..block.Length]);
            for(var i=0;i<samples.Length;i+=2)Left.Add(samples[i]);
            Interlocked.Add(ref _frames,samples.Length/2);return true;
        }
        public void SetNowPlaying(string artist,string title){}
        public OutputSnapshot Snapshot()=>new("fixture","SourceConnected",1,1,Frames/960,0,0,null,null);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static string Database()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU","OnAirNext-Dev","crossfade-station-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);return Path.Combine(root,"playback.db");
    }
    private static MediaItem Song(string id)
    {
        var measurement=new SourceLoudness(new string('A',64),1,1,4,-23,-3,4,DateTimeOffset.UtcNow,
            PlaybackRange:new(0,192000,192000));
        return new(id,id+".wav",id,id,DurationSeconds:4,Loudness:measurement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedMusicOverlapsWhileAnUnavailablePrefetchContinuesSequentially(bool delayIncoming)
    {
        var db=Database();await using var journal=new EventJournal(db);var output=new Output();var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var songs=new[]{Song("a"),Song("b")};
        await using var station=new StationEngine("unused",new("station",songs,[],Repeat:false,CrossfadeSeconds:.5),journal,[output],null,null,
            item=>new Decoder(item.Id=="a"?.1f:.2f,item.Id=="b"&&delayIncoming?gate.Task:null),()=>Task.CompletedTask);
        await station.StartAsync();
        if(delayIncoming)
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while(output.Frames<180000)await Task.Delay(20,timeout.Token);
            gate.TrySetResult();
        }
        await station.Completion.WaitAsync(TimeSpan.FromSeconds(18));
        var events=EventJournal.ReadEvents(db);var starts=events.Where(x=>x.Type=="started").ToArray();var ends=events.Where(x=>x.Type=="ended").ToArray();
        Assert.Equal(2,starts.Length);Assert.Equal(2,ends.Length);Assert.Equal(1,output.Starts);
        Assert.Equal(delayIncoming?192000:168000,starts[1].SamplePosition);
        Assert.Equal(192000,ends[0].SamplePosition);
        Assert.Equal(delayIncoming?384000:360000,output.Frames);
        Assert.True(output.Left.All(x=>x>.05f));
        if(!delayIncoming)Assert.InRange(output.Left[180000],.20f,.23f);
        Assert.True(EventJournal.Verify(db));
    }

    [Fact]
    public async Task AnOutgoingFileThatEndsEarlyKeepsThePreparedIncomingMusicOnAir()
    {
        var db=Database();await using var journal=new EventJournal(db);var output=new Output();var songs=new[]{Song("a"),Song("b")};
        await using var station=new StationEngine("unused",new("station",songs,[],Repeat:false,CrossfadeSeconds:.5),journal,[output],null,null,
            item=>new Decoder(item.Id=="a"?.1f:.2f,blocks:item.Id=="a"?185:200),()=>Task.CompletedTask);
        await station.StartAsync();await station.Completion.WaitAsync(TimeSpan.FromSeconds(18));
        var events=EventJournal.ReadEvents(db);
        Assert.Single(events,e=>e.ItemId=="a"&&e.Type=="failed");
        Assert.Single(events,e=>e.ItemId=="b"&&e.Type=="ended");
        Assert.Equal(360000,output.Frames);Assert.Equal(1,output.Starts);
        Assert.True(output.Left.All(x=>x>.05f));Assert.True(EventJournal.Verify(db));
    }

    [Fact]
    public async Task SavingATransitionDuringPlaybackAppliesToTheNextSongWithoutReopeningOutput()
    {
        var db=Database();await using var journal=new EventJournal(db);var output=new Output();var songs=new[]{Song("a"),Song("b")};
        var spec=new StationSettings("station",songs,[],Repeat:false);var profiles=new StationCrossfades(journal,[spec]);await profiles.InitializeAsync();
        await using var station=new StationEngine("unused",spec,journal,[output],null,null,
            item=>new Decoder(item.Id=="a"?.1f:.2f),()=>Task.CompletedTask,crossfade:profiles.Source("station"));
        await station.StartAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while(output.Frames<48000)await Task.Delay(20,timeout.Token);
        var saved=await profiles.SaveAsync("station",0,.5,Guid.NewGuid().ToString("N"));Assert.Equal(1,saved.Version);
        await station.Completion.WaitAsync(TimeSpan.FromSeconds(18));
        var starts=EventJournal.ReadEvents(db).Where(e=>e.Type=="started").ToArray();
        Assert.Equal(2,starts.Length);Assert.Equal(168000,starts[1].SamplePosition);
        Assert.Equal(1,output.Starts);Assert.Equal(360000,output.Frames);Assert.True(output.Left.All(x=>x>.05f));
    }
}
