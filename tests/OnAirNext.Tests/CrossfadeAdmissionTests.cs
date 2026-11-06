using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class CrossfadeAdmissionTests
{
    private static MediaItem Item(string id, ContentKind kind = ContentKind.Music, int seconds = 30)
    {
        var frames = (long)seconds * AudioFormat.SampleRate;
        var measured = new SourceLoudness(new string('A', 64), 123, 1, seconds, -23, -3, 4,
            DateTimeOffset.UtcNow, PlaybackRange: new SourcePlaybackRange(0, frames, frames));
        return new(id, id + ".wav", "Artist", "Song", kind, seconds, measured);
    }

    [Fact]
    public void FadeRequiresTwoDistinctMeasuredMusicTracksAndACompleteIncomingWindow()
    {
        var current = Item("one"); var next = Item("two");
        var window = CrossfadeAdmission.Plan(current, next, 3);
        Assert.NotNull(window); Assert.Equal(27L * AudioFormat.SampleRate, window.StartFrame);
        Assert.Equal(3 * AudioFormat.SampleRate, window.Frames);
        var ready = new PcmPrefetchSnapshot(160, window.Frames, false, null);
        Assert.True(CrossfadeAdmission.CanStart(window, window.StartFrame, ready));
        Assert.False(CrossfadeAdmission.CanStart(window, window.StartFrame, ready with { Frames = window.Frames - 1 }));
        Assert.False(CrossfadeAdmission.CanStart(window, window.StartFrame, ready with { Error = "IOException" }));
        Assert.False(CrossfadeAdmission.CanStart(window, window.StartFrame + 1, ready));
        Assert.False(CrossfadeAdmission.CanStart(window, window.StartFrame, ready, window.Frames - 1));
    }

    [Fact]
    public void DisabledClassicBrandingMissingAnalysisAndShortTracksKeepOrdinaryPlayout()
    {
        var current = Item("one"); var next = Item("two");
        Assert.Null(CrossfadeAdmission.Plan(current, next, 0));
        Assert.Null(CrossfadeAdmission.Plan(current, Item("jingle", ContentKind.Jingle), 3));
        Assert.Null(CrossfadeAdmission.Plan(current, current, 3));
        Assert.Null(CrossfadeAdmission.Plan(current, Item("short", seconds: 5), 3));
        Assert.Null(CrossfadeAdmission.Plan(current, next with { Loudness = null }, 3));
    }
}
