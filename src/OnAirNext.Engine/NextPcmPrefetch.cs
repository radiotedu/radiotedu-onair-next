namespace OnAirNext.Engine;

internal sealed record PcmPrefetchSnapshot(int Blocks, long Frames, bool Ended, string? Error);

// Prepares incoming PCM off the station lane. A transition may only begin when
// its entire required incoming window is already present; otherwise the caller
// can continue the current source without waiting on a slow disk or decoder.
// The decoder lifetime remains owned by DecoderPreparation.
internal sealed class NextPcmPrefetch : IFrameDecoder
{
    private readonly object _gate = new();
    private readonly Queue<PcmBlock> _blocks = new();
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _producer;
    public Task Ready { get; }
    private TaskCompletionSource _changed = NewSignal();
    private long _frames;
    private bool _ended, _disposed;
    private Exception? _error;

    public NextPcmPrefetch(IFrameDecoder decoder, int capacityBlocks)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        if (capacityBlocks is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(capacityBlocks));
        Ready = decoder.Ready;
        _slots = new SemaphoreSlim(capacityBlocks, capacityBlocks);
        _producer = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await _slots.WaitAsync(_stop.Token);
                    PcmBlock? block;
                    try { block = await decoder.ReadAsync(_stop.Token); }
                    catch { _slots.Release(); throw; }
                    if (block is null) { _slots.Release(); break; }
                    if (block.Length is <= 0 or > AudioFormat.BlockBytes || block.Length % AudioFormat.BytesPerSampleFrame != 0)
                    { block.Dispose(); _slots.Release(); throw new InvalidDataException("The prepared PCM block is malformed."); }
                    lock (_gate)
                    {
                        if (_disposed) { block.Dispose(); _slots.Release(); break; }
                        _blocks.Enqueue(block); _frames += block.Length / AudioFormat.BytesPerSampleFrame; Signal();
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception failure) { lock (_gate) _error = failure; }
            finally { lock (_gate) { _ended = true; Signal(); } }
        });
    }

    public PcmPrefetchSnapshot Snapshot()
    { lock (_gate) return new(_blocks.Count, _frames, _ended, _error?.GetType().Name); }

    // Ownership of a returned block transfers to the consumer.
    public bool TryTake(out PcmBlock? block)
    {
        lock (_gate)
        {
            if (_disposed || !_blocks.TryDequeue(out block)) { block = null; return false; }
            _frames -= block.Length / AudioFormat.BytesPerSampleFrame;
            _slots.Release(); return true;
        }
    }

    // After a prepared transition, the same single consumer can continue to
    // read this source without racing a second reader against the producer.
    public async ValueTask<PcmBlock?> ReadAsync(CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_blocks.TryDequeue(out var block))
                {
                    _frames -= block.Length / AudioFormat.BytesPerSampleFrame;
                    _slots.Release(); return block;
                }
                if (_ended)
                {
                    if (_error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_error).Throw();
                    return null;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(token);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Signal() { var previous = _changed; _changed = NewSignal(); previous.TrySetResult(); }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; Signal(); }
        await _stop.CancelAsync();
        await _producer;
        lock (_gate) { while (_blocks.TryDequeue(out var block)) block.Dispose(); _frames = 0; }
        _slots.Dispose(); _stop.Dispose();
    }
}
