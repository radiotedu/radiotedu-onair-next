using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class StationCrossfadesTests
{
    [Fact]
    public async Task OperatorSettingIsVersionedIdempotentAndSurvivesReopen()
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU","OnAirNext-Dev","crossfade-config-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);var db=Path.Combine(root,"playback.db");
        var stations=new[]{new StationSettings("classic",[new("a","a.wav","A","A")],[]),
            new StationSettings("rock",[new("b","b.wav","B","B")],[])};
        var command=Guid.NewGuid().ToString("N");
        await using(var journal=new EventJournal(db))
        {
            var settings=new StationCrossfades(journal,stations);await settings.InitializeAsync();
            var source=settings.Source("rock");var changes=0;source.Changed+=()=>changes++;
            var saved=await settings.SaveAsync("rock",0,3,command);
            Assert.Equal(1,saved.Version);Assert.Equal(3,saved.Seconds);Assert.Equal(1,changes);
            Assert.Equal(saved,await settings.SaveAsync("rock",0,3,command));Assert.Equal(1,changes);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>settings.SaveAsync("rock",0,2,Guid.NewGuid().ToString("N")));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>settings.SaveAsync("rock",0,2,command));
            await Assert.ThrowsAsync<ArgumentException>(()=>settings.SaveAsync("rock",1,.1,Guid.NewGuid().ToString("N")));
            Assert.Equal(0,settings.Snapshot("classic").Seconds);Assert.Equal(3,source.Snapshot().Configuration.Seconds);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.RequireRecoveryReaderAsync([]));
            await journal.RequireRecoveryReaderAsync([RecoveryCompatibility.CrossfadeFeature]);
        }
        await using(var reopened=new EventJournal(db))
        {
            var settings=new StationCrossfades(reopened,stations);await settings.InitializeAsync();
            Assert.Equal(3,settings.Snapshot("rock").Seconds);Assert.Equal(1,settings.Snapshot("rock").Version);
            Assert.Equal(0,settings.Snapshot("classic").Seconds);
        }
    }
}
