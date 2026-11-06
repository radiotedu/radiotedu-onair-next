namespace OnAirNext.Engine;

public sealed record ProgrammePublicationSummary(string Id, long Version, bool Active, string Name,
    IReadOnlyList<string> Stations, int AudioItems, string? DisplayText, DateTimeOffset UpdatedAtUtc);
public sealed record ProgrammePreparationStatus(string Id, string ProgrammeId, string State,
    DateTimeOffset RequestedAt, DateTimeOffset? FinishedAt, ProgrammePublicationSummary? Publication, string? Error);

// Authenticated control-plane orchestration. Media probing and decoding remain
// serialized on the preparation worker, never on a station or pipe handler.
// Expose this through the console only once autonomous execution is qualified.
public sealed class ProgrammePublicationControl : IAsyncDisposable
{
    private readonly EventJournal _journal;
    private readonly ProgrammeMediaPreparation _media;
    private readonly ProgrammePreparationJobs _jobs;
    private readonly IReadOnlySet<string> _stations;
    public event Action? Changed;

    public ProgrammePublicationControl(EventJournal journal, ProgrammeMediaPreparation media, IEnumerable<string> stations)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        ArgumentNullException.ThrowIfNull(stations);
        _stations = stations.ToHashSet(StringComparer.Ordinal);
        if (_stations.Count is < 1 or > 8 || _stations.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Choose the installed programme stations.");
        _jobs = new((draft, token) => _media.PrepareAsync(draft, _stations, token), async (prepared, version, command, actor) =>
        {
            var publication = await _journal.PublishPreparedProgrammeAsync(prepared, version, command, actor, _stations);
            Changed?.Invoke();
            return publication;
        });
    }

    public async Task<ProgrammePreparationStatus> StartAsync(string draftId, long draftVersion, long publicationVersion,
        string command, string actor)
    {
        var draft = await _journal.ProgrammeDraftAsync(draftId, draftVersion, _stations);
        return Summary(_jobs.Start(draft.Draft, publicationVersion, command, actor));
    }

    public ProgrammePreparationStatus Get(string command) => Summary(_jobs.Get(command));
    public ProgrammePreparationStatus Cancel(string command) => Summary(_jobs.Cancel(command));

    public async Task<ProgrammePublicationSummary?> PublicationAsync(string draftId)
    {
        var value = await _journal.ProgrammePublicationAsync(draftId, _stations);
        return value is null ? null : Summary(value);
    }

    public async Task<ProgrammePublicationSummary> UnpublishAsync(string id, long expectedVersion, string command, string actor)
    {
        var result = await _journal.UnpublishProgrammeAsync(id, expectedVersion, command, actor, _stations);
        Changed?.Invoke(); return Summary(result);
    }

    private static ProgrammePreparationStatus Summary(ProgrammePreparationJob value)
        => new(value.Id, value.ProgrammeId, value.State, value.RequestedAt, value.FinishedAt,
            value.Publication is null ? null : Summary(value.Publication), value.Error);
    private static ProgrammePublicationSummary Summary(ProgrammePublication value)
        => new(value.Id, value.Version, value.Active, value.Programme.Draft.Rule.Name,
            value.Programme.Draft.Rule.Stations, value.Programme.Items.Count, value.Programme.Draft.DisplayText, value.AtUtc);

    public async ValueTask DisposeAsync()
    {
        try { await _jobs.DisposeAsync(); }
        finally { await _media.DisposeAsync(); }
    }
}
