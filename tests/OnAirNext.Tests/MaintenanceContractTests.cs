using System.Text.Json;
using OnAirNext.Installation;
using Xunit;

namespace OnAirNext.Tests;

public sealed class MaintenanceContractTests
{
    private static MaintenanceContract Current()=>new(2,1,true){JournalFeatures=[..MaintenanceContract.ReaderFeatures]};
    [Fact]
    public void CurrentReaderDeclarationSurvivesPackageSerialization()
    {
        var roundtrip=JsonSerializer.Deserialize<MaintenanceContract>(JsonSerializer.Serialize(Current()))!;
        roundtrip.RequireCurrentReader();
    }
    [Fact]
    public void EveryRecoveryFeatureMustBeSupportedByBothMaintenanceImages()
    {
        foreach(var missing in MaintenanceContract.ReaderFeatures)
            Assert.Throws<InvalidOperationException>(()=>(Current() with{JournalFeatures=MaintenanceContract.ReaderFeatures.Where(x=>x!=missing).ToArray()}).RequireCurrentReader());
    }
    [Fact]
    public void LegacyOrIncompatibleContractsFailClosed()
    {
        foreach(var contract in new[]{new MaintenanceContract(1,1,true),Current() with{ContractVersion=1},Current() with{JournalSchemaVersion=2},Current() with{RetainsCurrentDataOnRollback=false},Current() with{JournalFeatures=[]}})
            Assert.Throws<InvalidOperationException>(()=>contract.RequireCurrentReader());
    }
}
