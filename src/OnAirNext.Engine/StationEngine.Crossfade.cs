using System.Runtime.InteropServices;

namespace OnAirNext.Engine;

public sealed partial class StationEngine
{
    private sealed class CrossfadeState(Cursor outgoing,DecoderPreparation.Source? decoder,NextPcmPrefetch? prefetch,
        PcmBlock? block,int blockOffset,EqualPowerCrossfade curve)
    {
        public Cursor Outgoing=outgoing;
        public readonly DecoderPreparation.Source? Decoder=decoder;
        public readonly NextPcmPrefetch? Prefetch=prefetch;
        public PcmBlock? Block=block;
        public int BlockOffset=blockOffset,Frames;
        public readonly EqualPowerCrossfade Curve=curve;
    }

    private CrossfadeState? _crossfade;
    private PlayoutSelection? _crossfadePlanCurrent;
    private PreparedSource? _crossfadePlanNext;
    private CrossfadeWindow? _crossfadePlan;

    private CrossfadeWindow? CurrentCrossfadePlan()
    {
        if(_crossfadeCurve is null||_selection is null||_nextSource is null)return null;
        if(!ReferenceEquals(_crossfadePlanCurrent,_selection)||!ReferenceEquals(_crossfadePlanNext,_nextSource))
        {
            _crossfadePlanCurrent=_selection;_crossfadePlanNext=_nextSource;
            _crossfadePlan=CrossfadeAdmission.Plan(_selection.Item,_nextSource.Selection.Item,_settings.CrossfadeSeconds);
        }
        return _crossfadePlan;
    }

    private long? FramesUntilProgrammeBoundary(long at)
    {
        if(_programmes is null)return null;
        var now=ProgrammeTime(at);var view=ProgrammeView(now);
        if(view is null)return 0; // A stale scheduler cannot safely authorize an overlap.
        DateTimeOffset? deadline=null;
        if(view.DueInsertion is { } due&&!_completedProgrammeInsertions.Contains(due.Occurrence.Key)&&
            _selection?.ScheduledOccurrenceKey!=due.Occurrence.Key)deadline=due.Occurrence.DueAtUtc;
        if(view.Upcoming is { } upcoming&&!_completedProgrammeInsertions.Contains(upcoming.Occurrence.Key)&&
            (deadline is null||upcoming.Occurrence.DueAtUtc<deadline))deadline=upcoming.Occurrence.DueAtUtc;
        if(_currentProgrammeWindow is { } window&&(deadline is null||window.EndsAtUtc<deadline))deadline=window.EndsAtUtc;
        return deadline is null?null:Math.Max(0,(long)Math.Floor((deadline.Value-now).TotalSeconds*AudioFormat.SampleRate));
    }

    private bool TryBeginCrossfade(CrossfadeWindow? plan,long at)
    {
        var next=_nextSource;
        if(!_announced||_decoded is null||next?.Prefetch is null||next.Decoder?.Ready.IsCompletedSuccessfully!=true||next.Fault is not null||
            !CrossfadeAdmission.CanStart(plan,_itemSamples,next.Prefetch.Snapshot(),FramesUntilProgrammeBoundary(at)))return false;
        if(next.Selection.QueueEntryId is not null&&_planner?.ResolveQueueRecovery(next.Selection) is null)return false;
        var outgoing=PreparedCursor();
        _crossfade=new(outgoing,_decoder,_currentPrefetch,_decoded,_decodedOffset,_crossfadeCurve!);
        _decoded=null;_decodedOffset=0;_decoder=next.Decoder;_currentPrefetch=next.Prefetch;_decoderFault=null;_nextSource=null;
        if(_selection?.AdvancesRotation==true)_index++;
        _selection=next.Selection;_announced=false;_occurrence=Guid.NewGuid().ToString("N");_previousOccurrence=null;_itemSamples=0;
        _pendingOverlapStart=outgoing.Occurrence;TrackSelection(_selection);
        return true;
    }

    private PlayoutCheckpoint PreparedPlayingCheckpoint(long at,Cursor cursor)
        =>new(_settings.Id,"Playing",cursor.RotationIndex,cursor.Selection.Item,cursor.Occurrence,cursor.ItemSamples,at,true,default,
            cursor.Selection.QueueEntryId,_sessionId,cursor.PreviousOccurrence,cursor.Selection.HoldsRotation,
            cursor.Selection.ScheduledOccurrenceKey,cursor.Selection.ProgrammeMusicKey,cursor.Selection.ProgrammeMusicPosition,
            cursor.Selection.SuppressStationBranding);

    private async ValueTask FinishCrossfadeAsync(string type,long at,string detail="")
    {
        var fade=_crossfade??throw new InvalidOperationException("No crossfade is active.");
        var checkpoint=_announced?PreparedPlayingCheckpoint(at,PreparedCursor()):new PlayoutCheckpoint(_settings.Id,"BetweenItems",
            _index,null,"",0,at,false,default,SessionId:_sessionId);
        if(!_announced)_pendingOverlapStart=null;
        PrepareBoundaryFor(type,at,detail,fade.Outgoing,checkpoint);
        fade.Block?.Dispose();fade.Block=null;RetireSource(fade.Decoder,fade.Prefetch);_crossfade=null;
        await PrepareNextAsync();
    }

    private async ValueTask CancelCrossfadeAsync(long at)
    {
        var fade=_crossfade??throw new InvalidOperationException("No crossfade is active.");
        var incoming=PreparedCursor();
        if(_announced)
        {
            var checkpoint=PreparedPlayingCheckpoint(at,fade.Outgoing);
            PrepareBoundaryFor("failed",at,"The prepared incoming audio became unavailable; the outgoing source continued.",incoming,checkpoint);
            PrepareBoundaryFor("resumed",at,"The outgoing source continued after a cancelled overlap.",fade.Outgoing,checkpoint);
        }
        else if(incoming.Selection.QueueEntryId is { } queued)_inflightQueue.Remove(queued);
        _decoded?.Dispose();RetireSource(_decoder,_currentPrefetch);
        _selection=fade.Outgoing.Selection;_index=fade.Outgoing.RotationIndex;_occurrence=fade.Outgoing.Occurrence;
        _previousOccurrence=fade.Outgoing.PreviousOccurrence;_itemSamples=fade.Outgoing.ItemSamples;_announced=true;
        _decoder=fade.Decoder;_currentPrefetch=fade.Prefetch;_decoderFault=null;_decoded=fade.Block;_decodedOffset=fade.BlockOffset;
        fade.Block=null;_crossfade=null;_pendingOverlapStart=null;
        await PrepareNextAsync();
    }

    private async ValueTask<int> FillCrossfadeAsync(Memory<byte> target,int offset,CancellationToken token)
    {
        var fade=_crossfade??throw new InvalidOperationException("No crossfade is active.");
        var at=_preparedSamples+offset/AudioFormat.BytesPerSampleFrame;
        if(_decoded is null)
        {
            if(_currentPrefetch?.TryTake(out _decoded)!=true||_decoded is null)
            {await CancelCrossfadeAsync(at);return 0;}
            _decodedOffset=0;
        }
        if(fade.Block is null)
        {
            try{fade.Block=fade.Prefetch is null?await fade.Decoder!.ReadAsync(token):await fade.Prefetch.ReadAsync(token);fade.BlockOffset=0;}
            catch(Exception error)when(error is not OperationCanceledException)
            {await FinishCrossfadeAsync("failed",at,"The outgoing source failed during overlap: "+error.GetType().Name);return 0;}
            if(fade.Block is null)
            {await FinishCrossfadeAsync("failed",at,"The outgoing source ended before its measured overlap window.");return 0;}
        }
        if(!_announced){_announced=true;PrepareBoundary("started",at);}
        var take=Math.Min(target.Length-offset,Math.Min(_decoded.Length-_decodedOffset,fade.Block.Length-fade.BlockOffset));
        take=Math.Min(take,(fade.Curve.Frames-fade.Frames)*AudioFormat.BytesPerSampleFrame);
        var frames=take/AudioFormat.BytesPerSampleFrame;
        AddSlice(at,at+frames,PreparedCursor());AddSlice(at,at+frames,fade.Outgoing,false);
        fade.Curve.Mix(MemoryMarshal.Cast<byte,float>(fade.Block.Memory.Span.Slice(fade.BlockOffset,take)),
            MemoryMarshal.Cast<byte,float>(_decoded.Memory.Span.Slice(_decodedOffset,take)),
            MemoryMarshal.Cast<byte,float>(target.Span.Slice(offset,take)),fade.Frames);
        _decodedOffset+=take;_itemSamples+=frames;fade.BlockOffset+=take;fade.Outgoing=fade.Outgoing with{ItemSamples=fade.Outgoing.ItemSamples+frames};fade.Frames+=frames;
        if(_decodedOffset==_decoded.Length){_decoded.Dispose();_decoded=null;}
        if(fade.BlockOffset==fade.Block.Length){fade.Block.Dispose();fade.Block=null;}
        if(fade.Frames==fade.Curve.Frames)await FinishCrossfadeAsync("ended",at+frames);
        return take;
    }

    private void ReleaseCrossfade()
    {
        if(_crossfade is not { } fade)return;
        fade.Block?.Dispose();fade.Block=null;RetireSource(fade.Decoder,fade.Prefetch);_crossfade=null;
    }
}
