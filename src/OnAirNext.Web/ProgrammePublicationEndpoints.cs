namespace OnAirNext.Web;

internal static class ProgrammePublicationEndpoints
{
    public static void MapProgrammePublication(this WebApplication app)
    {
        app.MapGet("/api/programmes/publication", async (string engine, string id, CancellationToken token) =>
            Results.Json(await EngineRpc.SendAsync(engine, new { Id = Guid.NewGuid().ToString("N"), Action = "programme-publication",
                DraftId = id }, token)));
        app.MapGet("/api/programmes/preparation", async (string engine, string job, CancellationToken token) =>
            Results.Json(await EngineRpc.SendAsync(engine, new { Id = Guid.NewGuid().ToString("N"), Action = "programme-preparation",
                ProgrammeJobId = job }, token)));
        app.MapPost("/api/programmes/prepare", async (PrepareProgrammeRequest request, CancellationToken token) =>
            Results.Json(await EngineRpc.SendAsync(request.Engine, new { request.Id, Action = "programme-prepare", request.DraftId,
                SourceVersion = request.DraftVersion, ExpectedVersion = request.PublicationVersion }, token)));
        app.MapPost("/api/programmes/cancel", async (CancelProgrammeRequest request, CancellationToken token) =>
            Results.Json(await EngineRpc.SendAsync(request.Engine, new { request.Id, Action = "programme-preparation-cancel",
                ProgrammeJobId = request.JobId }, token)));
        app.MapPost("/api/programmes/unpublish", async (UnpublishProgrammeRequest request, CancellationToken token) =>
            Results.Json(await EngineRpc.SendAsync(request.Engine, new { request.Id, Action = "programme-unpublish", request.DraftId,
                ExpectedVersion = request.PublicationVersion }, token)));
    }

    private sealed record PrepareProgrammeRequest(string Engine, string Id, string DraftId, long DraftVersion, long PublicationVersion);
    private sealed record CancelProgrammeRequest(string Engine, string Id, string JobId);
    private sealed record UnpublishProgrammeRequest(string Engine, string Id, string DraftId, long PublicationVersion);
}
