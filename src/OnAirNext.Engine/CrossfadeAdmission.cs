namespace OnAirNext.Engine;

internal sealed record CrossfadeWindow(long StartFrame, int Frames);

// Determine eligibility off the hot path. A station may overlap only validated
// music with enough prepared PCM; an unavailable window keeps ordinary playout.
internal static class CrossfadeAdmission
{
    public static CrossfadeWindow? Plan(MediaItem current, MediaItem next, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds is < .5 or > 15 || current.Kind != ContentKind.Music ||
            next.Kind != ContentKind.Music || current.Id == next.Id ||
            current.Loudness?.PlaybackRange is not { HasSignal: true } outgoing ||
            next.Loudness?.PlaybackRange is not { HasSignal: true } incoming) return null;
        try { outgoing.Validate(); incoming.Validate(); }
        catch (ArgumentException) { return null; }
        var frames = checked((int)Math.Round(seconds * AudioFormat.SampleRate, MidpointRounding.AwayFromZero));
        // Keep at least one full transition's worth of unmixed music on either
        // side, so unusually short material and branding retain their timing.
        if (outgoing.Frames < 2L * frames || incoming.Frames < 2L * frames) return null;
        return new(outgoing.Frames - frames, frames);
    }

    public static bool CanStart(CrossfadeWindow? window, long playedFrames, PcmPrefetchSnapshot? incoming,
        long? framesUntilTimedBoundary = null)
    {
        if (window is null || incoming is null || playedFrames != window.StartFrame ||
            incoming.Error is not null || incoming.Ended || incoming.Frames < window.Frames) return false;
        return framesUntilTimedBoundary is null || framesUntilTimedBoundary >= window.Frames;
    }
}
