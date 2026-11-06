namespace OnAirNext.Engine;

public enum ProgrammeTransitionKind { Continue, Scheduled, Ordinary, AwaitingSource }
public sealed record ProgrammeTransition(ProgrammeTransitionKind Kind, ProgrammeAudioCue? Cue = null);

// Pure sample-clock decision: the caller owns prepared decoders, persists actual
// playback and keeps a completed-insertion set until the journal catches up.
public static class ProgrammeTransitionPolicy
{
    public static ProgrammeTransition Decide(ProgrammeRuntimeView view, DateTimeOffset now,
        PlayoutSelection? current, CalendarOccurrence? currentMusicWindow, bool currentFinished,
        IReadOnlySet<string> completedInsertions, Func<ProgrammeAudioCue, bool> sourceReady)
    {
        ArgumentNullException.ThrowIfNull(view); ArgumentNullException.ThrowIfNull(completedInsertions);
        ArgumentNullException.ThrowIfNull(sourceReady);
        var music = Valid(view.Music, CalendarRuleKind.SpecialDay, now) ? view.Music : null;
        var due = Valid(view.DueInsertion, CalendarRuleKind.TimedSweeper, now) ? view.DueInsertion : null;
        if (view.Upcoming is { } upcoming && Valid(upcoming, upcoming.Occurrence.Kind, now))
        {
            if (upcoming.Occurrence.Kind == CalendarRuleKind.SpecialDay) music ??= upcoming;
            else due ??= upcoming;
        }
        if (due is not null && (completedInsertions.Contains(due.Occurrence.Key) ||
            currentFinished && current?.ScheduledOccurrenceKey == due.Occurrence.Key)) due = null;

        // A recording already started inside its tolerance window is allowed to
        // finish in full even when the admission window itself has expired.
        if (!currentFinished && current?.ScheduledOccurrenceKey is not null)
        {
            if (currentMusicWindow is { } owner && now >= owner.EndsAtUtc && owner.ReturnPolicy == CalendarReturnPolicy.FadeAtTime)
                return new(ProgrammeTransitionKind.Ordinary);
            return new(ProgrammeTransitionKind.Continue);
        }

        var leavingMusic = current?.ProgrammeMusicKey is { } key && (music is null || music.Occurrence.Key != key);
        if (leavingMusic && !currentFinished && currentMusicWindow?.ReturnPolicy == CalendarReturnPolicy.FinishCurrentItem)
            return new(ProgrammeTransitionKind.Continue);

        if (due is not null && current?.ScheduledOccurrenceKey != due.Occurrence.Key &&
            (currentFinished || due.Occurrence.StartPolicy == CalendarStartPolicy.FadeAtTime))
            return Prepared(due, currentFinished, sourceReady);

        if (!currentFinished && current is { HoldsRotation: true, QueueEntryId: null } &&
            (current.Item.Kind is ContentKind.Jingle or ContentKind.Sweeper) && music is { SuppressStationBranding: false } &&
            currentMusicWindow?.Key == music.Occurrence.Key)
            return new(ProgrammeTransitionKind.Continue);

        if (music is not null)
        {
            if (current?.ProgrammeMusicKey == music.Occurrence.Key)
            {
                if (!currentFinished) return new(ProgrammeTransitionKind.Continue);
                var target = current.ProgrammeMusicPosition is { } position && music.Selection.ProgrammeMusicPosition is { } candidate && candidate <= position
                    ? view.FollowingMusic : music;
                if (target?.Selection.ProgrammeMusicPosition is not { } nextPosition ||
                    current.ProgrammeMusicPosition is { } previous && nextPosition <= previous)
                    return new(ProgrammeTransitionKind.Ordinary);
                return Prepared(target, true, sourceReady);
            }
            if (currentFinished || music.Occurrence.StartPolicy == CalendarStartPolicy.FadeAtTime || leavingMusic)
                return Prepared(music, currentFinished, sourceReady);
        }
        return currentFinished || leavingMusic ? new(ProgrammeTransitionKind.Ordinary) : new(ProgrammeTransitionKind.Continue);
    }

    private static bool Valid(ProgrammeAudioCue? cue, CalendarRuleKind kind, DateTimeOffset now)
        => cue is not null && cue.Occurrence.Kind == kind && cue.Occurrence.DueAtUtc <= now && now < cue.Occurrence.EndsAtUtc;
    private static ProgrammeTransition Prepared(ProgrammeAudioCue cue, bool currentFinished, Func<ProgrammeAudioCue, bool> ready)
        => ready(cue) ? new(ProgrammeTransitionKind.Scheduled, cue)
            : new(currentFinished ? ProgrammeTransitionKind.Ordinary : ProgrammeTransitionKind.AwaitingSource, cue);
}
