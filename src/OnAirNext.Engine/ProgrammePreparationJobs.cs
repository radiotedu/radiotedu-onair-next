using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OnAirNext.Engine;

public sealed record ProgrammePreparationJob(string Id, string ProgrammeId, string State,
    DateTimeOffset RequestedAt, DateTimeOffset? FinishedAt = null,
    ProgrammePublication? Publication = null, string? Error = null);

// Preparation can decode many files, so it must never run on the pipe handler
// or a station sample loop. Only a single job may consume decoder resources.
// This component is intentionally not exposed as an on-air control until the
// scheduler is integrated and qualified.
public sealed class ProgrammePreparationJobs : IAsyncDisposable
{
    private sealed record Entry(string Fingerprint, ProgrammePreparationJob Snapshot);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _jobs = new(StringComparer.Ordinal);
    private readonly Func<ProgrammeDraft, CancellationToken, Task<PreparedProgramme>> _prepare;
    private readonly Func<PreparedProgramme, long, string, string, Task<ProgrammePublication>> _publish;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _cancel;
    private Task _running = Task.CompletedTask;
    private bool _busy, _disposed;

    public ProgrammePreparationJobs(Func<ProgrammeDraft, CancellationToken, Task<PreparedProgramme>> prepare,
        Func<PreparedProgramme, long, string, string, Task<ProgrammePublication>> publish, TimeProvider? clock = null)
    {
        _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _clock = clock ?? TimeProvider.System;
    }

    public ProgrammePreparationJob Start(ProgrammeDraft draft, long expectedVersion, string command, string actor)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Rule is null || !Guid.TryParse(command, out var parsed) || !Guid.TryParseExact(draft.Rule.Id, "N", out _) ||
            expectedVersion < 0 || expectedVersion == long.MaxValue || string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || actor.Any(char.IsControl))
            throw new ArgumentException("Choose a valid programme preparation request.");
        var id = parsed.ToString("N");
        // Detach mutable lists supplied by a caller before handing work to a background task.
        draft = JsonSerializer.Deserialize<ProgrammeDraft>(JsonSerializer.Serialize(draft)) ?? throw new ArgumentException("The programme is unavailable.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { draft, expectedVersion, actor }))));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jobs.TryGetValue(id, out var previous))
            {
                if (previous.Fingerprint != fingerprint) throw new ArgumentException("This request ID belongs to a different preparation.");
                return previous.Snapshot;
            }
            if (_busy) throw new InvalidOperationException("Another programme is being prepared. Wait for it to finish.");
            if (_jobs.Count >= 64)
            {
                var oldest = _jobs.Values.Where(x => x.Snapshot.FinishedAt is not null).MinBy(x => x.Snapshot.RequestedAt);
                if (oldest is null) throw new InvalidOperationException("The preparation history is full.");
                _jobs.Remove(oldest.Snapshot.Id);
            }
            var job = new ProgrammePreparationJob(id, draft.Rule.Id, "Preparing", _clock.GetUtcNow());
            _jobs.Add(id, new(fingerprint, job));
            _busy = true;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _cancel = cancel;
            _running = Task.Run(async () =>
            {
                var committing = false;
                try
                {
                    var prepared = await _prepare(draft, cancel.Token);
                    lock (_gate)
                    {
                        cancel.Token.ThrowIfCancellationRequested();
                        Set(id, _jobs[id].Snapshot with { State = "Committing" });
                        committing = true;
                    }
                    // Once the durable transaction begins, report its actual result;
                    // cancellation must not claim the publication did not happen.
                    var publication = await _publish(prepared, expectedVersion, id, actor);
                    lock (_gate) Set(id, _jobs[id].Snapshot with { State = "Published", FinishedAt = _clock.GetUtcNow(), Publication = publication });
                }
                catch (OperationCanceledException) when (!committing && cancel.IsCancellationRequested)
                { lock (_gate) Set(id, _jobs[id].Snapshot with { State = "Cancelled", FinishedAt = _clock.GetUtcNow() }); }
                catch (Exception failure)
                { lock (_gate) Set(id, _jobs[id].Snapshot with { State = "Failed", FinishedAt = _clock.GetUtcNow(), Error = "Programme preparation failed (" + failure.GetType().Name + "). Review the source and retry." }); }
                finally
                {
                    lock (_gate) { _busy = false; if (ReferenceEquals(_cancel, cancel)) _cancel = null; }
                    cancel.Dispose();
                }
            });
            return job;
        }
    }

    public ProgrammePreparationJob Get(string command)
    {
        if (!Guid.TryParse(command, out var id)) throw new ArgumentException("Choose a valid preparation request.");
        lock (_gate) return _jobs.TryGetValue(id.ToString("N"), out var entry) ? entry.Snapshot
            : throw new InvalidOperationException("This preparation is unavailable. Review the programme and try again.");
    }

    public ProgrammePreparationJob Cancel(string command)
    {
        if (!Guid.TryParse(command, out var id)) throw new ArgumentException("Choose a valid preparation request.");
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id.ToString("N"), out var entry)) throw new InvalidOperationException("This preparation is unavailable.");
            cancel = entry.Snapshot.State == "Preparing" ? _cancel : null;
        }
        try { cancel?.Cancel(); } catch (ObjectDisposedException) { /* Completed concurrently. */ }
        return Get(command);
    }

    private void Set(string id, ProgrammePreparationJob snapshot) => _jobs[id] = _jobs[id] with { Snapshot = snapshot };

    public async ValueTask DisposeAsync()
    {
        Task running;
        lock (_gate) { if (_disposed) return; _disposed = true; running = _running; }
        _shutdown.Cancel();
        await running;
        _shutdown.Dispose();
    }
}
