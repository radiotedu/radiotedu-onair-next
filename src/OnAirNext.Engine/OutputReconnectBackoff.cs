namespace OnAirNext.Engine;

// Preserve backoff during an outage, but do not penalize a later independent
// interruption after the source has been stable for a sustained period.
internal sealed class OutputReconnectBackoff
{
    private int _failures;
    internal TimeSpan Next(TimeSpan? connectedFor)
    {
        if (connectedFor >= TimeSpan.FromSeconds(30)) _failures = 0;
        _failures = Math.Min(_failures + 1, 5);
        return TimeSpan.FromMilliseconds(Math.Min(3000, 200 * (1 << _failures)));
    }
}
