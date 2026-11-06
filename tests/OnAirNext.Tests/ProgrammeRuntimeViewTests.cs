using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammeRuntimeViewTests
{
    private static DateTimeOffset Local(int day, int hour = 0, int minute = 0, int second = 0)
        => new(2026, 11, day, hour, minute, second, TimeSpan.FromHours(3));

    [Fact]
    public void RadioCanWarmTheMidnightProgrammeAndTheNineOhFiveRecordingBeforeTheirDeadlines()
    {
        var date = new DateOnly(2026, 11, 10); var root = Path.Combine(Path.GetTempPath(), "special-day");
        var music = new ProgrammeDraft(new(Guid.NewGuid().ToString("N"), 1, "10 November", ["4"],
            CalendarRuleKind.SpecialDay, date, date, TimeOnly.MinValue, TimeOnly.MinValue, AllDay: true,
            StartPolicy: CalendarStartPolicy.FadeAtTime, ReturnPolicy: CalendarReturnPolicy.FadeAtTime), root,
            DisplayText: "Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz", SuppressStationBranding: true);
        var insertion = new ProgrammeDraft(new(Guid.NewGuid().ToString("N"), 1, "09:05", ["4"],
            CalendarRuleKind.TimedSweeper, date, date, new(9, 5), new(9, 6), StartPolicy: CalendarStartPolicy.FadeAtTime),
            root, SourceFile: "signal.opus");
        var tracks = Enumerable.Range(0, 34).Select(i => new MediaItem("song-" + i, Path.Combine(root, i + ".opus"), "", "Song " + i)).ToArray();
        ProgrammePublication[] publications = [new(new(music, tracks), 1, true, "operator", DateTimeOffset.UtcNow),
            new(new(insertion, [new("signal", Path.Combine(root, "signal.opus"), "", "Remembrance", ContentKind.Program, 87)]), 1, true, "operator", DateTimeOffset.UtcNow)];
        string[] stations = ["4", "9"]; var completed = new HashSet<string>(StringComparer.Ordinal);
        ProgrammeRuntimeView At(string station, DateTimeOffset at, long position = 0)
            => ProgrammeRuntimeView.Create(publications, stations, station, at, completed, position);

        var before = At("4", Local(9, 23, 59, 50));
        Assert.Null(before.Music); Assert.Equal(Local(10).ToUniversalTime(), before.Upcoming!.Occurrence.DueAtUtc);
        Assert.Equal(ContentKind.Music, before.Upcoming.Selection.Item.Kind);
        Assert.True(before.Upcoming.SuppressStationBranding);
        var window = At("4", Local(10, 9, 4, 59), 3);
        Assert.Equal(3, window.Music!.Selection.ProgrammeMusicPosition);
        Assert.Equal(4, window.FollowingMusic!.Selection.ProgrammeMusicPosition);
        Assert.Equal("signal", window.Upcoming!.Selection.Item.Id);
        Assert.Equal(Local(10, 9, 5).ToUniversalTime(), window.Upcoming.Occurrence.DueAtUtc);
        var due = At("4", Local(10, 9, 5), 3);
        Assert.Equal("signal", due.DueInsertion!.Selection.Item.Id);
        Assert.Equal(87, due.DueInsertion.Selection.Item.DurationSeconds);
        Assert.Equal("Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz", due.DueInsertion.DisplayText);
        completed.Add(due.DueInsertion.Occurrence.Key);
        Assert.Null(At("4", Local(10, 9, 5, 30), 3).DueInsertion);
        Assert.Null(At("9", Local(10, 9, 5)).Music);
        Assert.Null(At("9", Local(10, 9, 5)).DueInsertion);
        Assert.Null(At("4", Local(11)).Music);
    }
}
