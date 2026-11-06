using System.Collections.Frozen;
using System.Globalization;

namespace OnAirNext.Engine;

public sealed record ProgrammeRuntimeSnapshot(bool Healthy, DateTimeOffset UpdatedAtUtc,
    IReadOnlyDictionary<string, ProgrammeRuntimeView> Stations, string? Error);

// All database, calendar and shuffle work stays on one background control lane.
// A station receives only an immutable view, including the next timed deadline.
public sealed class ProgrammeRuntimeCoordinator : IAsyncDisposable
{
    private readonly EventJournal _journal;
    private readonly IReadOnlySet<string> _stations;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private IReadOnlyList<ProgrammePublication> _publications = [];
    private long _sequence = -1;
    private ProgrammeRuntimeSnapshot _snapshot = new(false, default,
        new Dictionary<string, ProgrammeRuntimeView>().ToFrozenDictionary(StringComparer.Ordinal), "Programme state has not been loaded.");
    private Task? _run;
    private bool _disposed;

    public ProgrammeRuntimeCoordinator(EventJournal journal, IEnumerable<string> stations, TimeProvider? clock = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        ArgumentNullException.ThrowIfNull(stations);
        _stations = stations.ToHashSet(StringComparer.Ordinal);
        if (_stations.Count is < 1 or > 8 || _stations.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Choose the installed programme stations.");
        _clock = clock ?? TimeProvider.System;
    }

    public ProgrammeRuntimeSnapshot Snapshot() => Volatile.Read(ref _snapshot);

    public ProgrammeAudioCue? MusicAt(string station, DateTimeOffset now)
    {
        var snapshot = Snapshot();
        if (!snapshot.Healthy || Math.Abs((now - snapshot.UpdatedAtUtc).TotalSeconds) > 30 ||
            !snapshot.Stations.TryGetValue(station, out var view)) return null;
        return ActiveMusic(view, now);
    }

    public bool CanResume(string station, PlayoutCheckpoint checkpoint, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var snapshot = Snapshot();
        if (!snapshot.Healthy || Math.Abs((now - snapshot.UpdatedAtUtc).TotalSeconds) > 30 ||
            checkpoint.StationId != station || checkpoint.Item is null || !checkpoint.HoldsRotation ||
            !snapshot.Stations.TryGetValue(station, out var view)) return false;
        var music = ActiveMusic(view, now);
        if (checkpoint.ProgrammeMusicKey is { } musicKey)
        {
            if (music?.Occurrence.Key != musicKey || checkpoint.Item.Kind != ContentKind.Music || checkpoint.ProgrammeMusicPosition is not { } position || position < 0)
                return false;
            var programme = _publications.FirstOrDefault(p => p.Active && p.Id == music.Occurrence.RuleId &&
                p.Programme.Draft.Rule.Version == music.Occurrence.RuleVersion)?.Programme;
            if (programme is null || programme.Items.Count == 0) return false;
            var cycle = ProgrammeMusicOrder.PrepareCycle(programme.Items, programme.Draft.Order, musicKey, position / programme.Items.Count);
            return cycle[(int)(position % programme.Items.Count)] == checkpoint.Item;
        }
        if (checkpoint.ScheduledOccurrenceKey is not { } key || checkpoint.Item.Kind is not (ContentKind.Program or ContentKind.Sweeper)) return false;
        var parts = key.Split('/');
        if (parts.Length != 4 || parts[2] != station || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;
        var publication = _publications.FirstOrDefault(p => p.Active && p.Id == parts[0] && p.Programme.Draft.Rule.Version == version &&
            p.Programme.Draft.Rule.Kind == CalendarRuleKind.TimedSweeper && p.Programme.Draft.Rule.Stations.Contains(station, StringComparer.Ordinal));
        if (publication is null) return false;
        var due = new DateTimeOffset(ticks, TimeSpan.Zero);
        if (due.Year is < 2 or > 9998 || now < due) return false;
        var occurrence = ProgrammeCalendar.Forecast([publication.Programme.Draft.Rule with { Enabled = true }], _stations,
            due, TimeSpan.FromSeconds(1)).Occurrences.SingleOrDefault(o => o.Key == key);
        if (occurrence is null) return false;
        if (ProgrammeInsertionOrder.Choose(publication.Programme.Items, key) != checkpoint.Item) return false;
        if (now < occurrence.EndsAtUtc) return true;
        // An interrupted recording may finish after its admission window, but
        // must not unexpectedly replay much later or after its owning day ends.
        var duration = checkpoint.Item.DurationSeconds ?? 0;
        return music is not null && due >= music.Occurrence.DueAtUtc && due < music.Occurrence.EndsAtUtc &&
            now < music.Occurrence.EndsAtUtc && double.IsFinite(duration) && duration > 0 && duration <= 3600 &&
            now - due <= TimeSpan.FromSeconds(duration + 120) && now - checkpoint.SavedAtUtc >= TimeSpan.FromSeconds(-30) &&
            now - checkpoint.SavedAtUtc <= TimeSpan.FromMinutes(2);
    }

    private static ProgrammeAudioCue? ActiveMusic(ProgrammeRuntimeView view, DateTimeOffset now)
    {
        var cue = view.Music ?? view.Upcoming;
        return cue is not null && cue.Occurrence.Kind == CalendarRuleKind.SpecialDay &&
            cue.Occurrence.DueAtUtc <= now && now < cue.Occurrence.EndsAtUtc ? cue : null;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_run is not null) throw new InvalidOperationException("Programme monitoring is already running.");
        _run = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await RefreshAsync(_stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { break; }
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, _stop.Token);
            }
        });
    }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refresh.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var sequence = await _journal.ProgrammePublicationSequenceAsync();
            if (sequence != _sequence)
            {
                _publications = await _journal.ActiveProgrammePublicationsAsync(_stations);
                _sequence = sequence;
            }
            var now = _clock.GetUtcNow();
            var forecast = ProgrammeCalendar.Forecast(_publications.Select(p => p.Programme.Draft.Rule with { Enabled = true }),
                _stations, now, TimeSpan.FromHours(24));
            var views = new Dictionary<string, ProgrammeRuntimeView>(StringComparer.Ordinal);
            foreach (var station in _stations)
            {
                token.ThrowIfCancellationRequested();
                var occurrences = forecast.Occurrences.Where(o => o.StationId == station).ToArray();
                var timed = occurrences.Where(o => o.Kind == CalendarRuleKind.TimedSweeper).Select(o => o.Key).ToArray();
                var completed = timed.Length == 0 ? new HashSet<string>(StringComparer.Ordinal)
                    : await _journal.CompletedScheduledOccurrencesAsync(station, timed);
                var windows = occurrences.Where(o => o.Kind == CalendarRuleKind.SpecialDay && o.DueAtUtc <= now && now < o.EndsAtUtc)
                    .OrderByDescending(o => o.Priority).ThenBy(o => o.Key, StringComparer.Ordinal).ToArray();
                if (windows.Length > 1 && windows[0].Priority == windows[1].Priority)
                    throw new InvalidOperationException("Scheduled music windows have conflicting priorities.");
                var position = windows.Length == 0 ? 0 : (await _journal.ProgrammeMusicProgressAsync(windows[0].Key, station)).NextPosition;
                var next = occurrences.Where(o => o.DueAtUtc > now &&
                        (o.Kind != CalendarRuleKind.TimedSweeper || !completed.Contains(o.Key)))
                    .OrderBy(o => o.DueAtUtc).ThenByDescending(o => o.Priority).ThenBy(o => o.Key, StringComparer.Ordinal).FirstOrDefault();
                var nextPosition = next?.Kind == CalendarRuleKind.SpecialDay
                    ? (await _journal.ProgrammeMusicProgressAsync(next.Key, station)).NextPosition : 0;
                views.Add(station, ProgrammeRuntimeView.Create(_publications, _stations, station, now, completed, position, nextPosition));
            }
            Volatile.Write(ref _snapshot, new(true, now, views.ToFrozenDictionary(StringComparer.Ordinal), null));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            Volatile.Write(ref _snapshot, new(false, _clock.GetUtcNow(),
                new Dictionary<string, ProgrammeRuntimeView>().ToFrozenDictionary(StringComparer.Ordinal),
                "Scheduled programme state could not be refreshed (" + failure.GetType().Name + ")."));
        }
        finally { _refresh.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        var cancelling = _stop.CancelAsync();
        _disposed = true;
        await cancelling;
        if (_run is not null) { try { await _run; } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { } }
        await _refresh.WaitAsync(); _refresh.Release(); _refresh.Dispose(); _stop.Dispose();
    }
}
