namespace OnAirNext.Engine;

public sealed partial class StationEngine
{
    private const int MaximumPreparedEntries=8192,MaximumAuditTickets=1024;
    private sealed record Cursor(int RotationIndex,PlayoutSelection Selection,string Occurrence,string? PreviousOccurrence,long ItemSamples);
    private sealed record Slice(long Start,long End,Cursor Cursor,bool Dominant=true);
    private sealed record Boundary(string Type,long Position,string Detail,Cursor Cursor,PlayoutCheckpoint Checkpoint,string? OverlapWith);
    private sealed record AuditTicket(Task Commit,string? ReleaseQueueEntry);
    private readonly Queue<Slice> _slices=new();
    private readonly Queue<Boundary> _boundaries=new();
    private readonly Queue<AuditTicket> _audits=new();
    private readonly HashSet<string> _inflightQueue=new(StringComparer.Ordinal);
    private Cursor? _audible;
    private PlayoutSelection? _visibleNextSelection;
    private PlayoutCheckpoint? _lastCheckpoint;
    private string? _pendingOverlapStart;
    private int _pendingAudit;

    private Cursor PreparedCursor()=>new(_index,_selection!,_occurrence,_previousOccurrence,_itemSamples);
    private void TrackSelection(PlayoutSelection selection)
    {
        if(selection.QueueEntryId is not null&&!_inflightQueue.Add(selection.QueueEntryId))
            throw new InvalidOperationException("The planner selected a queue entry already owned by prepared or uncommitted audio.");
        _previousOccurrence ??= selection.PreviousOccurrenceId;
    }
    private void PublishQueueReservations()
    {
        if (_planner is null) return;
        var retained=new HashSet<string>(_inflightQueue,StringComparer.Ordinal);
        foreach (var source in new[]{_nextSource,_secondBrand,_musicFallback})
            if (source?.Selection.QueueEntryId is { } id) retained.Add(id);
        _planner.RetainQueueReservations(retained);
    }
    private void AddSlice(long start,long end,Cursor cursor,bool dominant=true)
    {
        if(_slices.Count>=MaximumPreparedEntries)throw new InvalidOperationException("Prepared programme ownership exceeded its bounded window.");
        _slices.Enqueue(new(start,end,cursor,dominant));
    }
    private void PrepareBoundary(string type,long position,string detail="")
    {
        if(_boundaries.Count>=MaximumPreparedEntries)throw new InvalidOperationException("Prepared event ownership exceeded its bounded window.");
        var between=type is "ended" or "skipped" or "failed" or "decode_failed" or "queue_deferred" or "interrupted";
        var cursor=PreparedCursor();
        if((type is "ended" or "skipped") && cursor.Selection.ScheduledOccurrenceKey is { } scheduled)
            RememberProgrammeInsertion(scheduled);
        var checkpoint=new PlayoutCheckpoint(_settings.Id,between?"BetweenItems":"Playing",
            _index+(between&&type!="interrupted"&&_selection?.AdvancesRotation==true?1:0),between?null:_selection?.Item,_occurrence,
            between?0:_itemSamples,position,!between&&_announced,default,between?null:_selection?.QueueEntryId,_sessionId,_previousOccurrence,
            !between&&_selection?.HoldsRotation==true,between?null:_selection?.ScheduledOccurrenceKey,
            between?null:_selection?.ProgrammeMusicKey,between?null:_selection?.ProgrammeMusicPosition,
            !between&&_selection?.SuppressStationBranding==true);
        var overlap=type=="started"?_pendingOverlapStart:null;
        if(type=="started")_pendingOverlapStart=null;
        PrepareBoundaryFor(type,position,detail,cursor,checkpoint,overlap);
    }
    private void PrepareBoundaryFor(string type,long position,string detail,Cursor cursor,PlayoutCheckpoint checkpoint,string? overlapWith=null)
    {
        if(_boundaries.Count>=MaximumPreparedEntries)throw new InvalidOperationException("Prepared event ownership exceeded its bounded window.");
        if(cursor.Selection.ProgrammeMusicKey is null||!cursor.Selection.SuppressStationBranding)
            _preparedBranding=_preparedBranding.Project(cursor.Selection.Item.Kind,type);
        _boundaries.Enqueue(new(type,position,detail,cursor,checkpoint,overlapWith));
    }
    // Start at the end of the published span is still unheard. Terminal at that end belongs to the published span.
    private void CommitBoundaries(long through,bool includeStartAtBoundary)
    {
        while(_boundaries.TryPeek(out var boundary)&&boundary.Position<=through)
        {
            if((boundary.Type is "started" or "resumed") && boundary.Position==through&&!includeStartAtBoundary)break;
            _boundaries.Dequeue();var cursor=boundary.Cursor;var item=cursor.Selection.Item;
            var entry=new PlaybackEvent(Guid.NewGuid().ToString("N"),cursor.Occurrence,_settings.Id,boundary.Type,
                item.Id,item.Artist,item.Title,item.Kind,_anchor.AddSeconds((double)boundary.Position/AudioFormat.SampleRate),
                boundary.Position,boundary.Detail,cursor.Selection.QueueEntryId,cursor.PreviousOccurrence,
                SourceGain:SourceNormalization.Decide(item.Loudness),ScheduledOccurrenceKey:cursor.Selection.ScheduledOccurrenceKey,
                ProgrammeMusicKey:cursor.Selection.ProgrammeMusicKey,ProgrammeMusicPosition:cursor.Selection.ProgrammeMusicPosition,
                SuppressStationBranding:cursor.Selection.SuppressStationBranding,OverlapWithOccurrenceId:boundary.OverlapWith);
            var checkpoint=boundary.Checkpoint with{SavedAtUtc=DateTimeOffset.UtcNow};
            var terminal=boundary.Type is "ended" or "skipped" or "failed" or "decode_failed" or "queue_deferred" or "interrupted";
            QueueAudit(()=>_journal.AppendAsync(entry,checkpoint),terminal?cursor.Selection.QueueEntryId:null);
            _lastCheckpoint=checkpoint;
            if(boundary.Type is "started" or "resumed")
            {
                Volatile.Write(ref _audible,cursor);
                PublishProgrammeMetadata(item,boundary.Position);
            }
            else if(terminal)
            {
                if(Volatile.Read(ref _audible)?.Occurrence==cursor.Occurrence)Volatile.Write(ref _audible,null);
                _requestedSkips.TryRemove(cursor.Occurrence,out _);
                if((boundary.Type is "ended" or "skipped") && cursor.Selection.ScheduledOccurrenceKey is { } scheduled)
                    RememberProgrammeInsertion(scheduled);
                if(boundary.Type=="ended")Interlocked.Increment(ref _completed);
                if(boundary.Type=="skipped")Interlocked.Increment(ref _skipped);
            }
        }
    }
    private void AdvanceAudibleProjection(long end)
    {
        Cursor? atEnd=null;
        while(_slices.TryPeek(out var slice)&&slice.Start<end)
        {
            if(slice.Dominant)atEnd=slice.Cursor with{ItemSamples=slice.Cursor.ItemSamples+Math.Min(end,slice.End)-slice.Start};
            if(slice.End>end)break;_slices.Dequeue();
        }
        if(atEnd is not null&&Volatile.Read(ref _audible)?.Occurrence==atEnd.Occurrence)Volatile.Write(ref _audible,atEnd);
        UpdateVisibleNext();
    }
    private void UpdateVisibleNext()
    {
        var occurrence=Volatile.Read(ref _audible)?.Occurrence;
        var next=_slices.FirstOrDefault(x=>x.Dominant&&x.Cursor.Occurrence!=occurrence)?.Cursor.Selection;
        if(next is null&&_selection is not null&&_occurrence!=occurrence)next=_selection;
        next??=_nextSource?.Selection;
        Volatile.Write(ref _visibleNextSelection,next);
    }
    private PlayoutCheckpoint AudibleCheckpoint(string state)
    {
        var cursor=Volatile.Read(ref _audible);
        if(cursor is not null)return new(_settings.Id,state,cursor.RotationIndex,cursor.Selection.Item,cursor.Occurrence,
            cursor.ItemSamples,_samples,true,DateTimeOffset.UtcNow,cursor.Selection.QueueEntryId,_sessionId,cursor.PreviousOccurrence,cursor.Selection.HoldsRotation,cursor.Selection.ScheduledOccurrenceKey,
            cursor.Selection.ProgrammeMusicKey,cursor.Selection.ProgrammeMusicPosition,cursor.Selection.SuppressStationBranding);
        return (_lastCheckpoint??new(_settings.Id,"BetweenItems",_index,null,"",0,_samples,false,default,SessionId:_sessionId))
            with{ProgramSamples=_samples,SavedAtUtc=DateTimeOffset.UtcNow};
    }
    private void EmitAudibleTerminal(string type,string detail)
    {
        var cursor=Volatile.Read(ref _audible);if(cursor is null)return;
        var item=cursor.Selection.Item;var checkpoint=AudibleCheckpoint(type=="interrupted"?"Stopped":"BetweenItems");
        if(type!="interrupted")checkpoint=checkpoint with{RotationIndex=cursor.RotationIndex+(cursor.Selection.AdvancesRotation?1:0),Item=null,ItemSamples=0,Announced=false,QueueEntryId=null,HoldsRotation=false,ScheduledOccurrenceKey=null,ProgrammeMusicKey=null,ProgrammeMusicPosition=null,SuppressStationBranding=false};
        var entry=new PlaybackEvent(Guid.NewGuid().ToString("N"),cursor.Occurrence,_settings.Id,type,item.Id,item.Artist,item.Title,
            item.Kind,_anchor.AddSeconds((double)_samples/AudioFormat.SampleRate),_samples,detail,cursor.Selection.QueueEntryId,cursor.PreviousOccurrence,
            SourceGain:SourceNormalization.Decide(item.Loudness),ScheduledOccurrenceKey:cursor.Selection.ScheduledOccurrenceKey,
            ProgrammeMusicKey:cursor.Selection.ProgrammeMusicKey,ProgrammeMusicPosition:cursor.Selection.ProgrammeMusicPosition,
            SuppressStationBranding:cursor.Selection.SuppressStationBranding);
        QueueAudit(()=>_journal.AppendAsync(entry,checkpoint),type=="interrupted"?null:cursor.Selection.QueueEntryId);_lastCheckpoint=checkpoint;
    }
    private void QueueAudit(Func<Task> submit,string? releaseQueueEntry=null)
    {
        ObserveAudit();
        if(_audits.Count>=MaximumAuditTickets){Interlocked.Increment(ref _auditRejected);throw new IOException("Playback history commit backlog exceeded its bounded limit.");}
        _audits.Enqueue(new(submit(),releaseQueueEntry));Volatile.Write(ref _pendingAudit,_audits.Count);
    }
    private void ObserveAudit()
    {
        var changed=false;
        while(_audits.TryPeek(out var ticket)&&ticket.Commit.IsCompleted)
        {
            _audits.Dequeue();Volatile.Write(ref _pendingAudit,_audits.Count);
            try{ticket.Commit.GetAwaiter().GetResult();}catch{Interlocked.Increment(ref _auditRejected);throw;}
            if(ticket.ReleaseQueueEntry is not null) changed|=_inflightQueue.Remove(ticket.ReleaseQueueEntry);
        }
        if (changed) PublishQueueReservations();
    }
    private async Task DrainAuditAsync()
    {
        if(_audits.Count==0)return;
        Exception? failure=null;
        try{await Task.WhenAll(_audits.Select(x=>x.Commit)).WaitAsync(TimeSpan.FromSeconds(2));}
        catch(TimeoutException){throw;}
        catch(Exception ex){failure=ex;}
        while(_audits.TryPeek(out var ticket)&&ticket.Commit.IsCompleted)
        {
            _audits.Dequeue();Volatile.Write(ref _pendingAudit,_audits.Count);
            if(ticket.Commit.IsCompletedSuccessfully){if(ticket.ReleaseQueueEntry is not null)_inflightQueue.Remove(ticket.ReleaseQueueEntry);}
            else{Interlocked.Increment(ref _auditRejected);failure??=(Exception?)ticket.Commit.Exception??new IOException("Playback history commit was cancelled.");}
        }
        if(failure is not null)throw failure;
    }
}
