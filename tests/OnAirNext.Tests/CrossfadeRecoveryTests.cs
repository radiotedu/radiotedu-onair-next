using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class CrossfadeRecoveryTests
{
    private static string Database()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU","OnAirNext-Dev","crossfade-recovery-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);return Path.Combine(root,"playback.db");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryClosesAnUnfinishedOutgoingTailOnceWithoutInventingCompletion(bool outgoingFinished)
    {
        var db=Database();var outgoing=Guid.NewGuid().ToString("N");var incoming=Guid.NewGuid().ToString("N");
        var songA=new MediaItem("a","a.wav","A","A");var songB=new MediaItem("b","b.wav","B","B");
        var now=DateTimeOffset.UtcNow;
        PlaybackEvent Event(string occurrence,string type,MediaItem item,long at)=>new(Guid.NewGuid().ToString("N"),occurrence,
            "station",type,item.Id,item.Artist,item.Title,item.Kind,now.AddSeconds((double)at/48000),at);
        PlayoutCheckpoint Check(MediaItem item,string occurrence,long at)=>new("station","Playing",0,item,occurrence,
            at,at,true,now.AddSeconds((double)at/48000),SessionId:"session");
        await using(var journal=new EventJournal(db))
        {
            await journal.AppendAsync(Event(outgoing,"started",songA,0),Check(songA,outgoing,0));
            await journal.AppendAsync(Event(incoming,"started",songB,48000) with {OverlapWithOccurrenceId=outgoing},
                Check(songB,incoming,48000));
            if(outgoingFinished)await journal.AppendAsync(Event(outgoing,"ended",songA,96000),Check(songB,incoming,96000));
            var first=await journal.PrepareRecoveryAsync("station");var second=await journal.PrepareRecoveryAsync("station");
            Assert.Equal(first,second);Assert.Equal("RestartPending",first!.State);Assert.Equal(incoming,first.OccurrenceId);
        }
        var events=EventJournal.ReadEvents(db);
        Assert.Single(events,e=>e.Type=="interrupted"&&e.OccurrenceId==incoming);
        Assert.Equal(outgoingFinished?0:1,events.Count(e=>e.Type=="interrupted"&&e.OccurrenceId==outgoing));
        Assert.Equal(outgoingFinished?1:0,events.Count(e=>e.Type=="ended"&&e.OccurrenceId==outgoing));
        Assert.True(EventJournal.Verify(db));
    }
}
