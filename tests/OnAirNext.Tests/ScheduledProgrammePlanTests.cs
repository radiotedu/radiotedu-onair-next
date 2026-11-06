using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ScheduledProgrammePlanTests
{
    private static readonly string[] Stations = ["4", "9"];
    private static readonly DateOnly Date = new(2026, 11, 10);
    private static DateTimeOffset Local(int day, int hour = 0, int minute = 0, int second = 0)
        => new(2026, 11, day, hour, minute, second, TimeSpan.FromHours(3));
    private static ProgrammePublication Publication(ProgrammeDraft draft, params MediaItem[] items)
        => new(new(draft, items), 1, true, "operator", DateTimeOffset.UtcNow);
    private static (ProgrammePublication Music, ProgrammePublication Signal) Fixtures()
    {
        var root = Path.Combine(Path.GetTempPath(), "programme-fixture");
        var music = new ProgrammeDraft(new(Guid.NewGuid().ToString("N"), 1, "Remembrance", ["4"],
            CalendarRuleKind.SpecialDay, Date, Date, TimeOnly.MinValue, TimeOnly.MinValue, AllDay: true,
            StartPolicy: CalendarStartPolicy.FadeAtTime, ReturnPolicy: CalendarReturnPolicy.FadeAtTime), root,
            DisplayText: "Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz", SuppressStationBranding: true);
        var signal = new ProgrammeDraft(new(Guid.NewGuid().ToString("N"), 1, "09:05", ["4"],
            CalendarRuleKind.TimedSweeper, Date, Date, new(9, 5), new(9, 6), StartPolicy: CalendarStartPolicy.FadeAtTime),
            root, SourceFile: "signal.opus");
        return (Publication(music, Enumerable.Range(0, 34).Select(i => new MediaItem("music-" + i, Path.Combine(root, i + ".opus"), "", "Song " + i)).ToArray()),
            Publication(signal, new MediaItem("signal", Path.Combine(root, "signal.opus"), "", "Remembrance", ContentKind.Program, 87)));
    }
    private static ScheduledProgrammePlan At(ProgrammePublication[] publications, string station, DateTimeOffset now,
        long position = 0, params string[] completed) => ScheduledProgrammePlan.Create(publications, Stations, station, now,
            completed.ToHashSet(StringComparer.Ordinal), position);

    [Fact]
    public void ProgrammeHoldsOrdinaryRotationAndSchedulesTheInsertionOnlyForRadio()
    {
        var (music, signal) = Fixtures(); ProgrammePublication[] publications = [music, signal];
        Assert.Null(At(publications, "4", Local(9, 23, 59, 59)).Selection);
        var start = At(publications, "4", Local(10));
        Assert.True(start.Selection!.HoldsRotation);
        Assert.True(start.SuppressStationBranding);
        Assert.Equal(ContentKind.Music, start.Selection.Item.Kind);
        Assert.Null(start.Selection.ScheduledOccurrenceKey);
        Assert.Equal("signal", start.UpcomingInsertion!.Item.Id);
        Assert.Equal(Local(10, 9, 5).ToUniversalTime(), start.NextOccurrence!.DueAtUtc);
        var insertion = At(publications, "4", Local(10, 9, 5));
        Assert.Equal("signal", insertion.Selection!.Item.Id);
        Assert.True(insertion.Selection.HoldsRotation);
        Assert.NotNull(insertion.Selection.ScheduledOccurrenceKey);
        Assert.Equal(87, insertion.Selection.Item.DurationSeconds);
        Assert.Equal("Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz", insertion.MetadataOverride);
        var after = At(publications, "4", Local(10, 9, 5, 30), 0, insertion.Selection.ScheduledOccurrenceKey!);
        Assert.Equal(start.Selection.Item.Id, after.Selection!.Item.Id);
        Assert.Null(At(publications, "9", Local(10, 9, 5)).Selection);
        Assert.Null(At(publications, "4", Local(11)).Selection);
    }

    [Fact]
    public void EachShuffleCycleContainsAllSongsAndCanBeReconstructedAfterRestart()
    {
        var (music, signal) = Fixtures(); ProgrammePublication[] publications = [music, signal];
        var first = Enumerable.Range(0, 34).Select(i => At(publications, "4", Local(10, 1), i).Selection!.Item.Id).ToArray();
        Assert.Equal(34, first.Distinct(StringComparer.Ordinal).Count());
        var next = At(publications, "4", Local(10, 1), 34);
        Assert.NotEqual(first[^1], next.Selection!.Item.Id);
        Assert.Equal(next.Selection.Item.Id, At(publications, "4", Local(10, 1), 34).Selection!.Item.Id);
    }
}
