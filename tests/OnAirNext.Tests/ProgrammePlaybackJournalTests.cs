using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammePlaybackJournalTests
{
    private static string Database()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "scheduled-attempt-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return Path.Combine(directory, "playback.db");
    }
    private static string Key() => Guid.NewGuid().ToString("N") + "/1/4/" + DateTimeOffset.UtcNow.UtcTicks;
    private static PlaybackEvent Event(string key, string attempt, string type) => new(Guid.NewGuid().ToString("N"),
        attempt, "4", type, "remembrance", "", "Remembrance", ContentKind.Program,
        DateTimeOffset.UtcNow, 0, ScheduledOccurrenceKey: key);

    [Fact]
    public async Task AttemptAndCompletionSurviveReopenAndPreventASecondPlay()
    {
        var path = Database(); var key = Key(); var first = Guid.NewGuid().ToString("N");
        var start = Event(key, first, "started");
        await using (var journal = new EventJournal(path))
        {
            await journal.InitializeProgrammePlaybackAsync();
            await journal.AppendAsync(start); await journal.AppendAsync(start);
            Assert.Equal("Playing", (await journal.ScheduledOccurrenceAsync(key, "4"))!.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(Event(key, "other", "started")));
            await journal.AppendAsync(Event(key, first, "interrupted"));
        }
        var second = Guid.NewGuid().ToString("N");
        await using (var journal = new EventJournal(path))
        {
            await journal.InitializeProgrammePlaybackAsync();
            Assert.Equal("Interrupted", (await journal.ScheduledOccurrenceAsync(key, "4"))!.State);
            await journal.AppendAsync(Event(key, second, "started"));
            await journal.AppendAsync(Event(key, second, "ended"));
            var completed = await journal.ScheduledOccurrenceAsync(key, "4");
            Assert.Equal("Completed", completed!.State); Assert.Equal(2, completed.Attempts);
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(Event(key, "third", "started")));
            await Assert.ThrowsAsync<ArgumentException>(() => journal.ScheduledOccurrenceAsync(key, "9"));
        }
        Assert.Equal(4, EventJournal.ReadEvents(path).Count(e => e.ScheduledOccurrenceKey == key));
    }

    [Fact]
    public async Task FailedDecodeCanBeRetriedAndWrongTerminalIsRejectedAtomically()
    {
        var path = Database(); var key = Key(); var first = Guid.NewGuid().ToString("N");
        await using var journal = new EventJournal(path); await journal.InitializeProgrammePlaybackAsync();
        await journal.AppendAsync(Event(key, first, "decode_failed"));
        Assert.Equal("Failed", (await journal.ScheduledOccurrenceAsync(key, "4"))!.State);
        var second = Guid.NewGuid().ToString("N"); await journal.AppendAsync(Event(key, second, "started"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(Event(key, first, "ended")));
        Assert.Equal("Playing", (await journal.ScheduledOccurrenceAsync(key, "4"))!.State);
        await journal.AppendAsync(Event(key, second, "ended"));
        Assert.Equal("Completed", (await journal.ScheduledOccurrenceAsync(key, "4"))!.State);
    }

    [Fact]
    public async Task CrashRecoveryMarksTheInterruptedScheduledAttemptBeforeRestart()
    {
        var path = Database(); var key = Key(); var attempt = Guid.NewGuid().ToString("N");
        var item = new MediaItem("remembrance", "fixture.opus", "", "Remembrance", ContentKind.Program);
        var checkpoint = new PlayoutCheckpoint("4", "Playing", 12, item, attempt, 48000, 96000, true,
            DateTimeOffset.UtcNow, HoldsRotation: true, ScheduledOccurrenceKey: key);
        await using (var journal = new EventJournal(path))
        {
            await journal.InitializeProgrammePlaybackAsync();
            await journal.InitializeDesiredStateAsync(new("4", [], [], AutoStart: false));
            await journal.AppendAsync(Event(key, attempt, "started"), checkpoint);
        }
        await using var reopened = new EventJournal(path); await reopened.InitializeProgrammePlaybackAsync();
        var recovered = await reopened.PrepareRecoveryAsync("4");
        Assert.Equal("RestartPending", recovered!.State); Assert.Equal(key, recovered.ScheduledOccurrenceKey);
        Assert.Equal("Interrupted", (await reopened.ScheduledOccurrenceAsync(key, "4"))!.State);
        await reopened.PrepareRecoveryAsync("4");
        Assert.Single(EventJournal.ReadEvents(path), e => e.Type == "interrupted" && e.ScheduledOccurrenceKey == key);
        var retry = Guid.NewGuid().ToString("N"); await reopened.AppendAsync(Event(key, retry, "started"));
        await reopened.AppendAsync(Event(key, retry, "ended"));
        Assert.Equal("Completed", (await reopened.ScheduledOccurrenceAsync(key, "4"))!.State);
    }
}
