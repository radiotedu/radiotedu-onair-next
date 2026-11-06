using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammeTransitionPolicyTests
{
    private static DateTimeOffset Local(int day, int hour = 0, int minute = 0, int second = 0)
        => new(2026, 11, day, hour, minute, second, TimeSpan.FromHours(3));
    private static CalendarOccurrence Occurrence(CalendarRuleKind kind, DateTimeOffset start, DateTimeOffset end)
    {
        var id = Guid.NewGuid().ToString("N");
        return new(id + "/1/4/" + start.UtcTicks, id, 1, "Remembrance", "4", kind, start.ToUniversalTime(), end.ToUniversalTime(),
            0, CalendarStartPolicy.FadeAtTime, CalendarReturnPolicy.FadeAtTime);
    }

    [Fact]
    public void MidnightAndNineOhFiveAreAdmittedAtTheirDeadlinesAndTheRecordingFinishesInFull()
    {
        var window = Occurrence(CalendarRuleKind.SpecialDay, Local(10), Local(11));
        var atNine = Occurrence(CalendarRuleKind.TimedSweeper, Local(10, 9, 5), Local(10, 9, 6));
        var ordinary = new PlayoutSelection(new("pop", "pop.opus", "", "Pop"));
        var first = new ProgrammeAudioCue(window, new(new("music", "music.opus", "", "Music"), HoldsRotation: true,
            ProgrammeMusicKey: window.Key, ProgrammeMusicPosition: 0), "Remembrance metadata", true);
        var second = first with { Selection = first.Selection with { Item = first.Selection.Item with { Id = "music-2" }, ProgrammeMusicPosition = 1 } };
        var signal = new ProgrammeAudioCue(atNine, new(new("signal", "signal.opus", "", "Recording", ContentKind.Program, 87),
            HoldsRotation: true, ScheduledOccurrenceKey: atNine.Key), "Remembrance metadata", true);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var before = new ProgrammeRuntimeView("4", Local(9, 23, 59, 50), null, null, null, first);
        ProgrammeTransition Choose(ProgrammeRuntimeView view, DateTimeOffset now, PlayoutSelection? current,
            CalendarOccurrence? owner = null, bool finished = false, bool ready = true)
            => ProgrammeTransitionPolicy.Decide(view, now, current, owner, finished, done, _ => ready);
        Assert.Equal(ProgrammeTransitionKind.Continue, Choose(before, Local(9, 23, 59, 59), ordinary).Kind);
        var midnight = Choose(before, Local(10), ordinary);
        Assert.Equal(ProgrammeTransitionKind.Scheduled, midnight.Kind); Assert.Equal("music", midnight.Cue!.Selection.Item.Id);
        var morning = new ProgrammeRuntimeView("4", Local(10, 9, 4, 59), first, second, null, signal);
        Assert.Equal(ProgrammeTransitionKind.AwaitingSource, Choose(morning, Local(10, 9, 5), first.Selection, window, ready: false).Kind);
        Assert.Equal("signal", Choose(morning, Local(10, 9, 5), first.Selection, window).Cue!.Selection.Item.Id);
        Assert.Equal(ProgrammeTransitionKind.Continue, Choose(morning, Local(10, 9, 6, 15), signal.Selection, window).Kind);
        done.Add(atNine.Key);
        var resume = Choose(morning, Local(10, 9, 6, 27), signal.Selection, window, finished: true);
        Assert.Equal(ProgrammeTransitionKind.Scheduled, resume.Kind); Assert.Equal("music", resume.Cue!.Selection.Item.Id);
        Assert.Equal(ProgrammeTransitionKind.Ordinary, Choose(morning, Local(11), first.Selection, window).Kind);
    }

    [Fact]
    public void MissingOrStalePreparedMusicFallsBackWithoutRepeatingAnOwnedPosition()
    {
        var window = Occurrence(CalendarRuleKind.SpecialDay, Local(10), Local(11));
        var cue = new ProgrammeAudioCue(window, new(new("one", "one.opus", "", "One"), HoldsRotation: true,
            ProgrammeMusicKey: window.Key, ProgrammeMusicPosition: 0), null, true);
        var view = new ProgrammeRuntimeView("4", Local(10), cue, cue, null, null);
        var decision = ProgrammeTransitionPolicy.Decide(view, Local(10, 1), cue.Selection, window, true,
            new HashSet<string>(), _ => true);
        Assert.Equal(ProgrammeTransitionKind.Ordinary, decision.Kind);
    }

    [Fact]
    public void AllowedStationBrandingFinishesInsideTheSpecialWindow()
    {
        var window = Occurrence(CalendarRuleKind.SpecialDay, Local(10), Local(11));
        var cue = new ProgrammeAudioCue(window, new(new("one", "one.opus", "", "One"), HoldsRotation: true,
            ProgrammeMusicKey: window.Key, ProgrammeMusicPosition: 0), null, false);
        var view = new ProgrammeRuntimeView("4", Local(10), cue, cue, null, null);
        var jingle = new PlayoutSelection(new("jingle", "jingle.opus", "", "ID", ContentKind.Jingle), HoldsRotation: true);
        var decision = ProgrammeTransitionPolicy.Decide(view, Local(10, 1), jingle, window, false,
            new HashSet<string>(), _ => true);
        Assert.Equal(ProgrammeTransitionKind.Continue, decision.Kind);
    }
}
