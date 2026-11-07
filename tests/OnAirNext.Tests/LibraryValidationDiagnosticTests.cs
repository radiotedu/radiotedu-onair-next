using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class LibraryValidationDiagnosticTests
{
    [Fact]
    public void DecodingErrorsSurviveLongTagsAndSubsequentLoudnessSummary()
    {
        var diagnostic = new string('T', 3000) + "\n[mp3float] Header missing\n" +
            "[decoder] Error submitting packet: Invalid data found when processing input\n" +
            new string('S', 1200);
        var details = LibraryAudioProbe.FailureDetails(diagnostic);
        Assert.Contains("Header missing", details);
        Assert.Contains("Invalid data found", details);
        Assert.DoesNotContain(new string('T', 20), details);
        Assert.True(details.Length <= 500);
    }

    [Fact]
    public void UnknownFailureRetainsTheBoundedEndOfTheDiagnostic()
    {
        var details = LibraryAudioProbe.FailureDetails(new string('x', 600) + " FINISH");
        Assert.Equal(500, details.Length);
        Assert.EndsWith(" FINISH", details);
    }
}
