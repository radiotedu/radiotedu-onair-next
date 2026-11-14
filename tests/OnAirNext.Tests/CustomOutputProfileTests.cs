using OnAirNext.Engine;
using Xunit;
namespace OnAirNext.Tests;
public sealed class CustomOutputProfileTests
{
    private static readonly string[] Ids=["1","2","4","5","8","9","10","11"];
    private static ProductionOutputConfiguration Profile()=>new(1,Ids.Select(id=>new StationOutputConfiguration(id,id=="4"?[new OutputSettings("pop-fixture","127.0.0.1",18000,"/next-pop-fixture",Guid.NewGuid().ToString("N"),Commissioned:true,Information:new("Fixture Pop","Synthetic qualification","Test","https://example.invalid",false))]:[])).ToArray(),ProductionOutputRoster.Custom);
    [Fact] public void CustomWizardProfileRetainsAllStationGroupsAndExplicitCommissioning()
    {
        var profile=Profile();Assert.Same(profile,profile.Validate(Ids));
        var output=Assert.Single(profile.Stations.Single(station=>station.StationId=="4").Outputs);
        Assert.Throws<ArgumentException>(()=>output.Validate());
        Assert.Throws<ArgumentException>(()=>(output with {Information=null}).ValidateCommissioned());
        Assert.Throws<ArgumentException>(()=>profile.Validate(["4"]));
    }
    [Fact] public void DuplicateSourceOwnersAndExternalLanguageMountsAreRejected()
    {
        var profile=Profile();var output=profile.Stations.Single(station=>station.StationId=="4").Outputs[0];
        var duplicate=profile with {Stations=profile.Stations.Select(station=>station.StationId=="5"?station with {Outputs=[output with {Id="other"}]}:station).ToArray()};
        Assert.Throws<ArgumentException>(()=>duplicate.Validate(Ids));
        var external=profile with {Stations=profile.Stations.Select(station=>station.StationId=="4"?station with {Outputs=[output with {Mount="/en"}]}:station).ToArray()};
        Assert.Throws<ArgumentException>(()=>external.Validate(Ids));
    }
}
