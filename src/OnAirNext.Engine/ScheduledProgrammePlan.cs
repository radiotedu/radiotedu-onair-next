namespace OnAirNext.Engine;

// A control-lane projection only: callers must commit audible attempts before
// advancing musicPosition and must not mistake a projection for actual playout.
public sealed record ScheduledProgrammePlan(PlayoutSelection? Selection, string? MetadataOverride,
    string? MusicOccurrenceKey, bool SuppressStationBranding, CalendarOccurrence? NextOccurrence, PlayoutSelection? UpcomingInsertion)
{
    public static ScheduledProgrammePlan Create(IEnumerable<ProgrammePublication> publications,
        IEnumerable<string> installedStations, string station, DateTimeOffset now,
        IReadOnlySet<string> completedInsertions, long musicPosition)
    {
        ArgumentNullException.ThrowIfNull(publications);
        if (musicPosition < 0) throw new ArgumentException("Music progress cannot be negative.");
        var active = publications.Where(p => p.Active).Take(ProgrammeCalendar.MaximumRules + 1).ToArray();
        if (active.Length > ProgrammeCalendar.MaximumRules) throw new ArgumentException("Too many active programmes.");
        var byId = active.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var selected = ProgrammePlaybackSelector.Select(active.Select(p => p.Programme.Draft),
            installedStations, station, now, completedInsertions);
        var text = selected.ActiveProgramme?.DisplayText;
        PlayoutSelection? NextInsertion(CalendarOccurrence? occurrence)
        {
            if (occurrence is null || occurrence.Kind != CalendarRuleKind.TimedSweeper) return null;
            var media = byId[occurrence.RuleId].Programme.Items;
            if (media.Count != 1 || media[0].Kind != ContentKind.Program)
                throw new InvalidOperationException("A timed recording must resolve to one prepared programme asset.");
            return new(media[0], HoldsRotation: true, ScheduledOccurrenceKey: occurrence.Key);
        }
        var insertion = NextInsertion(selected.InsertionOccurrence);
        if (insertion is not null)
            return new(insertion, text ?? selected.DueInsertion?.DisplayText, selected.ProgrammeOccurrence?.Key,
                selected.ActiveProgramme?.SuppressStationBranding == true, selected.NextOccurrence, NextInsertion(selected.NextOccurrence));
        if (selected.ProgrammeOccurrence is not { } occurrence || selected.ActiveProgramme is not { } programme)
            return new(null, null, null, false, selected.NextOccurrence, NextInsertion(selected.NextOccurrence));
        var items = byId[occurrence.RuleId].Programme.Items;
        if (items.Count == 0) throw new InvalidOperationException("The active programme contains no music.");
        var cycle = musicPosition / items.Count;
        var offset = (int)(musicPosition % items.Count);
        var music = ProgrammeMusicOrder.PrepareCycle(items, programme.Order, occurrence.Key, cycle)[offset];
        return new(new(music, HoldsRotation: true, ProgrammeMusicKey: occurrence.Key, ProgrammeMusicPosition: musicPosition),
            text, occurrence.Key, programme.SuppressStationBranding,
            selected.NextOccurrence, NextInsertion(selected.NextOccurrence));
    }
}
