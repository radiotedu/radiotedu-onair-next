namespace OnAirNext.Engine;

public sealed record ProgrammeAudioCue(CalendarOccurrence Occurrence, PlayoutSelection Selection,
    string? DisplayText, bool SuppressStationBranding);

// Immutable control-lane preparation for one station. The caller can warm the
// upcoming source before DueAtUtc and compare that timestamp on the sample
// clock; constructing a view or forecasting never records an actual play.
public sealed record ProgrammeRuntimeView(string StationId, DateTimeOffset ObservedAtUtc,
    ProgrammeAudioCue? Music, ProgrammeAudioCue? FollowingMusic, ProgrammeAudioCue? DueInsertion,
    ProgrammeAudioCue? Upcoming)
{
    public static ProgrammeRuntimeView Create(IEnumerable<ProgrammePublication> publications,
        IEnumerable<string> installedStations, string station, DateTimeOffset now,
        IReadOnlySet<string> completedInsertions, long musicPosition, long upcomingMusicPosition = 0)
    {
        ArgumentNullException.ThrowIfNull(publications);
        if (musicPosition < 0 || musicPosition >= long.MaxValue - 1 || upcomingMusicPosition < 0 || upcomingMusicPosition >= long.MaxValue - 1)
            throw new ArgumentException("Choose a valid music position.");
        var active = publications.Where(p => p.Active).Take(ProgrammeCalendar.MaximumRules + 1).ToArray();
        if (active.Length > ProgrammeCalendar.MaximumRules) throw new ArgumentException("Too many active programmes.");
        var byId = active.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var selection = ProgrammePlaybackSelector.Select(active.Select(p => p.Programme.Draft),
            installedStations, station, now, completedInsertions);

        ProgrammeAudioCue Music(CalendarOccurrence occurrence, long position)
        {
            var programme = byId[occurrence.RuleId].Programme;
            var count = programme.Items.Count;
            if (count == 0) throw new InvalidOperationException("The scheduled music window is empty.");
            var cycle = ProgrammeMusicOrder.PrepareCycle(programme.Items, programme.Draft.Order, occurrence.Key, position / count);
            var item = cycle[(int)(position % count)];
            return new(occurrence, new(item, HoldsRotation: true, ProgrammeMusicKey: occurrence.Key, ProgrammeMusicPosition: position),
                programme.Draft.DisplayText, programme.Draft.SuppressStationBranding);
        }
        ProgrammeAudioCue Insertion(CalendarOccurrence occurrence)
        {
            var programme = byId[occurrence.RuleId].Programme;
            if (programme.Items.Count != 1 || programme.Items[0].Kind != ContentKind.Program)
                throw new InvalidOperationException("A timed recording must resolve to one prepared programme asset.");
            return new(occurrence, new(programme.Items[0], HoldsRotation: true, ScheduledOccurrenceKey: occurrence.Key),
                selection.ActiveProgramme?.DisplayText ?? programme.Draft.DisplayText,
                selection.ActiveProgramme?.SuppressStationBranding == true);
        }

        var music = selection.ProgrammeOccurrence is { } window ? Music(window, musicPosition) : null;
        var following = selection.ProgrammeOccurrence is { } same ? Music(same, musicPosition + 1) : null;
        var insertion = selection.InsertionOccurrence is { } due ? Insertion(due) : null;
        var upcoming = selection.NextOccurrence is not { } next ? null
            : next.Kind == CalendarRuleKind.SpecialDay ? Music(next, upcomingMusicPosition) : Insertion(next);
        return new(station, now.ToUniversalTime(), music, following, insertion, upcoming);
    }
}
