using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;

namespace OnAirNext.Host;

// A bounded, independent audio lane. Losing this client never owns station/output lifetime.
internal sealed class MicrophonePipeServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly ServicePipePolicy? _policy;
    private readonly Func<string, LiveInput> _microphone;
    private readonly CancellationTokenSource _stop;
    private readonly Task _worker;
    public MicrophonePipeServer(string control, bool serviceMode, ServicePipePolicy? policy, Func<string, LiveInput> microphone, CancellationToken token)
    {
        var name = ServiceEndpoint.Microphone(control); _policy = policy; _microphone = microphone;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _pipe = serviceMode && OperatingSystem.IsWindows() ? policy!.CreateServer(name)
            : new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 16384, 4096);
        _worker = RunAsync();
    }
    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            LiveInput? input = null; var connected = false;
            try
            {
                await _pipe.WaitForConnectionAsync(_stop.Token); connected = true;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var line = await PipeFrames.ReadAsync(_pipe, 4096, deadline.Token) ?? throw new IOException("Missing microphone request.");
                if (_policy is not null && OperatingSystem.IsWindows()) _policy.Authorize(_pipe);
                var request = JsonSerializer.Deserialize<MicrophoneRequest>(line) ?? throw new ArgumentException("Missing microphone request.");
                request.Validate();
                try { input = _microphone(request.StationId); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                { await ReplyAsync(new(false, error.Message), deadline.Token); if (await _pipe.ReadAsync(new byte[1], deadline.Token) != 0) throw new IOException("Unexpected microphone data."); continue; }
                input.SetEnabled(true);
                await ReplyAsync(new(true), deadline.Token);
                var buffer = new byte[MicrophoneRequest.BlockBytes];
                while (!_stop.IsCancellationRequested)
                {
                    deadline.CancelAfter(MicrophoneRequest.IdleMilliseconds);
                    await _pipe.ReadExactlyAsync(buffer, deadline.Token);
                    // A replaced or stopped station must require another explicit Go live.
                    if (!ReferenceEquals(input, _microphone(request.StationId))) break;
                    var block = PcmBlock.Rent(); buffer.CopyTo(block.Memory.Span); input.Push(block);
                    await _pipe.WriteAsync(new byte[] { 6 }, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException) { }
            finally
            {
                input?.SetEnabled(false);
                if (connected) try { _pipe.Disconnect(); } catch (IOException) { }
            }
        }
    }
    private Task ReplyAsync(MicrophoneReply reply, CancellationToken token) => _pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply) + "\n"), token).AsTask();
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _worker; } finally { await _pipe.DisposeAsync(); _stop.Dispose(); }
    }
}
