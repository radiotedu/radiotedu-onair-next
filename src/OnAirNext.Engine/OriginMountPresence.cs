using System.Net;

namespace OnAirNext.Engine;

// A source TCP socket can remain writable while an Icecast-compatible origin
// no longer exposes its mount. Probe only this output and require repeated,
// independently confirmed 404s before allowing its supervisor to reconnect.
internal static class OriginMountPresence
{
    internal static async Task MonitorAsync(OutputSettings settings, CancellationToken stop,
        HttpMessageHandler? handler = null, TimeSpan? interval = null, TimeSpan? timeout = null)
    {
        var wait = interval ?? TimeSpan.FromSeconds(15);
        var deadline = timeout ?? TimeSpan.FromSeconds(3);
        if (wait <= TimeSpan.Zero || deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException();
        using var http = new HttpClient(handler ?? new SocketsHttpHandler
        { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = deadline });
        http.Timeout = Timeout.InfiniteTimeSpan;
        var uri = new UriBuilder("http", settings.Host, settings.Port, settings.Mount).Uri;
        var missing = 0;
        while (true)
        {
            await Task.Delay(wait, stop);
            try
            {
                var head = await ProbeAsync(http, uri, HttpMethod.Head, deadline, stop);
                if (head != HttpStatusCode.NotFound) { missing = 0; continue; }
                // Some servers do not support HEAD. A GET checks the response headers
                // only and is immediately disposed; no listener audio is retained.
                var get = await ProbeAsync(http, uri, HttpMethod.Get, deadline, stop);
                missing = get == HttpStatusCode.NotFound ? missing + 1 : 0;
                if (missing >= 3) throw new OriginMountMissingException();
            }
            catch (OriginMountMissingException) { throw; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
            {
                // Network failures, proxy errors and timeouts are inconclusive.
                missing = 0;
            }
        }
    }

    private static async Task<HttpStatusCode> ProbeAsync(HttpClient http, Uri uri, HttpMethod method,
        TimeSpan timeout, CancellationToken stop)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stop);
        budget.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.ConnectionClose = true;
        // A race can turn a confirming GET into a listener. Never share an
        // identity with a real player on origins that deduplicate listeners.
        request.Headers.UserAgent.ParseAdd("RadioTEDU-OnAirNext-MountCheck/" + Guid.NewGuid().ToString("N"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        return response.StatusCode;
    }

    internal sealed class OriginMountMissingException() : IOException("The origin no longer exposes this mount; reconnecting this output.") { }
}
