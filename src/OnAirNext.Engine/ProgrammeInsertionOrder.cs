using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace OnAirNext.Engine;

// A timed folder selects one validated recording reproducibly for each
// occurrence, including after a service restart. A specific file stays alone.
public static class ProgrammeInsertionOrder
{
    public static MediaItem Choose(IEnumerable<MediaItem> source, string occurrenceKey)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(occurrenceKey) || occurrenceKey.Length > 512 || occurrenceKey.Any(char.IsControl))
            throw new ArgumentException("Choose a valid timed occurrence.");
        var items = source.Take(ProgrammeMusicOrder.MaximumTracks + 1).ToArray();
        if (items.Length is < 1 or > ProgrammeMusicOrder.MaximumTracks || items.Any(x => x is null || x.Kind != ContentKind.Program ||
                string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 256 || x.Id.Any(char.IsControl)) ||
            items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Choose distinct validated timed recordings.");
        Array.Sort(items, (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(occurrenceKey));
        var index = (int)(BinaryPrimitives.ReadUInt64BigEndian(hash) % (ulong)items.Length);
        return items[index];
    }
}
