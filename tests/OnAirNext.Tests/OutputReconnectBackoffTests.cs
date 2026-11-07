using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public class OutputReconnectBackoffTests
{
    [Fact]
    public void RepeatedFailuresBackOffButAStableConnectionClearsTheOldOutage()
    {
        var retry = new OutputReconnectBackoff();
        Assert.Equal(TimeSpan.FromMilliseconds(400), retry.Next(null));
        Assert.Equal(TimeSpan.FromMilliseconds(800), retry.Next(TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromMilliseconds(1600), retry.Next(null));
        Assert.Equal(TimeSpan.FromMilliseconds(3000), retry.Next(null));
        Assert.Equal(TimeSpan.FromMilliseconds(3000), retry.Next(TimeSpan.FromSeconds(29)));
        Assert.Equal(TimeSpan.FromMilliseconds(400), retry.Next(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromMilliseconds(800), retry.Next(null));
    }
}
