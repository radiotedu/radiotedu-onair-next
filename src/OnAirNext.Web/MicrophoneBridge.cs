using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;

namespace OnAirNext.Web;

internal static class MicrophoneBridge
{
    public const string Protocol = "onair-microphone-v1";
    public static async Task RunAsync(HttpContext context, string engine, string station, CancellationToken stopping)
    {
        if (!context.WebSockets.IsWebSocketRequest || !EngineRpc.IsIsolated(engine))
            throw new ArgumentException("Choose a connected engine and open the microphone from On Air.");
        var request = new MicrophoneRequest(station, Guid.NewGuid().ToString("N")); request.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var name = ServiceEndpoint.Microphone(engine);
        await using var pipe = ServicePipeClient.Create(name);
        await pipe.ConnectAsync(deadline.Token); ServicePipeClient.AuthenticateServer(pipe, name);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n"), deadline.Token);
        var line = await PipeFrames.ReadAsync(pipe, 4096, deadline.Token) ?? throw new IOException("Microphone did not confirm the selected station.");
        var reply = JsonSerializer.Deserialize<MicrophoneReply>(line) ?? throw new IOException("Invalid microphone response.");
        if (!reply.Accepted) throw new InvalidOperationException(reply.Error ?? "Microphone unavailable.");
        using var socket = await context.WebSockets.AcceptWebSocketAsync(Protocol);
        var pcm = new byte[MicrophoneRequest.BlockBytes]; var acknowledgement = new byte[1];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                deadline.CancelAfter(MicrophoneRequest.IdleMilliseconds);
                var count = 0;
                ValueWebSocketReceiveResult packet;
                do
                {
                    packet = await socket.ReceiveAsync(pcm.AsMemory(count), deadline.Token);
                    if (packet.MessageType == WebSocketMessageType.Close) return;
                    if (packet.MessageType != WebSocketMessageType.Binary || packet.Count == 0)
                        throw new IOException("Microphone expects bounded PCM blocks.");
                    count += packet.Count;
                    if (count == pcm.Length && !packet.EndOfMessage) throw new IOException("Microphone block exceeds its limit.");
                } while (!packet.EndOfMessage);
                if (count != pcm.Length) throw new IOException("Microphone block is incomplete.");
                await pipe.WriteAsync(pcm, deadline.Token);
                await pipe.ReadExactlyAsync(acknowledgement, deadline.Token);
                if (acknowledgement[0] != 6) throw new IOException("Microphone input was not acknowledged.");
                // Confirms engine receipt, not listener delivery. No unbounded server send queue.
                await socket.SendAsync(acknowledgement.AsMemory(), WebSocketMessageType.Binary, true, deadline.Token);
            }
        }
        catch (Exception error) when (error is IOException or WebSocketException or OperationCanceledException) { }
        finally { socket.Abort(); }
    }
}
