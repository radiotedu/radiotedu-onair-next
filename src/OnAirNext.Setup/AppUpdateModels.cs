namespace OnAirNext.Installation;

public sealed record MaintenanceContract(int ContractVersion,int JournalSchemaVersion,bool RetainsCurrentDataOnRollback)
{
    public string[] JournalFeatures { get; init; } = [];
    // Protocol values also used by the browser; setup deliberately does not load the audio engine.
    public static IReadOnlyList<string> ReaderFeatures { get; } = Array.AsReadOnly(new[]
    {
        "held-rotation-v1",
        "station-queue-v1",
        "programme-automation-v1",
        "crossfade-v1"
    });
    public void RequireCurrentReader()
    {
        if(ContractVersion!=2 || JournalSchemaVersion!=1 || !RetainsCurrentDataOnRollback || JournalFeatures is null ||
            ReaderFeatures.Any(feature=>!JournalFeatures.Contains(feature,StringComparer.Ordinal)))
            throw new InvalidOperationException("This package needs an explicit recovery migration; application-only maintenance requires declared support for current queue, programme and crossfade data in both images.");
    }
}

public sealed record AppUpdateReview(UpdateSwapReview Files,string OperatorSid,string PackageRoot,
    string ConfigurationSha256,string OwnershipSha256,string InitialPlanSha256,uint ServiceProcessId,DateTimeOffset ServiceStartedAtUtc,int JournalSchemaVersion,string? OriginalSource=null,string? ReplacementSource=null,string? PreflightStatusSha256=null);

public sealed record AppUpdateCompletion(string ReviewSha256,UpdateDirection Direction,string ImageSha256,DateTimeOffset VerifiedAtUtc);
public sealed record AppUpdateSnapshot(string ReviewSha256,string Folder,DateTimeOffset CapturedAtUtc,SetupFile[] Files);
