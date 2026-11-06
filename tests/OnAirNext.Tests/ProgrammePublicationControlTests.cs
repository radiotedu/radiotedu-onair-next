using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammePublicationControlTests
{
    private static string Id() => Guid.NewGuid().ToString("N");
    private static readonly IReadOnlySet<string> Stations = new HashSet<string>(["4", "9"], StringComparer.Ordinal);
    private static string Folder()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "programme-control-tests", Id());
        Directory.CreateDirectory(path); return path;
    }
    private static ProgrammeDraft Draft(string folder) => new(new(Id(), 1, "10 November", ["4"],
        CalendarRuleKind.SpecialDay, new(2026, 11, 10), new(2026, 11, 10), TimeOnly.MinValue, TimeOnly.MinValue,
        AllDay: true), folder, DisplayText: "Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz", SuppressStationBranding: true);
    private static Task<AudioMetadata> Probe(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var file = new FileInfo(path);
        var measured = new SourceLoudness(new string('A', 64), file.Length, file.LastWriteTimeUtc.Ticks,
            30, -23, -3, 4, DateTimeOffset.UtcNow);
        return Task.FromResult(new AudioMetadata("Artist", "Song", 30, measured));
    }
    private static async Task<ProgrammePreparationStatus> Finished(ProgrammePublicationControl control, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var status = control.Get(id); if (status.FinishedAt is not null) return status;
            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task AReviewedSavedDraftIsPreparedPublishedReadBackAndUnpublished()
    {
        var root = Folder(); var mediaFolder = Path.Combine(root, "music"); Directory.CreateDirectory(mediaFolder);
        await File.WriteAllTextAsync(Path.Combine(mediaFolder, "song.opus"), "fixture");
        var database = Path.Combine(root, "playback.db"); var draft = Draft(mediaFolder); var command = Id();
        await using var journal = new EventJournal(database);
        await journal.InitializeProgrammeDraftsAsync(); await journal.InitializeProgrammePublicationsAsync();
        await journal.SaveProgrammeDraftAsync(draft, 0, Id(), "operator", Stations);
        await using var control = new ProgrammePublicationControl(journal, new ProgrammeMediaPreparation(Probe), Stations);
        var notifications = 0; control.Changed += () => Interlocked.Increment(ref notifications);
        Assert.Equal("Preparing", (await control.StartAsync(draft.Rule.Id, 1, 0, command, "operator")).State);
        var result = await Finished(control, command);
        Assert.Equal("Published", result.State); Assert.True(result.Publication!.Active);
        Assert.Equal(1, result.Publication.AudioItems); Assert.Equal(new[] { "4" }, result.Publication.Stations);
        Assert.Equal("Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz", result.Publication.DisplayText);
        var stored = await control.PublicationAsync(draft.Rule.Id);
        Assert.NotNull(stored); Assert.Equal(result.Publication.Id, stored.Id);
        Assert.Equal(result.Publication.Version, stored.Version); Assert.Equal(result.Publication.AudioItems, stored.AudioItems);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.ArchiveProgrammeDraftAsync(draft.Rule.Id, 1, Id(), "operator", Stations));
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartAsync(draft.Rule.Id, 2, 1, Id(), "operator"));
        var stopped = await control.UnpublishAsync(draft.Rule.Id, 1, Id(), "operator");
        Assert.False(stopped.Active); Assert.Equal(2, stopped.Version);
        Assert.True((await journal.ArchiveProgrammeDraftAsync(draft.Rule.Id, 1, Id(), "operator", Stations)).Archived);
        Assert.Equal(2, notifications);
        Assert.Empty(await journal.ActiveProgrammePublicationsAsync(Stations));
    }

    [Fact]
    public async Task AFailedAudioValidationDoesNotPublishAnyPartialProgramme()
    {
        var root = Folder(); var mediaFolder = Path.Combine(root, "music"); Directory.CreateDirectory(mediaFolder);
        await File.WriteAllTextAsync(Path.Combine(mediaFolder, "bad.opus"), "not audio");
        var draft = Draft(mediaFolder); await using var journal = new EventJournal(Path.Combine(root, "playback.db"));
        await journal.InitializeProgrammeDraftsAsync(); await journal.InitializeProgrammePublicationsAsync();
        await journal.SaveProgrammeDraftAsync(draft, 0, Id(), "operator", Stations);
        Task<AudioMetadata> Bad(string path, CancellationToken token) => throw new InvalidDataException("fixture");
        await using var control = new ProgrammePublicationControl(journal, new ProgrammeMediaPreparation(Bad), Stations);
        var command = Id(); await control.StartAsync(draft.Rule.Id, 1, 0, command, "operator");
        Assert.Equal("Failed", (await Finished(control, command)).State);
        Assert.Null(await control.PublicationAsync(draft.Rule.Id));
        Assert.Empty(await journal.ActiveProgrammePublicationsAsync(Stations));
    }
}
