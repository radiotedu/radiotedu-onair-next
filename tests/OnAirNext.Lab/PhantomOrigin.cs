using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OnAirNext.Engine;

namespace OnAirNext.Lab;

// Loopback-only fixture: a source remains writable after its mount disappears.
internal sealed class PhantomOrigin : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _clients = [];
    private readonly ConcurrentDictionary<string,int> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,long> _bytes = new(StringComparer.Ordinal);
    private readonly Task _run;
    private int _hidden;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public PhantomOrigin() { _listener.Start(); _run = AcceptAsync(); }
    public OutputSettings Output(string id) => new(id, "127.0.0.1", Port, "/next-" + id, "loopback-fixture-password");
    public void HidePhantom() => Volatile.Write(ref _hidden, 1);
    public long Bytes(string name) => _bytes.GetValueOrDefault(name);
    private async Task AcceptAsync()
    {
        try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _clients.Add(HandleAsync(client)); } }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream(); var data = new List<byte>(); var one = new byte[1];
            while (data.Count < 8192)
            {
                if (await stream.ReadAsync(one, _stop.Token) == 0) return; data.Add(one[0]);
                if (data.Count >= 4 && data[^4] == 13 && data[^3] == 10 && data[^2] == 13 && data[^1] == 10) break;
            }
            var line = Encoding.ASCII.GetString(data.ToArray()).Split('\n')[0].Trim().Split(' ');
            if (line.Length < 2) return; var method = line[0]; var route = line[1].Split('?')[0];
            if (method == "PUT" && (route is "/next-phantom" or "/next-peer"))
            {
                var count = _connections.AddOrUpdate(route, 1, (_, value) => value + 1);
                if (route == "/next-phantom" && count > 1) Volatile.Write(ref _hidden, 0);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), _stop.Token);
                var buffer = new byte[16384]; int length;
                while ((length = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                    _bytes.AddOrUpdate(route, length, (_, value) => value + length);
                return;
            }
            var online = route == "/admin/metadata" ||
                _connections.ContainsKey(route) && (route != "/next-phantom" || Volatile.Read(ref _hidden) == 0);
            var payload = online && route == "/admin/metadata" ? "<iceresponse><return>1</return></iceresponse>" : "";
            var response = $"HTTP/1.1 {(online ? "200 OK" : "404 Not Found")}\r\nContent-Length: {Encoding.UTF8.GetByteCount(payload)}\r\nConnection: close\r\n\r\n{payload}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), _stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener.Stop(); await _run; await Task.WhenAll(_clients); _stop.Dispose();
    }
}
