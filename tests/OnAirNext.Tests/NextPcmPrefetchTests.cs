using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class NextPcmPrefetchTests
{
    private sealed class Decoder(Func<CancellationToken, ValueTask<PcmBlock?>> read) : IFrameDecoder
    {
        public Task Ready => Task.CompletedTask;
        public ValueTask<PcmBlock?> ReadAsync(CancellationToken token) => read(token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task SlowDecoderNeverBlocksTheConsumerAndCanBeCancelled()
    {
        var waiting = new TaskCompletionSource<PcmBlock?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<PcmBlock?> Read(CancellationToken token)
        { entered.TrySetResult(); return await waiting.Task.WaitAsync(token); }
        await using var decoder = new Decoder(Read);
        await using var prefetch = new NextPcmPrefetch(decoder, 4);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(prefetch.TryTake(out _));
        Assert.Equal(0, prefetch.Snapshot().Frames);
    }

    [Fact]
    public async Task BufferIsBoundedAndTransfersEveryBlockInOrder()
    {
        var number = 0;
        ValueTask<PcmBlock?> Read(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (number == 5) return ValueTask.FromResult<PcmBlock?>(null);
            var block = PcmBlock.Rent(); block.Memory.Span[0] = (byte)++number;
            return ValueTask.FromResult<PcmBlock?>(block);
        }
        await using var decoder = new Decoder(Read);
        await using var prefetch = new NextPcmPrefetch(decoder, 4);
        await WaitFor(() => prefetch.Snapshot().Blocks == 4);
        Assert.Equal(4L * AudioFormat.SamplesPerBlock, prefetch.Snapshot().Frames);
        var values = new List<int>();
        while (values.Count < 5)
        {
            if (prefetch.TryTake(out var block)) { using (block) values.Add(block!.Memory.Span[0]); }
            else await Task.Delay(10);
        }
        await WaitFor(() => prefetch.Snapshot().Ended);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, values);
        Assert.Null(prefetch.Snapshot().Error);
        Assert.Equal(0, prefetch.Snapshot().Frames);
    }

    [Fact]
    public async Task DecoderFailureIsReportedWithoutDiscardingAlreadyPreparedAudio()
    {
        var calls = 0;
        ValueTask<PcmBlock?> Read(CancellationToken token)
            => ++calls == 1 ? ValueTask.FromResult<PcmBlock?>(PcmBlock.Rent()) : throw new IOException("fixture");
        await using var decoder = new Decoder(Read);
        await using var prefetch = new NextPcmPrefetch(decoder, 4);
        await WaitFor(() => prefetch.Snapshot().Ended);
        Assert.Equal("IOException", prefetch.Snapshot().Error);
        Assert.True(prefetch.TryTake(out var block));
        block!.Dispose();
        Assert.False(prefetch.TryTake(out _));
    }
}
