using System.Diagnostics;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static partial class ProcessingChecks
{
    private static async Task CrossfadeLoopbackChecksAsync(string ffmpeg,string icecastRoot,string root,CaseResult result)
    {
        var folder=Path.Combine(root,"crossfade-loopback");Directory.CreateDirectory(folder);
        var first=Fixture(folder,"first",8*48000,.1f,ContentKind.Music,440);
        var second=Fixture(folder,"second",8*48000,.1f,ContentKind.Music,660);
        await using(var analysis=new LibraryAudioProbe(ffmpeg))
        {
            var a=await analysis.VerifyAsync(first.Path,CancellationToken.None);var b=await analysis.VerifyAsync(second.Path,CancellationToken.None);
            first=first with{Loudness=a.Loudness,DurationSeconds=a.Seconds};second=second with{Loudness=b.Loudness,DurationSeconds=b.Seconds};
        }
        await using var origin=new Sandbox(icecastRoot,folder);await origin.StartAsync();
        await using var journal=new EventJournal(Path.Combine(folder,"playback.db"));
        await using var station=new StationEngine(ffmpeg,new("crossfade",[first,second],
            [origin.Output("crossfade-main"),origin.Output("crossfade-low")],Repeat:true,CrossfadeSeconds:3),journal);
        await station.StartAsync();var wait=Stopwatch.StartNew();
        while(!station.Snapshot().Outputs.All(x=>x.State=="SourceConnected"))
        {
            if(wait.Elapsed>TimeSpan.FromSeconds(12))throw new TimeoutException("Crossfade sources did not connect.");
            await Task.Delay(50);
        }
        await using var main=new Probe(ffmpeg,origin.Port,"/next-crossfade-main");
        await using var low=new Probe(ffmpeg,origin.Port,"/next-crossfade-low");
        while(station.Snapshot().CompletedItems<1||station.Snapshot().CurrentAssetId!=second.Id)
        {
            if(wait.Elapsed>TimeSpan.FromSeconds(20))throw new TimeoutException("The measured crossfade did not complete.");
            await Task.Delay(50);
        }
        var metadataWait=Stopwatch.StartNew();
        while(!station.Snapshot().Outputs.All(x=>x.Metadata?.PublishedTitle==second.Artist+" - "+second.Title))
        {
            if(metadataWait.Elapsed>TimeSpan.FromSeconds(1))throw new TimeoutException("Both listeners did not receive the incoming song metadata.");
            await Task.Delay(50);
        }
        Check(result,station.Snapshot().Outputs.All(x=>x.Metadata?.PublishedTitle==second.Artist+" - "+second.Title),
            "Both outputs publish the incoming song metadata at the transition.");
        await Task.Delay(TimeSpan.FromSeconds(3));
        var state=station.Snapshot();var listeners=new[]{main.Snapshot(),low.Snapshot()};
        var events=EventJournal.ReadEvents(journal.DatabasePath);
        var incoming=events.First(e=>e.Type=="started"&&e.ItemId==second.Id);
        var outgoing=events.First(e=>e.Type=="ended"&&e.ItemId==first.Id);
        Check(result,outgoing.SamplePosition-incoming.SamplePosition==3L*48000,"Measured music overlaps for exactly three seconds in the real PCM engine.");
        Check(result,state.Outputs.All(x=>x.State=="SourceConnected"&&x.EncoderStarts==1&&x.Connections==1&&x.DroppedBlocks==0),
            "Both encoded sources remain connected through the music overlap without dropped PCM blocks.");
        Check(result,listeners.All(x=>x.Error is null&&x.DecodedSeconds>=4&&x.LongestSilentRun==0&&x.FirstTones.Contains(440)&&x.FirstTones.Contains(660)),
            "Both listeners decode the two tones through the overlap without a silent 20 ms window.");
        Check(result,EventJournal.Verify(journal.DatabasePath),"The encoded overlap preserves the journal chain.");
        result.Details["crossfadeLoopback"]=new{IncomingStart=incoming.SamplePosition,OutgoingEnd=outgoing.SamplePosition,state.Outputs,Listeners=listeners};
    }
}
