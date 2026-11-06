using System.Diagnostics;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static partial class ProcessingChecks
{
    private sealed class ProgrammeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static async Task ProgrammeChecksAsync(string ffmpeg,string root,CaseResult result)
    {
        var folder=Path.Combine(root,"programme");var specialFolder=Path.Combine(folder,"special");
        Directory.CreateDirectory(folder);Directory.CreateDirectory(specialFolder);
        var normal=Fixture(folder,"normal",12*48000,.1f);
        _=Fixture(specialFolder,"special",12*48000,.2f);
        var known=new HashSet<string>(["4"],StringComparer.Ordinal);var day=new DateOnly(2026,11,10);
        var draft=new ProgrammeDraft(new(Guid.NewGuid().ToString("N"),1,"Remembrance",["4"],CalendarRuleKind.SpecialDay,
            day,day,TimeOnly.MinValue,TimeOnly.MinValue,AllDay:true,StartPolicy:CalendarStartPolicy.FadeAtTime,
            ReturnPolicy:CalendarReturnPolicy.FadeAtTime),specialFolder,DisplayText:"Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz",SuppressStationBranding:true);
        await using var journal=new EventJournal(Path.Combine(folder,"playback.db"));
        await journal.InitializeDesiredStateAsync(new("4",[],[],AutoStart:false));
        await journal.InitializeProgrammeDraftsAsync();await journal.InitializeProgrammePublicationsAsync();await journal.InitializeProgrammePlaybackAsync();
        await journal.SaveProgrammeDraftAsync(draft,0,Guid.NewGuid().ToString("N"),"fixture",known);
        await using var preparation=new ProgrammeMediaPreparation(ffmpeg);
        var prepared=await preparation.PrepareAsync(draft,known);
        await journal.PublishPreparedProgrammeAsync(prepared,0,Guid.NewGuid().ToString("N"),"fixture",known);
        var clock=new ProgrammeClock(new DateTimeOffset(2026,11,9,23,59,58,TimeSpan.FromHours(3)));
        await using var programmes=new ProgrammeRuntimeCoordinator(journal,known,clock);await programmes.RefreshAsync();
        Check(result,programmes.Snapshot().Healthy,"The isolated special-day programme is loaded before its deadline.");
        var sink=new Capture(journal.DatabasePath,inspectHistory:false);
        await using(var station=new StationEngine(ffmpeg,new("4",[normal],[],Repeat:true),journal,[sink],programmes:programmes,clock:clock))
        {
            await station.StartAsync();var wait=Stopwatch.StartNew();var special=prepared.Items[0];
            while(station.Snapshot().CurrentAssetId!=special.Id)
            {
                if(wait.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("Scheduled PCM did not begin in the fixture.");
                await Task.Delay(20);
            }
            await Task.Delay(350);
        }
        var events=EventJournal.ReadEvents(journal.DatabasePath);
        var started=events.Single(e=>e.Type=="started"&&e.ProgrammeMusicKey is not null);
        Check(result,Math.Abs(started.SamplePosition-2L*48000)<=960,"The real decoded special source starts within one PCM block of its sample-clock deadline.");
        Check(result,events.Any(e=>e.Type=="interrupted"&&e.ItemId==normal.Id)&&sink.Starts==1,
            "The normal source is interrupted without restarting the output.");
        var samples=sink.Samples;var start=Math.Max(0,(int)started.SamplePosition-24000);var end=Math.Min((int)sink.Frames,(int)started.SamplePosition+12000);
        var quiet=false;
        for(var frame=start;frame+960<=end;frame+=960)
        {
            double energy=0;for(var i=0;i<960;i++){var value=samples[(frame+i)*2];energy+=value*value;}
            if(Math.Sqrt(energy/960)<.001)quiet=true;
        }
        Check(result,!quiet,"The transition contains no silent 20 ms PCM block in the measured window.");
        Check(result,EventJournal.Verify(journal.DatabasePath),"The timed PCM transition preserves the playback journal chain.");
        result.Details["timedProgramme"]=new{StartFrame=started.SamplePosition,OutputFrames=sink.Frames,OutputStarts=sink.Starts,Quiet20ms=quiet};
    }
}
