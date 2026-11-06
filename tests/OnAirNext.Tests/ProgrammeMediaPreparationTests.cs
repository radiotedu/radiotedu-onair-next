using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammeMediaPreparationTests
{
    private static readonly IReadOnlySet<string> Stations = new HashSet<string>(["4"], StringComparer.Ordinal);
    private static string Folder()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadioTEDU", "OnAirNext-Dev", "programme-media-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static ProgrammeDraft Draft(string folder, CalendarRuleKind kind = CalendarRuleKind.SpecialDay, string? file = null)
        => new(new(Guid.NewGuid().ToString("N"), 1, "Remembrance", ["4"], kind,
            new(2026, 11, 10), new(2026, 11, 10), new(9, 5), new(10, 0)), folder, SourceFile: file);
    private static Task<AudioMetadata> Probe(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var file = new FileInfo(path);
        var measured = new SourceLoudness(new string('A', 64), file.Length, file.LastWriteTimeUtc.Ticks,
            87, -23, -3, 4, DateTimeOffset.UtcNow);
        return Task.FromResult(new AudioMetadata("Artist", Path.GetFileNameWithoutExtension(path), 87, measured));
    }

    [Fact]
    public async Task MusicAndSpecificTimedRecordingProduceValidatedStationScopedAssets()
    {
        var folder = Folder(); await File.WriteAllTextAsync(Path.Combine(folder, "b.opus"), "audio-b");
        await File.WriteAllTextAsync(Path.Combine(folder, "a.opus"), "audio-a");
        await File.WriteAllTextAsync(Path.Combine(folder, "ignore.txt"), "not audio");
        await using var preparation = new ProgrammeMediaPreparation(Probe);
        var music = await preparation.PrepareAsync(Draft(folder), Stations);
        Assert.Equal(new[] { "a", "b" }, music.Items.Select(x => x.Title));
        Assert.All(music.Items, item => { Assert.Equal(ContentKind.Music, item.Kind); Assert.NotNull(item.Loudness); });
        var timed = await preparation.PrepareAsync(Draft(folder, CalendarRuleKind.TimedSweeper, "b.opus"), Stations);
        var recording = Assert.Single(timed.Items); Assert.Equal(ContentKind.Program, recording.Kind);
        Assert.Equal(87, recording.DurationSeconds); Assert.False(SourcePlaybackRange.AppliesTo(recording.Kind));
        Assert.False(timed.Draft.Rule.Enabled);
    }

    [Fact]
    public async Task ChangedOrMissingFilesNeverProduceAPartialPreparedProgramme()
    {
        var folder = Folder(); var file = Path.Combine(folder, "track.opus"); await File.WriteAllTextAsync(file, "original");
        async Task<AudioMetadata> MutatingProbe(string path, CancellationToken token)
        {
            var measured = await Probe(path, token); await File.AppendAllTextAsync(path, "changed", token); return measured;
        }
        await using var preparation = new ProgrammeMediaPreparation(MutatingProbe);
        await Assert.ThrowsAsync<IOException>(() => preparation.PrepareAsync(Draft(folder), Stations));
        await Assert.ThrowsAsync<IOException>(() => preparation.PrepareAsync(Draft(folder, CalendarRuleKind.TimedSweeper, "missing.opus"), Stations));
    }
}
