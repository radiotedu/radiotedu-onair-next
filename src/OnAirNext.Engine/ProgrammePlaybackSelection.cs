namespace OnAirNext.Engine;

// Control-lane selection only. Callers must persist publication and occurrence
// completion separately and must never treat this forecast as an actual play.
public sealed record ProgrammePlaybackSelection(ProgrammeDraft? ActiveProgramme,
    CalendarOccurrence? ProgrammeOccurrence, ProgrammeDraft? DueInsertion,
    CalendarOccurrence? InsertionOccurrence, CalendarOccurrence? NextOccurrence);

public static class ProgrammePlaybackSelector
{
    public static ProgrammePlaybackSelection Select(IEnumerable<ProgrammeDraft> published,
        IEnumerable<string> installedStations, string station, DateTimeOffset now,
        IReadOnlySet<string> completedInsertions)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(installedStations);
        ArgumentNullException.ThrowIfNull(completedInsertions);
        var known = installedStations.ToHashSet(StringComparer.Ordinal);
        if (!known.Contains(station)) throw new ArgumentException("Choose an installed station.");
        var drafts = published.Take(ProgrammeCalendar.MaximumRules + 1).ToArray();
        if (drafts.Length > ProgrammeCalendar.MaximumRules || drafts.Any(d => d is null))
            throw new ArgumentException("Too many published programmes.");
        var normalized = drafts.Select(d => d.Normalize(known)).ToArray();
        var byId = normalized.ToDictionary(d => d.Rule.Id, StringComparer.OrdinalIgnoreCase);
        var forecast = ProgrammeCalendar.Forecast(normalized.Select(d => d.Rule with { Enabled = true }),
            known, now, TimeSpan.FromHours(24));
        var occurrences = forecast.Occurrences.Where(o => o.StationId == station).ToArray();
        var active = occurrences.Where(o => o.Kind == CalendarRuleKind.SpecialDay && o.DueAtUtc <= now && now < o.EndsAtUtc)
            .OrderByDescending(o => o.Priority).ThenBy(o => o.Key, StringComparer.Ordinal).ToArray();
        var due = occurrences.Where(o => o.Kind == CalendarRuleKind.TimedSweeper && o.DueAtUtc <= now && now < o.EndsAtUtc &&
                !completedInsertions.Contains(o.Key))
            .OrderByDescending(o => o.Priority).ThenBy(o => o.DueAtUtc).ThenBy(o => o.Key, StringComparer.Ordinal).ToArray();
        RejectEqualPriority(active, "special-day programmes");
        RejectEqualPriority(due, "timed insertions");
        var programme = active.FirstOrDefault();
        var insertion = due.FirstOrDefault();
        var next = occurrences.Where(o => o.DueAtUtc > now &&
                (o.Kind != CalendarRuleKind.TimedSweeper || !completedInsertions.Contains(o.Key)))
            .OrderBy(o => o.DueAtUtc).ThenByDescending(o => o.Priority).ThenBy(o => o.Key, StringComparer.Ordinal).FirstOrDefault();
        return new(programme is null ? null : byId[programme.RuleId], programme,
            insertion is null ? null : byId[insertion.RuleId], insertion, next);
    }

    private static void RejectEqualPriority(CalendarOccurrence[] candidates, string label)
    {
        if (candidates.Length > 1 && candidates[0].Priority == candidates[1].Priority)
            throw new InvalidOperationException("Conflicting " + label + " have equal priority; resolve the schedule before playback.");
    }
}
