using System.Text.Json;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class LibraryTagCaseTests
{
    [Theory]
    [InlineData("{\"ARTIST\":\"Johann Sebastian Bach\"}", "Johann Sebastian Bach")]
    [InlineData("{\"artist\":\"Mor ve Ötesi\"}", "Mor ve Ötesi")]
    [InlineData("{\"Artist\":\"Metallica\"}", "Metallica")]
    [InlineData("{\"ARTIST\":\"One\",\"artist\":\"Two\"}", "")]
    [InlineData("{\"ARTIST\":\"Same\",\"artist\":\"Same\"}", "Same")]
    [InlineData("{\"ARTIST\":null}", "")]
    public void EmbeddedTagKeysIgnoreCaseButConflictingValuesAreNotGuessed(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, LibraryAudioProbe.ReadTag(document.RootElement, "artist"));
    }
}
