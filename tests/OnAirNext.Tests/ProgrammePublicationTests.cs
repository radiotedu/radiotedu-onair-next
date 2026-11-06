using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammePublicationTests
{
    private static readonly IReadOnlySet<string> Stations = new HashSet<string>(["4", "9"], StringComparer.Ordinal);
    private static string Root()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "programme-publication-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static ProgrammeDraft Draft(string folder) => new(new(Id(), 1, "10 November", ["4"],
        CalendarRuleKind.SpecialDay, new(2026, 11, 10), new(2026, 11, 10), TimeOnly.MinValue,
        TimeOnly.MinValue, AllDay: true), folder, DisplayText: "Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz", SuppressStationBranding: true);
    private static async Task<PreparedProgramme> Prepared(ProgrammeDraft draft)
    {
        var path = Path.Combine(draft.SourceFolder, "song.opus"); await File.WriteAllTextAsync(path, "fixture audio");
        var info = new FileInfo(path);
        var loudness = new SourceLoudness(new string('A', 64), info.Length, info.LastWriteTimeUtc.Ticks, 30,
            -23, -3, 5, DateTimeOffset.UtcNow);
        return new(draft, [new MediaItem("song", path, "Artist", "Song", DurationSeconds: 30, Loudness: loudness)]);
    }
    private static async Task Initialize(EventJournal journal)
    { await journal.InitializeProgrammeDraftsAsync(); await journal.InitializeProgrammePublicationsAsync(); }

    [Fact]
    public async Task PublicationSurvivesReopenAndRetryEvenWhenMediaTemporarilyDisappears()
    {
        var root = Root(); var folder = Path.Combine(root, "music"); Directory.CreateDirectory(folder);
        var database = Path.Combine(root, "playback.db"); var draft = Draft(folder); var prepared = await Prepared(draft);
        var command = Id(); ProgrammePublication first;
        await using (var journal = new EventJournal(database))
        {
            await Initialize(journal); await journal.SaveProgrammeDraftAsync(draft, 0, Id(), "operator", Stations);
            first = await journal.PublishPreparedProgrammeAsync(prepared, 0, command, "operator", Stations);
            Assert.True(first.Active); Assert.Equal(1, first.Version);
        }
        File.Move(prepared.Items[0].Path, Path.Combine(folder, "temporarily-offline.opus"));
        await using var reopened = new EventJournal(database); await Initialize(reopened);
        var retry = await reopened.PublishPreparedProgrammeAsync(prepared, 0, command, "operator", Stations);
        Assert.Equal(first.AtUtc, retry.AtUtc);
        var active = Assert.Single(await reopened.ActiveProgrammePublicationsAsync(Stations));
        Assert.True(active.Programme.Draft.SuppressStationBranding); Assert.Equal("Song", Assert.Single(active.Programme.Items).Title);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.PublishPreparedProgrammeAsync(prepared, 0, command, "other-operator", Stations));
        var stopped = await reopened.UnpublishProgrammeAsync(first.Id, 1, Id(), "operator", Stations);
        Assert.False(stopped.Active); Assert.Equal(2, stopped.Version);
        Assert.Empty(await reopened.ActiveProgrammePublicationsAsync(Stations));
        Assert.Empty(EventJournal.ReadEvents(database));
    }

    [Fact]
    public async Task AChangedDraftOrSourceCannotBePublishedAndARejectedRequestDoesNotArmAnEvent()
    {
        var root = Root(); var folder = Path.Combine(root, "music"); Directory.CreateDirectory(folder);
        var draft = Draft(folder); var prepared = await Prepared(draft);
        await using var journal = new EventJournal(Path.Combine(root, "playback.db")); await Initialize(journal);
        await journal.SaveProgrammeDraftAsync(draft, 0, Id(), "operator", Stations);
        await journal.SaveProgrammeDraftAsync(draft with { Rule = draft.Rule with { Version = 2, Name = "Changed" } }, 1, Id(), "operator", Stations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PublishPreparedProgrammeAsync(prepared, 0, Id(), "operator", Stations));
        await File.AppendAllTextAsync(prepared.Items[0].Path, "changed");
        var updated = prepared with { Draft = prepared.Draft with { Rule = prepared.Draft.Rule with { Version = 2, Name = "Changed" } } };
        await Assert.ThrowsAsync<IOException>(() => journal.PublishPreparedProgrammeAsync(updated, 0, Id(), "operator", Stations));
        Assert.Empty(await journal.ActiveProgrammePublicationsAsync(Stations));
    }
}
