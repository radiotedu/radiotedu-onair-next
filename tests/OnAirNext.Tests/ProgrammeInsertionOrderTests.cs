using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammeInsertionOrderTests
{
    [Fact]
    public void TimedFolderSelectionIsStableAcrossEnumerationOrderAndRejectsNonProgrammeAudio()
    {
        var items = Enumerable.Range(0, 3).Select(i => new MediaItem("clip-" + i, i + ".opus", "", "Clip", ContentKind.Program)).ToArray();
        var key = Guid.NewGuid().ToString("N") + "/1/4/" + DateTimeOffset.UtcNow.UtcTicks;
        var first = ProgrammeInsertionOrder.Choose(items, key);
        Assert.Equal(first, ProgrammeInsertionOrder.Choose(items.Reverse(), key));
        Assert.Contains(first, items);
        Assert.Throws<ArgumentException>(() => ProgrammeInsertionOrder.Choose([items[0] with { Kind = ContentKind.Music }], key));
        Assert.Throws<ArgumentException>(() => ProgrammeInsertionOrder.Choose([items[0], items[0]], key));
    }
}
