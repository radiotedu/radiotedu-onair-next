using System.Security.Cryptography;
using System.Text;

namespace OnAirNext.Engine;

public sealed record PreparedProgramme(ProgrammeDraft Draft, IReadOnlyList<MediaItem> Items);

// Run on a serialized background lane, never in the station sample loop. A
// failed or incomplete scan returns no prepared programme to the publisher.
public sealed class ProgrammeMediaPreparation : IAsyncDisposable
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".aif", ".aiff", ".wma", ".mp4" };
    private readonly LibraryAudioProbe? _audio;
    private readonly Func<string, CancellationToken, Task<AudioMetadata>> _probe;

    public ProgrammeMediaPreparation(string ffmpeg)
    {
        _audio = new LibraryAudioProbe(ffmpeg); _probe = _audio.VerifyAsync;
    }
    internal ProgrammeMediaPreparation(Func<string, CancellationToken, Task<AudioMetadata>> probe)
        => _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public async Task<PreparedProgramme> PrepareAsync(ProgrammeDraft draft, IReadOnlySet<string> stations, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var normalized = draft.Normalize(stations);
        var root = normalized.SourceFolder;
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The programme audio folder is unavailable.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Programme folders cannot be links.");
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(root); var directories = 0;
        while (pending.TryPop(out var directory))
        {
            if (++directories > 8192) throw new IOException("The programme contains too many folders.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) { if (normalized.SourceFile is null) pending.Push(path); continue; }
                if (!Extensions.Contains(Path.GetExtension(path))) continue;
                if (normalized.SourceFile is not null && !Path.GetFileName(path).Equals(normalized.SourceFile, StringComparison.OrdinalIgnoreCase)) continue;
                if (files.Count >= ProgrammeMusicOrder.MaximumTracks) throw new IOException("The programme contains too many audio files.");
                files.Add(path);
            }
        }
        if (files.Count == 0 || normalized.SourceFile is not null && files.Count != 1)
            throw new IOException("The programme audio selection is empty or ambiguous.");
        var items = new List<MediaItem>(files.Count);
        foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested(); var info = new FileInfo(path);
            var length = info.Length; var modified = info.LastWriteTimeUtc.Ticks;
            var metadata = await _probe(path, token);
            metadata.Loudness?.Validate();
            if (metadata.Loudness is null || metadata.Loudness.Bytes != length || metadata.Loudness.ModifiedTicks != modified ||
                !double.IsFinite(metadata.Seconds) || metadata.Seconds <= 0 || metadata.Seconds > 604800)
                throw new InvalidDataException("Programme audio has no matching completed validation.");
            info.Refresh();
            if (!info.Exists || info.Length != length || info.LastWriteTimeUtc.Ticks != modified)
                throw new IOException("Programme audio changed during validation; prepare the programme again.");
            var relative = Path.GetRelativePath(root, path);
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.Rule.Id + "\n" + relative.ToUpperInvariant())));
            var kind = normalized.Rule.Kind == CalendarRuleKind.SpecialDay ? ContentKind.Music : ContentKind.Program;
            if (kind == ContentKind.Music && metadata.Loudness.PlaybackRange is { HasSignal: false })
                throw new InvalidDataException("A selected music file is entirely below the broadcast silence threshold.");
            items.Add(new(id, path, metadata.Artist, metadata.Title, kind, metadata.Seconds, metadata.Loudness));
        }
        return new(normalized, items.AsReadOnly());
    }

    public ValueTask DisposeAsync() => _audio?.DisposeAsync() ?? ValueTask.CompletedTask;
}
