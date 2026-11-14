using System.IO.Pipes;
using System.Net.Sockets;

namespace OnAirNext.Engine;

// Windows retains its overlapped named-pipe path. Unix uses FFmpeg's local unix
// socket protocol so neither platform falls back to blocking redirected stdio.
public sealed class CodecPipe : IDisposable
{
    private readonly NamedPipeServerStream? _windows;
    private readonly Socket? _listener;
    private readonly string? _directory;
    private NetworkStream? _unix;
    public string Path { get; }
    public Stream Stream => (Stream?)_windows ?? _unix ?? throw new InvalidOperationException("Connect the codec first.");
    public CodecPipe(PipeDirection direction)
    {
        if (OperatingSystem.IsWindows())
        {
            var name = "onair-next-codec-" + Guid.NewGuid().ToString("N");
            Path = @"\\.\pipe\" + name;
            _windows = new NamedPipeServerStream(name, direction, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
        }
        else
        {
            // Darwin AF_UNIX paths have a short length limit. This random private
            // directory is independent of any media root and owned by this instance.
            _directory = "/tmp/rt-codec-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var socketPath = System.IO.Path.Combine(_directory, "pcm");
            Path = "unix:" + socketPath;
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { _listener.Bind(new UnixDomainSocketEndPoint(socketPath)); _listener.Listen(1); }
            catch { _listener.Dispose(); if(File.Exists(socketPath))File.Delete(socketPath); Directory.Delete(_directory); throw; }
        }
    }
    public async Task ConnectAsync(CancellationToken token)
    {
        if (_windows is not null) { await _windows.WaitForConnectionAsync(token); return; }
        var socket = await _listener!.AcceptAsync(token);
        _unix = new NetworkStream(socket, ownsSocket: true);
    }
    public void Dispose()
    {
        _windows?.Dispose(); _unix?.Dispose(); _listener?.Dispose();
        if (_directory is not null)
        {
            var socketPath = System.IO.Path.Combine(_directory,"pcm");
            File.Delete(socketPath);
            if (Directory.Exists(_directory)) Directory.Delete(_directory,recursive:false);
        }
    }
}
