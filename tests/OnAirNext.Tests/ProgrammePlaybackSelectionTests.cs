using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammePlaybackSelectionTests
{
    private static readonly string[] Stations = ["4", "9"];
    private static readonly DateOnly Date = new(2026, 11, 10);
    private static DateTimeOffset Local(int day, int hour = 0, int minute = 0, int second = 0)
        => new(2026, 11, day, hour, minute, second, TimeSpan.FromHours(3));
    private static ProgrammeDraft Programme() => new(new(Guid.NewGuid().ToString("N"), 1, "10 November",
        ["4"], CalendarRuleKind.SpecialDay, Date, Date, TimeOnly.MinValue, TimeOnly.MinValue,
        AllDay: true, StartPolicy: CalendarStartPolicy.FadeAtTime, ReturnPolicy: CalendarReturnPolicy.FadeAtTime),
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "remember-songs")),
        DisplayText: "Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz", SuppressStationBranding: true);
    private static ProgrammeDraft Insertion() => new(new(Guid.NewGuid().ToString("N"), 1, "09:05 remembrance",
        ["4"], CalendarRuleKind.TimedSweeper, Date, Date, new(9, 5), new(9, 6),
        StartPolicy: CalendarStartPolicy.FadeAtTime),
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "remember-signal")), SourceFile: "signal.opus");
    private static ProgrammePlaybackSelection At(ProgrammeDraft[] items, string station, DateTimeOffset now,
        params string[] completed) => ProgrammePlaybackSelector.Select(items, Stations, station, now,
            completed.ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void OneDayProgrammeAndInsertionUseExactIstanbulBoundariesAndDoNotTargetOtherStations()
    {
        var programme = Programme(); var insertion = Insertion(); var items = new[] { programme, insertion };
        Assert.Null(At(items, "4", Local(9, 23, 59, 59)).ActiveProgramme);
        var start = At(items, "4", Local(10));
        Assert.Equal(programme.Rule.Id, start.ActiveProgramme!.Rule.Id);
        Assert.True(start.ActiveProgramme.SuppressStationBranding);
        Assert.Equal("Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz", start.ActiveProgramme.DisplayText);
        Assert.Equal(Local(10, 9, 5).ToUniversalTime(), start.NextOccurrence!.DueAtUtc);
        Assert.Null(At(items, "4", Local(10, 9, 4, 59)).DueInsertion);
        var due = At(items, "4", Local(10, 9, 5));
        Assert.Equal(insertion.Rule.Id, due.DueInsertion!.Rule.Id);
        Assert.Equal(programme.Rule.Id, due.ActiveProgramme!.Rule.Id);
        Assert.Null(At(items, "9", Local(10, 9, 5)).ActiveProgramme);
        Assert.Null(At(items, "9", Local(10, 9, 5)).DueInsertion);
        Assert.Null(At(items, "4", Local(11)).ActiveProgramme);
    }

    [Fact]
    public void CompletedOrExpiredTimedInsertionIsNotSelectedAgain()
    {
        var items = new[] { Programme(), Insertion() };
        var due = At(items, "4", Local(10, 9, 5));
        var occurrenceKey = due.InsertionOccurrence!.Key;
        Assert.Null(At(items, "4", Local(10, 9, 5, 30), occurrenceKey).DueInsertion);
        Assert.NotNull(At(items, "4", Local(10, 9, 5, 30)).DueInsertion);
        Assert.Null(At(items, "4", Local(10, 9, 6)).DueInsertion);
        Assert.NotNull(At(items, "4", Local(10, 9, 6)).ActiveProgramme);
    }

    [Fact]
    public void OverlappingProgrammePrioritiesMustHaveAnUnambiguousWinner()
    {
        var first = Programme(); var second = Programme();
        Assert.Throws<InvalidOperationException>(() => At([first, second], "4", Local(10, 1)));
        var higher = second with { Rule = second.Rule with { Priority = 20 } };
        Assert.Equal(higher.Rule.Id, At([first, higher], "4", Local(10, 1)).ActiveProgramme!.Rule.Id);
    }
}
