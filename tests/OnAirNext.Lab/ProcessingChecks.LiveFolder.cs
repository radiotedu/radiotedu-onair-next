using System.Diagnostics;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

internal static partial class ProcessingChecks
{
    // Synthetic media and database remain inside the disposable lab run.
    // Exercise the real probe and normal 30-second polling, not Wake or Rescan.
    private static async Task LiveFolderChecksAsync(string ffmpeg,string root,CaseResult result)
    {
        var fixtureRoot=Path.Combine(root,"live-folder");
        var music=Path.Combine(fixtureRoot,"music");Directory.CreateDirectory(music);
        var first=Fixture(music,"First",48000*4,0.08f);
        await using var journal=new EventJournal(Path.Combine(fixtureRoot,"catalog.db"));
        var settings=new StationSettings("live-folder-fixture",[],[],AutoStart:false);
        await journal.InitializeDesiredStateAsync(settings);
        await using var library=new StationLibrary(journal,ffmpeg,fixtureRoot,[settings.Id]);
        await library.InitializeAsync();await library.SetRootAsync(settings.Id,music,"fixture-folder");
        var planner=library.Planner(settings)!;var clock=Stopwatch.StartNew();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(120));
        while(planner.AvailableItems!=1)await Task.Delay(200,deadline.Token);
        var firstReady=clock.Elapsed.TotalSeconds;
        var added=Fixture(music,"Added",48000*4,0.08f,frequency:660);
        while(planner.AvailableItems!=2)await Task.Delay(200,deadline.Token);
        var addedReady=clock.Elapsed.TotalSeconds-firstReady;
        var selected=Enumerable.Range(0,2).Select(i=>planner.Select(i).Item).ToArray();
        Check(result,selected.Select(x=>x.Title).Order(StringComparer.Ordinal).SequenceEqual(new[]{"Added","First"}),
            "A new real audio file joins the ordinary rotation without import, Wake, or rescan.");
        Check(result,selected.All(x=>x.Loudness?.PlaybackRange is not null),
            "Both files pass real decode, loudness and playback-range validation before publication.");
        Check(result,File.Exists(first.Path)&&File.Exists(added.Path),"Live folder scanning leaves original media intact.");
        result.Details["liveFolder"]=new{FirstReadySeconds=firstReady,AddedReadySeconds=addedReady,Ready=selected.Length};
    }
}
