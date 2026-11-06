using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammeMusicOrderTests
{
    [Fact]
    public void ShuffledCyclesContainEveryTrackAndCanBeReproducedAfterRestart()
    {
        var tracks = Enumerable.Range(0, 34).Select(i => new MediaItem($"song-{i:D2}", $"song-{i:D2}.opus", "", $"Song {i}")).ToArray();
        var first = ProgrammeMusicOrder.PrepareCycle(tracks, ProgrammeOrder.Shuffle, "occurrence/4/nov10", 0);
        var replay = ProgrammeMusicOrder.PrepareCycle(tracks.Reverse(), ProgrammeOrder.Shuffle, "occurrence/4/nov10", 0);
        var next = ProgrammeMusicOrder.PrepareCycle(tracks, ProgrammeOrder.Shuffle, "occurrence/4/nov10", 1);
        Assert.Equal(tracks.Select(x => x.Id).Order(), first.Select(x => x.Id).Order());
        Assert.Equal(first.Select(x => x.Id), replay.Select(x => x.Id));
        Assert.NotEqual(first.Select(x => x.Id), next.Select(x => x.Id));
        Assert.Equal(tracks.Select(x => x.Id), ProgrammeMusicOrder.PrepareCycle(tracks, ProgrammeOrder.FolderOrder, "event", 0).Select(x => x.Id));
    }

    [Fact]
    public void InvalidOrDuplicateMediaCannotEnterThePublishedCycle()
    {
        var track = new MediaItem("one", "one.opus", "", "One");
        Assert.Throws<ArgumentException>(() => ProgrammeMusicOrder.PrepareCycle([], ProgrammeOrder.Shuffle, "event", 0));
        Assert.Throws<ArgumentException>(() => ProgrammeMusicOrder.PrepareCycle([track, track], ProgrammeOrder.Shuffle, "event", 0));
        Assert.Throws<ArgumentException>(() => ProgrammeMusicOrder.PrepareCycle([track with { Kind = ContentKind.Jingle }], ProgrammeOrder.Shuffle, "event", 0));
        Assert.Throws<ArgumentException>(() => ProgrammeMusicOrder.PrepareCycle([track], ProgrammeOrder.Shuffle, "event", -1));
    }
}
