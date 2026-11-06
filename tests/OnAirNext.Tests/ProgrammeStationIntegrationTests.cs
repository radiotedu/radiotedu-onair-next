using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammeStationIntegrationTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
    private sealed class Decoder(int blocks, float sample) : IFrameDecoder
    {
        private int _remaining = blocks;
        public Task Ready => Task.CompletedTask;
        public ValueTask<PcmBlock?> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_remaining-- <= 0) return ValueTask.FromResult<PcmBlock?>(null);
            var block = PcmBlock.Rent(); MemoryMarshal.Cast<byte, float>(block.Memory.Span).Fill(sample);
            return ValueTask.FromResult<PcmBlock?>(block);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Output : IProgramOutput
    {
        private int _starts, _blocks;
        public int Starts => Volatile.Read(ref _starts);
        public int Blocks => Volatile.Read(ref _blocks);
        public ConcurrentQueue<string> Metadata { get; } = new();
        public void Start() => Interlocked.Increment(ref _starts);
        public bool Publish(PcmBlock block) { Interlocked.Increment(ref _blocks); return true; }
        public void SetNowPlaying(string artist, string title) => Metadata.Enqueue(artist.Length == 0 ? title : artist + " - " + title);
        public OutputSnapshot Snapshot() => new("fixture", "SourceConnected", 1, 1, Blocks, 0, 0, null, null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static DateTimeOffset Local(int day, int hour = 0, int minute = 0, int second = 0)
        => new(2026, 11, day, hour, minute, second, TimeSpan.FromHours(3));
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static string Folder()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "programme-station-tests", Id());
        Directory.CreateDirectory(path); return path;
    }
    private static Task<AudioMetadata> Probe(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var file = new FileInfo(path);
        return Task.FromResult(new AudioMetadata("Special artist", Path.GetFileNameWithoutExtension(path), 87,
            new SourceLoudness(new string('A', 64), file.Length, file.LastWriteTimeUtc.Ticks, 87, -23, -3, 4, DateTimeOffset.UtcNow)));
    }
    private static Task<AudioMetadata> MeasuredProbe(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var file=new FileInfo(path);
        return Task.FromResult(new AudioMetadata("Special artist",Path.GetFileNameWithoutExtension(path),2.2,
            new SourceLoudness(new string('A',64),file.Length,file.LastWriteTimeUtc.Ticks,2.2,-23,-3,4,DateTimeOffset.UtcNow,
                PlaybackRange:new(0,105600,105600))));
    }
    private static async Task<(ProgrammeRuntimeCoordinator Runtime, ProgrammeDraft Draft)> Prepare(
        EventJournal journal, string folder, Clock clock, bool allDay, bool measured=false)
    {
        var known = new HashSet<string>(["4"], StringComparer.Ordinal); var date = new DateOnly(2026, 11, 10);
        var draft = new ProgrammeDraft(new(Id(), 1, "10 November", ["4"], CalendarRuleKind.SpecialDay,
            date, date, TimeOnly.MinValue, allDay ? TimeOnly.MinValue : new TimeOnly(0, 1), AllDay: allDay,
            StartPolicy: CalendarStartPolicy.FadeAtTime, ReturnPolicy: CalendarReturnPolicy.FadeAtTime), folder,
            DisplayText: "Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz", SuppressStationBranding: true);
        await journal.InitializeDesiredStateAsync(new("4", [], [], AutoStart: false));
        await journal.InitializeProgrammeDraftsAsync(); await journal.InitializeProgrammePublicationsAsync(); await journal.InitializeProgrammePlaybackAsync();
        await journal.SaveProgrammeDraftAsync(draft, 0, Id(), "operator", known);
        Func<string,CancellationToken,Task<AudioMetadata>> probe=measured?MeasuredProbe:Probe;
        await using var media = new ProgrammeMediaPreparation(probe);
        await journal.PublishPreparedProgrammeAsync(await media.PrepareAsync(draft, known), 0, Id(), "operator", known);
        var runtime = new ProgrammeRuntimeCoordinator(journal, known, clock); await runtime.RefreshAsync();
        Assert.True(runtime.Snapshot().Healthy); return (runtime, draft);
    }

    [Fact]
    public async Task APreparedMidnightProgrammeReplacesNormalMusicWithoutReopeningTheOutput()
    {
        var root = Folder(); var media = Path.Combine(root, "music"); Directory.CreateDirectory(media);
        await File.WriteAllTextAsync(Path.Combine(media, "special.opus"), "fixture");
        await using var journal = new EventJournal(Path.Combine(root, "playback.db"));
        var clock = new Clock(Local(9, 23, 59, 58)); var (runtime, _) = await Prepare(journal, media, clock, allDay: true);
        await using (runtime)
        {
            var output = new Output(); var normal = new MediaItem("normal", "normal.opus", "Normal artist", "Normal song");
            var settings = new StationSettings("4", [normal], [], Repeat: true);
            await using var station = new StationEngine("unused", settings, journal, [output], null, null,
                item => new Decoder(1000, item.Id == "normal" ? .1f : .25f), () => Task.CompletedTask, runtime, () => clock.GetUtcNow());
            await station.StartAsync();
            await Until(() => station.Snapshot().CurrentAssetId is { } id && id != "normal");
            Assert.Equal("Running", station.Snapshot().State); Assert.Equal(1, output.Starts);
            Assert.Contains("Gazi Mustafa Kemal Atatürk'ü Saygıyla Anıyoruz", output.Metadata);
            Assert.True(output.Blocks >= 90);
        }
    }

    [Fact]
    public async Task TheSpecialWindowReturnsToTheNormalRotationAtItsEnd()
    {
        var root = Folder(); var media = Path.Combine(root, "music"); Directory.CreateDirectory(media);
        await File.WriteAllTextAsync(Path.Combine(media, "special-a.opus"), "a");
        await File.WriteAllTextAsync(Path.Combine(media, "special-b.opus"), "b");
        await using var journal = new EventJournal(Path.Combine(root, "playback.db"));
        var clock = new Clock(Local(10, 0, 0, 58)); var (runtime, _) = await Prepare(journal, media, clock, allDay: false);
        await using (runtime)
        {
            var output = new Output(); var normal = new MediaItem("normal", "normal.opus", "Normal artist", "Normal song");
            var settings = new StationSettings("4", [normal], [], Repeat: true);
            await using var station = new StationEngine("unused", settings, journal, [output], null, null,
                item => new Decoder(1000, item.Id == "normal" ? .1f : .25f), () => Task.CompletedTask, runtime, () => clock.GetUtcNow());
            await station.StartAsync();
            await Until(() => station.Snapshot().CurrentAssetId is { } id && id != "normal");
            await Until(() => station.Snapshot().CurrentAssetId == "normal");
            Assert.Equal("Running", station.Snapshot().State); Assert.Equal(1, output.Starts);
            Assert.Contains("Normal artist - Normal song", output.Metadata);
            Assert.True(output.Blocks >= 90);
        }
    }

    [Fact]
    public async Task TimedRecordingContinuesPastItsAdmissionWindowAndSpecialMusicResumes()
    {
        var root=Folder();var music=Path.Combine(root,"music");var timed=Path.Combine(root,"timed");
        Directory.CreateDirectory(music);Directory.CreateDirectory(timed);
        await File.WriteAllTextAsync(Path.Combine(music,"special.opus"),"music fixture");
        var recording=Path.Combine(timed,"recording.opus");await File.WriteAllTextAsync(recording,"recording fixture");
        await using var journal=new EventJournal(Path.Combine(root,"playback.db"));
        var clock=new Clock(Local(10,9,4,58));var (runtime,_)=await Prepare(journal,music,clock,allDay:true);
        await using(runtime)
        {
            var known=new HashSet<string>(["4"],StringComparer.Ordinal);var day=new DateOnly(2026,11,10);
            var draft=new ProgrammeDraft(new(Id(),1,"09:05 recording",["4"],CalendarRuleKind.TimedSweeper,
                day,day,new TimeOnly(9,5),new TimeOnly(9,6),StartPolicy:CalendarStartPolicy.FadeAtTime,
                LateToleranceSeconds:1),timed,SourceFile:Path.GetFileName(recording));
            await journal.SaveProgrammeDraftAsync(draft,0,Id(),"operator",known);
            await using var preparation=new ProgrammeMediaPreparation(Probe);
            var prepared=await preparation.PrepareAsync(draft,known);
            await journal.PublishPreparedProgrammeAsync(prepared,0,Id(),"operator",known);await runtime.RefreshAsync();
            Assert.True(runtime.Snapshot().Healthy);
            var output=new Output();var normal=new MediaItem("normal","normal.opus","Normal artist","Normal song");
            await using var station=new StationEngine("unused",new("4",[normal],[],Repeat:true),journal,[output],null,null,
                item=>new Decoder(item.Id==prepared.Items[0].Id?300:1000,.2f),()=>Task.CompletedTask,runtime,()=>clock.GetUtcNow());
            await station.StartAsync();
            await Until(()=>station.Snapshot().CurrentAssetId==prepared.Items[0].Id);
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal(prepared.Items[0].Id,station.Snapshot().CurrentAssetId);
            await Until(()=>station.Snapshot().CurrentAssetId!=prepared.Items[0].Id);
            await Until(()=>EventJournal.ReadEvents(journal.DatabasePath).Any(e=>e.Type=="ended"&&e.ItemId==prepared.Items[0].Id));
            var events=EventJournal.ReadEvents(journal.DatabasePath);
            var started=Assert.Single(events,e=>e.Type=="started"&&e.ItemId==prepared.Items[0].Id);
            var ended=Assert.Single(events,e=>e.Type=="ended"&&e.ItemId==prepared.Items[0].Id);
            Assert.Equal(300L*960,ended.SamplePosition-started.SamplePosition);
            Assert.Equal("Running",station.Snapshot().State);Assert.Equal(1,output.Starts);
        }
    }

    [Fact]
    public async Task AReadyMusicOverlapDoesNotDelayTheTimedRecordingDeadline()
    {
        var root=Folder();var music=Path.Combine(root,"music");var timed=Path.Combine(root,"timed");
        Directory.CreateDirectory(music);Directory.CreateDirectory(timed);
        await File.WriteAllTextAsync(Path.Combine(music,"a.opus"),"a");await File.WriteAllTextAsync(Path.Combine(music,"b.opus"),"b");
        await File.WriteAllTextAsync(Path.Combine(timed,"recording.opus"),"recording");
        await using var journal=new EventJournal(Path.Combine(root,"playback.db"));
        var clock=new Clock(Local(10,9,4,58));var (runtime,_)=await Prepare(journal,music,clock,allDay:true,measured:true);
        await using(runtime)
        {
            var known=new HashSet<string>(["4"],StringComparer.Ordinal);var day=new DateOnly(2026,11,10);
            var draft=new ProgrammeDraft(new(Id(),1,"09:05 recording",["4"],CalendarRuleKind.TimedSweeper,
                day,day,new TimeOnly(9,5),new TimeOnly(9,6),StartPolicy:CalendarStartPolicy.FadeAtTime),timed,SourceFile:"recording.opus");
            await journal.SaveProgrammeDraftAsync(draft,0,Id(),"operator",known);await using var preparation=new ProgrammeMediaPreparation(Probe);
            var prepared=await preparation.PrepareAsync(draft,known);
            await journal.PublishPreparedProgrammeAsync(prepared,0,Id(),"operator",known);await runtime.RefreshAsync();
            var output=new Output();var normal=new MediaItem("normal","normal.opus","Normal","Normal");
            await using var station=new StationEngine("unused",new("4",[normal],[],Repeat:true,CrossfadeSeconds:.5),journal,[output],null,null,
                _=>new Decoder(1000,.2f),()=>Task.CompletedTask,runtime,()=>clock.GetUtcNow());
            await station.StartAsync();await Until(()=>station.Snapshot().CurrentAssetId==prepared.Items[0].Id);
            await Until(()=>EventJournal.ReadEvents(journal.DatabasePath).Any(e=>e.Type=="started"&&e.ItemId==prepared.Items[0].Id));
            var events=EventJournal.ReadEvents(journal.DatabasePath);var timedStart=Assert.Single(events,e=>e.Type=="started"&&e.ItemId==prepared.Items[0].Id);
            Assert.InRange(timedStart.SamplePosition,2L*48000-960,2L*48000+960);
            Assert.Single(events,e=>e.Type=="started"&&e.ProgrammeMusicKey is not null);
            Assert.Equal(1,output.Starts);
        }
    }

    [Fact]
    public async Task AFailedFirstSpecialSourceLeavesNormalAudioRunningAndAdvancesToAnotherSong()
    {
        var root = Folder(); var media = Path.Combine(root, "music"); Directory.CreateDirectory(media);
        await File.WriteAllTextAsync(Path.Combine(media, "special-a.opus"), "a");
        await File.WriteAllTextAsync(Path.Combine(media, "special-b.opus"), "b");
        await using var journal = new EventJournal(Path.Combine(root, "playback.db"));
        var clock = new Clock(Local(9, 23, 59, 58)); var (runtime, _) = await Prepare(journal, media, clock, allDay: true);
        await using (runtime)
        {
            var upcoming = runtime.Snapshot().Stations["4"].Upcoming!; var badId = upcoming.Selection.Item.Id;
            var output = new Output(); var normal = new MediaItem("normal", "normal.opus", "Normal artist", "Normal song");
            var settings = new StationSettings("4", [normal], [], Repeat: true);
            await using var station = new StationEngine("unused", settings, journal, [output], null, null,
                item => item.Id == badId ? throw new IOException("unavailable fixture") : new Decoder(1000, .2f),
                () => Task.CompletedTask, runtime, () => clock.GetUtcNow());
            await station.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((await journal.ProgrammeMusicProgressAsync(upcoming.Occurrence.Key, "4")).NextPosition == 0)
                await Task.Delay(20, timeout.Token);
            Assert.Equal("normal", station.Snapshot().CurrentAssetId);
            clock.Now = Local(10, 0, 0, 1); await runtime.RefreshAsync();
            await Until(() => station.Snapshot().CurrentAssetId is { } id && id != "normal" && id != badId);
            Assert.Equal("Running", station.Snapshot().State); Assert.Equal(1, output.Starts);
            Assert.Single(EventJournal.ReadEvents(journal.DatabasePath), e => e.Type == "decode_failed" && e.ItemId == badId);
        }
    }
}
