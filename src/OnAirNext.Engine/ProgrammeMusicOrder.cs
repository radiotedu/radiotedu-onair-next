using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OnAirNext.Engine;

// Build a bounded cycle off the audio lane. The occurrence key and cycle number
// make the same published cycle reproducible after a service restart.
public static class ProgrammeMusicOrder
{
    public const int MaximumTracks = 4096;

    public static IReadOnlyList<MediaItem> PrepareCycle(IEnumerable<MediaItem> source,
        ProgrammeOrder order, string occurrenceKey, long cycle)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(order) || string.IsNullOrWhiteSpace(occurrenceKey) || occurrenceKey.Length > 512 ||
            occurrenceKey.Any(char.IsControl) || cycle < 0)
            throw new ArgumentException("Choose a valid published occurrence, music order and cycle.");
        var items = source.Take(MaximumTracks + 1).ToArray();
        if (items.Length is < 1 or > MaximumTracks || items.Any(x => x is null ||
                string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 256 || x.Id.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(x.Path) || x.Kind != ContentKind.Music) ||
            items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Choose 1–4,096 distinct validated music assets.");
        if (order == ProgrammeOrder.FolderOrder) return Array.AsReadOnly(items);
        // With two tracks, preserving the first cycle's order is the only way
        // to avoid immediately repeating the final track at every boundary.
        if (items.Length == 2) cycle = 0;
        var shuffled = Shuffle(items, occurrenceKey, cycle);
        if (items.Length > 2 && cycle > 0 && shuffled[0].Id == Shuffle(items, occurrenceKey, cycle - 1)[^1].Id)
            (shuffled[0], shuffled[1]) = (shuffled[1], shuffled[0]);
        return Array.AsReadOnly(shuffled);
    }

    private static MediaItem[] Shuffle(MediaItem[] items, string occurrenceKey, long cycle)
    {
        var prefix = occurrenceKey + "\n" + cycle.ToString(CultureInfo.InvariantCulture) + "\n";
        return items.Select(item => (Item: item,
                Key: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix + item.Id)))))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ThenBy(pair => pair.Item.Id, StringComparer.Ordinal)
            .Select(pair => pair.Item).ToArray();
    }
}
