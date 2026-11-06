using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class EqualPowerCrossfadeTests
{
    [Fact]
    public void StereoTransitionHasExactEndpointsAndEqualPowerMidpoint()
    {
        var fade = new EqualPowerCrossfade(5);
        var outgoing = Enumerable.Repeat(1f, 10).ToArray();
        var incoming = Enumerable.Repeat(0f, 10).ToArray();
        var old = new float[10]; fade.Mix(outgoing, incoming, old, 0);
        var next = new float[10]; fade.Mix(incoming, outgoing, next, 0);
        Assert.Equal(1f, old[0]); Assert.Equal(0f, next[0]);
        Assert.Equal(0f, old[8]); Assert.Equal(1f, next[8]);
        Assert.InRange(old[4], 0.7070f, 0.7072f); Assert.InRange(next[4], 0.7070f, 0.7072f);
        for (var frame = 0; frame < 5; frame++)
        {
            Assert.Equal(old[frame * 2], old[frame * 2 + 1]);
            Assert.InRange(old[frame * 2] * old[frame * 2] + next[frame * 2] * next[frame * 2], 0.99999f, 1.00001f);
        }
    }

    [Fact]
    public void BlockedProcessingMatchesOnePassAndKeepsMaximumMusicInTheDecoderDomain()
    {
        var fade = new EqualPowerCrossfade(9); var old = Enumerable.Repeat(64f, 18).ToArray();
        var next = Enumerable.Repeat(64f, 18).ToArray(); var whole = new float[18];
        fade.Mix(old, next, whole, 0); var split = new float[18];
        fade.Mix(old.AsSpan(0, 8), next.AsSpan(0, 8), split.AsSpan(0, 8), 0);
        fade.Mix(old.AsSpan(8), next.AsSpan(8), split.AsSpan(8), 4);
        Assert.Equal(whole, split); Assert.All(whole, value => Assert.InRange(value, -64f, 64f));
        Assert.Throws<ArgumentException>(() => fade.Mix(old, next, split, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqualPowerCrossfade(1));
    }
}
