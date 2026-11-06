namespace OnAirNext.Engine;

public sealed record ProgrammePublication(PreparedProgramme Programme, long Version, bool Active,
    string Actor, DateTimeOffset AtUtc)
{
    public string Id => Programme.Draft.Rule.Id;
}

internal static class ProgrammePublicationValidation
{
    public static PreparedProgramme Normalize(PreparedProgramme prepared, IReadOnlySet<string> stations, bool checkFiles)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(prepared.Draft);
        var draft = prepared.Draft.Normalize(stations);
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(draft.SourceFolder)!), draft.SourceFolder,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Choose a programme folder rather than an entire drive.");
        if (prepared.Items is null || prepared.Items.Count is < 1 or > ProgrammeMusicOrder.MaximumTracks)
            throw new ArgumentException("Prepare a bounded, nonempty programme before publishing.");
        var kind = draft.Rule.Kind == CalendarRuleKind.SpecialDay ? ContentKind.Music : ContentKind.Program;
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var ids = new HashSet<string>(StringComparer.Ordinal); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<MediaItem>(prepared.Items.Count);
        foreach (var item in prepared.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 128 || item.Id.Any(char.IsControl) ||
                !ids.Add(item.Id) || item.Kind != kind || item.Artist is null || item.Artist.Length > 128 ||
                string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 256 || item.Artist.Any(char.IsControl) || item.Title.Any(char.IsControl) ||
                item.DurationSeconds is not { } duration || !double.IsFinite(duration) || duration <= 0 || duration > 604800 || item.Loudness is null ||
                Math.Abs(item.Loudness.Seconds - duration) > 0.01)
                throw new ArgumentException("The prepared programme contains invalid or duplicate audio metadata.");
            item.Loudness.Validate();
            var path = LocalMediaPath.Normalize(item.Path);
            var relative = Path.GetRelativePath(draft.SourceFolder, path);
            if (relative == "." || Path.IsPathFullyQualified(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) || !paths.Add(path) ||
                draft.SourceFile is not null && (!Path.GetFileName(path).Equals(draft.SourceFile, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetDirectoryName(path), draft.SourceFolder, pathComparison)))
                throw new ArgumentException("Prepared audio must remain inside its selected programme folder.");
            if (checkFiles)
            {
                var directory = draft.SourceFolder;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Programme folders cannot be links.");
                var relativeDirectory = Path.GetDirectoryName(relative);
                if (!string.IsNullOrEmpty(relativeDirectory))
                    foreach (var part in relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                    {
                        directory = Path.Combine(directory, part);
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Programme folders cannot be links.");
                    }
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != item.Loudness.Bytes ||
                    info.LastWriteTimeUtc.Ticks != item.Loudness.ModifiedTicks)
                    throw new IOException("A prepared programme file changed or became unavailable. Prepare it again.");
            }
            items.Add(item with { Path = path });
        }
        if (draft.SourceFile is not null && items.Count != 1)
            throw new ArgumentException("The selected timed recording must contain exactly one file.");
        return new(draft, items.AsReadOnly());
    }
}
