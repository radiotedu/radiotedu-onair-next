namespace OnAirNext.Engine;

public sealed partial class StationEngine
{
    private readonly HashSet<string> _completedProgrammeInsertions = new(StringComparer.Ordinal);
    private readonly Queue<string> _completedProgrammeOrder = new();
    private readonly HashSet<(string Key, long Position)> _failedProgrammeTracks = [];
    private string? _failedProgrammeKey;
    private CalendarOccurrence? _currentProgrammeWindow;
    private PreparedSource? _programmeSource;
    private ProgrammeAudioCue? _programmeCue;
    private (string? Window, long? Position, string? Due, string? Upcoming, bool Healthy) _lastProgrammePlan;

    private void RememberProgrammeInsertion(string key)
    {
        if (!_completedProgrammeInsertions.Add(key)) return;
        _completedProgrammeOrder.Enqueue(key);
        if (_completedProgrammeOrder.Count > ProgrammeCalendar.MaximumOccurrences)
            _completedProgrammeInsertions.Remove(_completedProgrammeOrder.Dequeue());
    }

    private DateTimeOffset ProgrammeTime(long position)
        => _anchor == default ? _utcNow() : _anchor.AddSeconds((double)position / AudioFormat.SampleRate);

    private ProgrammeRuntimeView? ProgrammeView(DateTimeOffset now)
    {
        if (_programmes is null) return null;
        var snapshot = _programmes.Snapshot();
        return snapshot.Healthy && Math.Abs((now - snapshot.UpdatedAtUtc).TotalSeconds) <= 30 &&
            snapshot.Stations.TryGetValue(_settings.Id, out var view) ? view : null;
    }

    private PlayoutSelection? NextProgrammeMusic()
    {
        var currentSelection = _selection;
        if (currentSelection is null || currentSelection.ProgrammeMusicKey is null && currentSelection.ScheduledOccurrenceKey is null) return null;
        var now = ProgrammeTime(_preparedSamples); var view = ProgrammeView(now);
        var music = view?.Music;
        if (music is null || music.Occurrence.DueAtUtc > now || now >= music.Occurrence.EndsAtUtc) return null;
        foreach (var cue in new[] { music, view!.FollowingMusic })
        {
            if (cue?.Selection.ProgrammeMusicKey is not { } key || cue.Selection.ProgrammeMusicPosition is not { } position ||
                _failedProgrammeTracks.Contains((key, position))) continue;
            if (currentSelection.ProgrammeMusicKey == key && currentSelection.ProgrammeMusicPosition is { } current && position <= current) continue;
            return cue.Selection;
        }
        return null;
    }

    private void MarkProgrammeSourceFailed(PlayoutSelection? selection)
    {
        if (selection?.ProgrammeMusicKey is { } key && selection.ProgrammeMusicPosition is { } position)
        {
            if (_failedProgrammeKey != key || _failedProgrammeTracks.Count >= ProgrammeMusicOrder.MaximumTracks)
            { _failedProgrammeTracks.Clear(); _failedProgrammeKey = key; }
            _failedProgrammeTracks.Add((key, position));
        }
    }

    private static bool ReadyForProgramme(PreparedSource? source)
    {
        if (source?.Decoder?.Ready.IsCompletedSuccessfully != true || source.Fault is not null) return false;
        if (source.Prefetch is not { } prefetch) return true;
        var ready = prefetch.Snapshot();
        return ready.Error is null && ready.Frames >= AudioFormat.SamplesPerBlock;
    }

    private void PrepareProgrammeCue(ProgrammeAudioCue? cue, DateTimeOffset now)
    {
        if (cue is not null && (cue.Occurrence.DueAtUtc - now > TimeSpan.FromSeconds(30) || now >= cue.Occurrence.EndsAtUtc)) cue = null;
        if (cue is not null && _programmeCue?.Occurrence.Key == cue.Occurrence.Key && _programmeCue.Selection == cue.Selection) return;
        RetireSource(_programmeSource?.Decoder, _programmeSource?.Prefetch); _programmeSource = null; _programmeCue = null;
        if (cue is null) return;
        // Free the least urgent branding preparation before starting a timed
        // source; current audio and its ordinary fallback continue unchanged.
        if (_secondBrand is not null) { RetireSource(_secondBrand.Decoder, _secondBrand.Prefetch); _secondBrand = null; }
        var decoder = CreatePreparedDecoder(cue.Selection, out var fault); NextPcmPrefetch? prefetch = null;
        try
        {
            if (decoder is not null) prefetch = new NextPcmPrefetch(decoder, 60);
            _programmeSource = new(cue.Selection, decoder, fault, prefetch); _programmeCue = cue;
            PublishQueueReservations();
        }
        catch { RetireSource(decoder, prefetch); throw; }
    }

    private async ValueTask ApplyProgrammeAsync()
    {
        if (_programmes is null || _selection is null || _inputEnded) return;
        var now = ProgrammeTime(_preparedSamples); var view = ProgrammeView(now);
        if (view is null) { PrepareProgrammeCue(null, now); return; }
        var signature = (view.Music?.Occurrence.Key, view.Music?.Selection.ProgrammeMusicPosition,
            view.DueInsertion?.Occurrence.Key, view.Upcoming?.Occurrence.Key, true);
        if (signature != _lastProgrammePlan)
        {
            _lastProgrammePlan = signature;
            await RefreshPreparedAsync();
        }
        var due = view.DueInsertion;
        if (due is not null && (_completedProgrammeInsertions.Contains(due.Occurrence.Key) || _selection.ScheduledOccurrenceKey == due.Occurrence.Key)) due = null;
        var active = view.Music;
        if (active is not null && _selection.ProgrammeMusicKey == active.Occurrence.Key) active = null;
        if (_selection.ScheduledOccurrenceKey is not null) active = null;
        if (active?.Selection.ProgrammeMusicKey is { } key && active.Selection.ProgrammeMusicPosition is { } position &&
            _failedProgrammeTracks.Contains((key, position))) active = null;
        var candidate = due ?? active ?? view.Upcoming;
        if (candidate is not null && (_completedProgrammeInsertions.Contains(candidate.Occurrence.Key) ||
            candidate.Occurrence.Key == _selection.ScheduledOccurrenceKey || candidate.Occurrence.Key == _selection.ProgrammeMusicKey)) candidate = null;
        PrepareProgrammeCue(candidate, now);
        var decision = ProgrammeTransitionPolicy.Decide(view, now, _selection, _currentProgrammeWindow, false,
            _completedProgrammeInsertions, cue => _programmeCue?.Occurrence.Key == cue.Occurrence.Key &&
                _programmeCue.Selection == cue.Selection && ReadyForProgramme(_programmeSource));
        if (decision.Kind == ProgrammeTransitionKind.Scheduled && decision.Cue is { } scheduled)
            await SwitchProgrammeSourceAsync(_programmeSource!, scheduled, view);
        else if (decision.Kind == ProgrammeTransitionKind.Ordinary &&
            (_selection.ProgrammeMusicKey is not null || _selection.ScheduledOccurrenceKey is not null && _currentProgrammeWindow is not null))
        {
            var normal = ReadyForProgramme(_musicFallback) ? _musicFallback
                : _nextSource?.Selection.ProgrammeMusicKey is null && ReadyForProgramme(_nextSource) ? _nextSource : null;
            if (normal is not null) await SwitchProgrammeSourceAsync(normal, null, view);
        }
    }

    private async ValueTask SwitchProgrammeSourceAsync(PreparedSource source, ProgrammeAudioCue? cue, ProgrammeRuntimeView view)
    {
        if (ReferenceEquals(source, _programmeSource)) { _programmeSource = null; _programmeCue = null; }
        if (ReferenceEquals(source, _musicFallback)) _musicFallback = null;
        if (ReferenceEquals(source, _nextSource)) _nextSource = null;
        if (_announced) PrepareBoundary("interrupted", _preparedSamples, "The scheduled programme changed; ordinary rotation retains its position.");
        else if (_selection?.QueueEntryId is { } unannouncedQueue) _inflightQueue.Remove(unannouncedQueue);
        _decoded?.Dispose(); _decoded = null; _decodedOffset = 0;
        RetireSource(_decoder, _currentPrefetch);
        _decoder = source.Decoder; _currentPrefetch = source.Prefetch; _decoderFault = source.Fault;
        _selection = source.Selection; _announced = false; _occurrence = Guid.NewGuid().ToString("N");
        _previousOccurrence = null; _itemSamples = 0;
        _currentProgrammeWindow = cue?.Occurrence.Kind == CalendarRuleKind.SpecialDay ? cue.Occurrence
            : cue is null ? null : view.Music?.Occurrence ?? _currentProgrammeWindow;
        TrackSelection(_selection); await PrepareNextAsync();
    }
}
