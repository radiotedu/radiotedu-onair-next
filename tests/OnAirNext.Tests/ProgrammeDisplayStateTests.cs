using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammeDisplayStateTests
{
    [Fact]
    public void FixedTextBeginsAndEndsAtTheWindowAndSurvivesATransientControlFailure()
    {
        var start = new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero); var end = start.AddDays(1);
        var id = Guid.NewGuid().ToString("N"); var key = id + "/1/4/" + start.UtcTicks;
        var occurrence = new CalendarOccurrence(key, id, 1, "10 November", "4", CalendarRuleKind.SpecialDay,
            start, end, 0, CalendarStartPolicy.FadeAtTime, CalendarReturnPolicy.FadeAtTime);
        var cue = new ProgrammeAudioCue(occurrence, new(new("one", "one.opus", "", "Song"), HoldsRotation: true),
            "Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz", true);
        var view = new ProgrammeRuntimeView("4", start.AddSeconds(-1), null, null, null, cue);
        var healthy = new ProgrammeRuntimeSnapshot(true, start, new Dictionary<string, ProgrammeRuntimeView> { ["4"] = view }, null);
        var failed = new ProgrammeRuntimeSnapshot(false, start, new Dictionary<string, ProgrammeRuntimeView>(), "unavailable");
        var state = new ProgrammeDisplayState();
        Assert.Null(state.Resolve(healthy, "4", start.AddTicks(-1)));
        Assert.Equal(cue.DisplayText, state.Resolve(healthy, "4", start));
        Assert.Equal(cue.DisplayText, state.Resolve(failed, "4", end.AddTicks(-1)));
        Assert.Null(state.Resolve(failed, "4", end));
        Assert.Null(new ProgrammeDisplayState().Resolve(healthy, "9", start));
    }
}
