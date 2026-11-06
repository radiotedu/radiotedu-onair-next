namespace OnAirNext.Engine;

// The fixed event text belongs to the special-day window, including a timed
// recording within it. Keep the last known text through a transient control
// read failure, but never past its known end time.
internal sealed class ProgrammeDisplayState
{
    private string? _text;
    private DateTimeOffset _until;

    public string? Resolve(ProgrammeRuntimeSnapshot snapshot, string station, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Healthy && Math.Abs((now - snapshot.UpdatedAtUtc).TotalSeconds) <= 30 &&
            snapshot.Stations.TryGetValue(station, out var view))
        {
            var cue = Valid(view.Music, now) ? view.Music : Valid(view.Upcoming, now) ? view.Upcoming : null;
            _text = cue?.DisplayText;
            _until = cue?.Occurrence.EndsAtUtc ?? default;
        }
        else if (now >= _until) { _text = null; _until = default; }
        return _text;
    }

    private static bool Valid(ProgrammeAudioCue? cue, DateTimeOffset now)
        => cue is not null && cue.Occurrence.Kind == CalendarRuleKind.SpecialDay &&
            cue.Occurrence.DueAtUtc <= now && now < cue.Occurrence.EndsAtUtc;
}
