using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.Host;
using OnAirNext.ServiceControl;
using Xunit;

namespace OnAirNext.Tests;

public sealed class MicrophoneTransportTests
{
    [Fact]
    public async Task CaptureIsStationBoundAndIdleDisconnectClearsQueuedSpeech()
    {
        var name = "onair-next-dev-mic-" + Guid.NewGuid().ToString("N");
        using var input = new LiveInput();
        await using var server = new MicrophonePipeServer(name, false, null,
            station => station == "chosen" ? input : throw new ArgumentException("Unknown station."), CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var client = new NamedPipeClientStream(".", ServiceEndpoint.Microphone(name), PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(deadline.Token);
        var request = new MicrophoneRequest("chosen", Guid.NewGuid().ToString("N"));
        await client.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n"), deadline.Token);
        var line = await PipeFrames.ReadAsync(client, 4096, deadline.Token);
        Assert.True(JsonSerializer.Deserialize<MicrophoneReply>(line!)!.Accepted);
        var pcm = new byte[AudioFormat.BlockBytes]; MemoryMarshal.Cast<byte,float>(pcm.AsSpan()).Fill(.1f);
        await client.WriteAsync(pcm, deadline.Token); var ack = new byte[1]; await client.ReadExactlyAsync(ack, deadline.Token); Assert.Equal(6, ack[0]);
        var programme = new byte[AudioFormat.BlockBytes]; input.Mix(programme);
        Assert.Equal(.1f, MemoryMarshal.Cast<byte,float>(programme)[0]);
        // Leave a second, unconsumed speech block, then drop capture without sending End Live.
        await client.WriteAsync(pcm, deadline.Token); await client.ReadExactlyAsync(ack, deadline.Token);
        await Task.Delay(MicrophoneRequest.IdleMilliseconds + 400, deadline.Token);
        programme.AsSpan().Clear(); input.Mix(programme);
        Assert.All(MemoryMarshal.Cast<byte,float>(programme).ToArray(), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public async Task UnknownStationCannotEnableMicrophoneAndLaneCanBeReused()
    {
        var name = "onair-next-dev-mic-" + Guid.NewGuid().ToString("N");
        using var input = new LiveInput();
        await using var server = new MicrophonePipeServer(name, false, null,
            station => station == "chosen" ? input : throw new InvalidOperationException("Station unavailable."), CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        foreach (var station in new[] { "wrong", "chosen" })
        {
            await using var client = new NamedPipeClientStream(".", ServiceEndpoint.Microphone(name), PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(deadline.Token);
            await client.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new MicrophoneRequest(station, Guid.NewGuid().ToString("N"))) + "\n"), deadline.Token);
            var line = await PipeFrames.ReadAsync(client,4096,deadline.Token);
            Assert.Equal(station == "chosen", JsonSerializer.Deserialize<MicrophoneReply>(line!)!.Accepted);
            await Task.Delay(50,deadline.Token);
        }
    }

    [Fact]
    public void MicrophoneEndpointCannotMasqueradeAsTheControlEngine()
    {
        Assert.True(ServiceEndpoint.IsReserved(ServiceEndpoint.MicrophonePipe));
        Assert.False(OnAirNext.Web.EngineRpc.IsIsolated(ServiceEndpoint.MicrophonePipe));
        Assert.Throws<ArgumentException>(() => ServiceEndpoint.Microphone(ServiceEndpoint.MicrophonePipe));
        Assert.Throws<ArgumentException>(() => new MicrophoneRequest("", "bad").Validate());
    }
}
