using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammeMusicJournalTests
{
    private static string Id() => Guid.NewGuid().ToString("N");
    private static string Key() => Id() + "/1/4/" + DateTimeOffset.UtcNow.UtcTicks;
    private static string Database()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "programme-music-tests", Id());
        Directory.CreateDirectory(folder); return Path.Combine(folder, "playback.db");
    }
    private static PlaybackEvent Event(string key, long position, string attempt, string type, string? previous = null)
        => new(Id(), attempt, "4", type, "song-" + position, "Artist", "Song", ContentKind.Music,
            DateTimeOffset.UtcNow, 0, PreviousOccurrenceId: previous, ProgrammeMusicKey: key, ProgrammeMusicPosition: position);

    [Fact]
    public async Task OverlappingMusicAttemptsAdvanceOnceAndCanRetryTheInterruptedLastTrack()
    {
        var path = Database(); var key = Key(); var first = Id(); var second = Id();
        await using (var journal = new EventJournal(path))
        {
            await journal.InitializeProgrammePlaybackAsync();
            var start = Event(key, 0, first, "started"); await journal.AppendAsync(start); await journal.AppendAsync(start);
            await journal.AppendAsync(Event(key, 1, second, "started"));
            var overlap = await journal.ProgrammeMusicProgressAsync(key, "4");
            Assert.Equal(2, overlap.NextPosition); Assert.Equal(2, overlap.PlayingAttempts);
            await journal.AppendAsync(Event(key, 0, first, "ended"));
            await journal.AppendAsync(Event(key, 1, second, "interrupted"));
        }
        await using var reopened = new EventJournal(path); await reopened.InitializeProgrammePlaybackAsync();
        var retry = Id(); await reopened.AppendAsync(Event(key, 1, retry, "started", second));
        var recovered = await reopened.ProgrammeMusicProgressAsync(key, "4");
        Assert.Equal(2, recovered.NextPosition); Assert.Equal(1, recovered.PlayingAttempts);
        await reopened.AppendAsync(Event(key, 1, retry, "ended"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AppendAsync(Event(key, 1, Id(), "started", second)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AppendAsync(Event(key, 1, Id(), "started")));
        await reopened.AppendAsync(Event(key, 2, Id(), "decode_failed"));
        Assert.Equal(3, (await reopened.ProgrammeMusicProgressAsync(key, "4")).NextPosition);
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.ProgrammeMusicProgressAsync(key, "9"));
    }

    [Fact]
    public async Task CrashDuringOverlapClosesBothAttemptsAndRetriesOnlyTheLatest()
    {
        var path = Database(); var key = Key(); var first = Id(); var second = Id();
        PlayoutCheckpoint Checkpoint(string attempt, long position) => new("4", "Playing", 12,
            new MediaItem("song-" + position, "fixture.opus", "Artist", "Song", ContentKind.Music),
            attempt, 1000, 100000 + position, true, DateTimeOffset.UtcNow, HoldsRotation: true,
            ProgrammeMusicKey: key, ProgrammeMusicPosition: position);
        await using (var journal = new EventJournal(path))
        {
            await journal.InitializeProgrammePlaybackAsync();
            await journal.InitializeDesiredStateAsync(new("4", [], [], AutoStart: false));
            await journal.AppendAsync(Event(key, 0, first, "started"), Checkpoint(first, 0));
            await journal.AppendAsync(Event(key, 1, second, "started") with { OverlapWithOccurrenceId = first }, Checkpoint(second, 1));
        }
        await using var reopened = new EventJournal(path); await reopened.InitializeProgrammePlaybackAsync();
        var saved = await reopened.PrepareRecoveryAsync("4");
        Assert.Equal(second, saved!.OccurrenceId);
        Assert.Equal(0, (await reopened.ProgrammeMusicProgressAsync(key, "4")).PlayingAttempts);
        Assert.Equal(2, EventJournal.ReadEvents(path).Count(e => e.Type == "interrupted" && e.ProgrammeMusicKey == key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AppendAsync(Event(key, 0, Id(), "started", first)));
        await reopened.AppendAsync(Event(key, 1, Id(), "started", second));
        Assert.Equal(2, (await reopened.ProgrammeMusicProgressAsync(key, "4")).NextPosition);
    }

    [Fact]
    public async Task CrashRecoveryRetainsTheUnfinishedMusicPositionWithoutAdvancingTwice()
    {
        var path = Database(); var key = Key(); var attempt = Id();
        var song = new MediaItem("song-0", "fixture.opus", "Artist", "Song", ContentKind.Music);
        var checkpoint = new PlayoutCheckpoint("4", "Playing", 12, song, attempt, 48000, 96000, true,
            DateTimeOffset.UtcNow, HoldsRotation: true, ProgrammeMusicKey: key, ProgrammeMusicPosition: 0);
        await using (var journal = new EventJournal(path))
        {
            await journal.InitializeProgrammePlaybackAsync();
            await journal.InitializeDesiredStateAsync(new("4", [], [], AutoStart: false));
            await journal.AppendAsync(Event(key, 0, attempt, "started"), checkpoint);
        }
        await using var reopened = new EventJournal(path); await reopened.InitializeProgrammePlaybackAsync();
        var saved = await reopened.PrepareRecoveryAsync("4");
        Assert.Equal("RestartPending", saved!.State); Assert.Equal(key, saved.ProgrammeMusicKey);
        Assert.Equal(0, saved.ProgrammeMusicPosition);
        await reopened.PrepareRecoveryAsync("4");
        Assert.Single(EventJournal.ReadEvents(path), e => e.Type == "interrupted" && e.ProgrammeMusicKey == key);
        var retry = Id(); await reopened.AppendAsync(Event(key, 0, retry, "started", attempt));
        await reopened.AppendAsync(Event(key, 0, retry, "ended"));
        Assert.Equal(1, (await reopened.ProgrammeMusicProgressAsync(key, "4")).NextPosition);
    }

    [Fact]
    public async Task AnExpiredSpecialRecoveryKeepsTheOrdinaryRotationPosition()
    {
        var path = Database(); var key = Key(); var attempt = Id();
        var song = new MediaItem("song-0", "fixture.opus", "Artist", "Song", ContentKind.Music);
        var checkpoint = new PlayoutCheckpoint("4", "Playing", 12, song, attempt, 48000, 96000, true,
            DateTimeOffset.UtcNow, HoldsRotation: true, ProgrammeMusicKey: key, ProgrammeMusicPosition: 0);
        await using var journal = new EventJournal(path);
        await journal.InitializeProgrammePlaybackAsync();
        await journal.InitializeDesiredStateAsync(new("4", [], [], AutoStart: false));
        await journal.AppendAsync(Event(key, 0, attempt, "started"), checkpoint);
        await journal.PrepareRecoveryAsync("4");
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.DeferUnavailableProgrammeRecoveryAsync("4", "wrong"));
        var saved = await journal.DeferUnavailableProgrammeRecoveryAsync("4", attempt);
        Assert.Equal("BetweenItems", saved.State); Assert.Equal(12, saved.RotationIndex); Assert.Null(saved.Item);
        Assert.Null(saved.ProgrammeMusicKey); Assert.Equal(1, (await journal.ProgrammeMusicProgressAsync(key, "4")).NextPosition);
        Assert.Single(EventJournal.ReadEvents(path), e => e.Type == "recovery_deferred" && e.ProgrammeMusicKey == key);
    }

    [Fact]
    public async Task ADecoderFailureBeforeTheFirstSampleSkipsOnlyThatPosition()
    {
        var path = Database(); var key = Key(); var attempt = Id();
        await using var journal = new EventJournal(path); await journal.InitializeProgrammePlaybackAsync();
        await journal.AppendAsync(Event(key, 0, attempt, "decode_failed"));
        await journal.AppendAsync(Event(key, 0, attempt, "decode_failed"));
        Assert.Equal(1, (await journal.ProgrammeMusicProgressAsync(key, "4")).NextPosition);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(Event(key, 0, Id(), "started")));
        await journal.AppendAsync(Event(key, 1, Id(), "started"));
        Assert.Equal(2, (await journal.ProgrammeMusicProgressAsync(key, "4")).NextPosition);
    }

    [Fact]
    public async Task SuppressedSpecialMusicPreservesBrandingCadenceWhileOptionalBrandingCanAdvanceIt()
    {
        var key = Key(); var first = Id(); var second = Id();
        await using var journal = new EventJournal(Database()); await journal.InitializeProgrammePlaybackAsync();
        await journal.AppendAsync(Event(key, 0, first, "started") with { SuppressStationBranding = true });
        await journal.AppendAsync(Event(key, 0, first, "ended") with { SuppressStationBranding = true });
        Assert.Equal(0, (await journal.BrandingProgressAsync("4")).CompletedMusic);
        await journal.AppendAsync(Event(key, 1, second, "started"));
        await journal.AppendAsync(Event(key, 1, second, "ended"));
        Assert.Equal(1, (await journal.BrandingProgressAsync("4")).CompletedMusic);
    }
}
