namespace OnAirNext.Engine;

// Prepared off the programme lane and reusable across stations. The mixer is
// allocation-free; its output must still pass the normal final-mix limiter.
public sealed class EqualPowerCrossfade
{
    private readonly float[] _outgoing, _incoming;
    public int Frames => _outgoing.Length;

    public EqualPowerCrossfade(int frames)
    {
        if (frames is < 2 or > 15 * AudioFormat.SampleRate)
            throw new ArgumentOutOfRangeException(nameof(frames));
        _outgoing = new float[frames]; _incoming = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var angle = Math.PI * i / (2 * (frames - 1));
            _outgoing[i] = (float)Math.Cos(angle); _incoming[i] = (float)Math.Sin(angle);
        }
        _outgoing[0] = 1; _incoming[0] = 0; _outgoing[^1] = 0; _incoming[^1] = 1;
    }

    public void Mix(ReadOnlySpan<float> outgoing, ReadOnlySpan<float> incoming, Span<float> output, int firstFrame)
    {
        if (outgoing.Length != incoming.Length || outgoing.Length % AudioFormat.Channels != 0 ||
            output.Length < outgoing.Length || firstFrame < 0 || firstFrame > Frames ||
            outgoing.Length / AudioFormat.Channels > Frames - firstFrame)
            throw new ArgumentException("Choose matching stereo blocks inside the prepared transition.");
        for (var frame = 0; frame < outgoing.Length / AudioFormat.Channels; frame++)
        {
            var outGain = _outgoing[firstFrame + frame]; var inGain = _incoming[firstFrame + frame];
            for (var channel = 0; channel < AudioFormat.Channels; channel++)
            {
                var index = frame * AudioFormat.Channels + channel;
                var mixed = outgoing[index] * outGain + incoming[index] * inGain;
                // Bound the combined music to the same domain as one decoder so
                // a simultaneous maximum-domain live contribution stays valid.
                output[index] = Math.Clamp(mixed, -AudioFormat.MaximumSourceSample, AudioFormat.MaximumSourceSample);
            }
        }
    }
}
