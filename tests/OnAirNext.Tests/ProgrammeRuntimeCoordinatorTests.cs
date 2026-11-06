using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammeRuntimeCoordinatorTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static DateTimeOffset Local(int day, int hour = 0, int minute = 0, int second = 0)
        => new(2026, 11, day, hour, minute, second, TimeSpan.FromHours(3));
    private static string Folder()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "programme-runtime-tests", Id());
        Directory.CreateDirectory(path); return path;
    }
    private static Task<AudioMetadata> Probe(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var file = new FileInfo(path);
        return Task.FromResult(new AudioMetadata("Artist", Path.GetFileNameWithoutExtension(path), 87,
            new SourceLoudness(new string('A', 64), file.Length, file.LastWriteTimeUtc.Ticks, 87, -23, -3, 4, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task ReopenKeepsShuffleProgressAndCompletedRecordingWithoutTargetingOtherStations()
    {
        var root = Folder(); var musicFolder = Path.Combine(root, "music"); var signalFolder = Path.Combine(root, "signal");
        Directory.CreateDirectory(musicFolder); Directory.CreateDirectory(signalFolder);
        await File.WriteAllTextAsync(Path.Combine(musicFolder, "a.opus"), "a");
        await File.WriteAllTextAsync(Path.Combine(musicFolder, "b.opus"), "b");
        await File.WriteAllTextAsync(Path.Combine(signalFolder, "signal.opus"), "signal");
        var day = new DateOnly(2026, 11, 10); var known = new HashSet<string>(["4", "9"], StringComparer.Ordinal);
        var music = new ProgrammeDraft(new(Id(), 1, "10 November", ["4"], CalendarRuleKind.SpecialDay,
            day, day, TimeOnly.MinValue, TimeOnly.MinValue, AllDay: true, StartPolicy: CalendarStartPolicy.FadeAtTime,
            ReturnPolicy: CalendarReturnPolicy.FadeAtTime), musicFolder, DisplayText: "Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz",
            SuppressStationBranding: true);
        var insertion = new ProgrammeDraft(new(Id(), 1, "09:05", ["4"], CalendarRuleKind.TimedSweeper,
            day, day, new(9, 5), new(9, 6), StartPolicy: CalendarStartPolicy.FadeAtTime), signalFolder, SourceFile: "signal.opus");
        var database = Path.Combine(root, "playback.db"); var clock = new Clock(Local(9, 23, 59, 50));
        await using (var journal = new EventJournal(database))
        {
            await journal.InitializeProgrammeDraftsAsync(); await journal.InitializeProgrammePublicationsAsync(); await journal.InitializeProgrammePlaybackAsync();
            await journal.SaveProgrammeDraftAsync(music, 0, Id(), "operator", known);
            await journal.SaveProgrammeDraftAsync(insertion, 0, Id(), "operator", known);
            await using var preparation = new ProgrammeMediaPreparation(Probe);
            await journal.PublishPreparedProgrammeAsync(await preparation.PrepareAsync(music, known), 0, Id(), "operator", known);
            await journal.PublishPreparedProgrammeAsync(await preparation.PrepareAsync(insertion, known), 0, Id(), "operator", known);
            await using var runtime = new ProgrammeRuntimeCoordinator(journal, known, clock);
            await runtime.RefreshAsync(); var before = runtime.Snapshot(); Assert.True(before.Healthy);
            Assert.NotNull(before.Stations["4"].Upcoming); Assert.Null(before.Stations["9"].Upcoming);
            clock.Now = Local(10); await runtime.RefreshAsync();
            var first = runtime.Snapshot().Stations["4"].Music!;
            Assert.Equal(0, first.Selection.ProgrammeMusicPosition);
            var item = first.Selection.Item;
            await journal.AppendAsync(new(Id(), Id(), "4", "started", item.Id, item.Artist, item.Title, item.Kind,
                clock.GetUtcNow(), 0, ProgrammeMusicKey: first.Occurrence.Key, ProgrammeMusicPosition: 0));
            await runtime.RefreshAsync(); Assert.Equal(1, runtime.Snapshot().Stations["4"].Music!.Selection.ProgrammeMusicPosition);
            clock.Now = Local(10, 9, 5); await runtime.RefreshAsync();
            var signal = runtime.Snapshot().Stations["4"].DueInsertion!; var attempt = Id(); var clip = signal.Selection.Item;
            var interrupted = new PlayoutCheckpoint("4", "RestartPending", 0, clip, attempt, 48000, 96000, true,
                Local(10, 9, 6), HoldsRotation: true, ScheduledOccurrenceKey: signal.Occurrence.Key);
            clock.Now = Local(10, 9, 6, 15); await runtime.RefreshAsync();
            Assert.True(runtime.CanResume("4", interrupted, clock.GetUtcNow()));
            clock.Now = Local(10, 9, 10); await runtime.RefreshAsync();
            Assert.False(runtime.CanResume("4", interrupted, clock.GetUtcNow()));
            clock.Now = Local(10, 9, 5); await runtime.RefreshAsync();
            await journal.AppendAsync(new(Id(), attempt, "4", "started", clip.Id, clip.Artist, clip.Title, clip.Kind,
                clock.GetUtcNow(), 0, ScheduledOccurrenceKey: signal.Occurrence.Key));
            await journal.AppendAsync(new(Id(), attempt, "4", "ended", clip.Id, clip.Artist, clip.Title, clip.Kind,
                clock.GetUtcNow(), 1, ScheduledOccurrenceKey: signal.Occurrence.Key));
            await runtime.RefreshAsync(); Assert.Null(runtime.Snapshot().Stations["4"].DueInsertion);
        }
        clock.Now = Local(10, 9, 5, 30);
        await using (var reopened = new EventJournal(database))
        {
            await reopened.InitializeProgrammeDraftsAsync(); await reopened.InitializeProgrammePublicationsAsync(); await reopened.InitializeProgrammePlaybackAsync();
            await using var runtime = new ProgrammeRuntimeCoordinator(reopened, known, clock); await runtime.RefreshAsync();
            Assert.True(runtime.Snapshot().Healthy);
            Assert.Null(runtime.Snapshot().Stations["4"].DueInsertion);
            Assert.Equal(1, runtime.Snapshot().Stations["4"].Music!.Selection.ProgrammeMusicPosition);
            await reopened.UnpublishProgrammeAsync(music.Rule.Id, 1, Id(), "operator", known);
            await runtime.RefreshAsync(); Assert.Null(runtime.Snapshot().Stations["4"].Music);
        }
    }
}
