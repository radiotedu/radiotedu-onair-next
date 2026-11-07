using System.Net;
using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class OriginMountPresenceTests
{
    private static readonly OutputSettings Settings = new("fixture", "127.0.0.1", 19001, "/next-fixture",
        "fixture-secret-never-transmitted");

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal("/next-fixture", request.RequestUri.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            Assert.NotEmpty(request.Headers.UserAgent);
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task ThreeIndependentlyConfirmedMissingResponsesReconnectOnlyThatOutput()
    {
        var methods = new List<HttpMethod>();
        using var handler = new Handler(request => { methods.Add(request.Method); return new(HttpStatusCode.NotFound); });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<OriginMountPresence.OriginMountMissingException>(() =>
            OriginMountPresence.MonitorAsync(Settings, stop.Token, handler, TimeSpan.FromMilliseconds(5)));
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Get, HttpMethod.Head, HttpMethod.Get, HttpMethod.Head, HttpMethod.Get }, methods);
    }

    [Fact]
    public async Task HeadUnsupportedButGetAvailableDoesNotRestartAHealthySource()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gets = 0;
        using var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Get && Interlocked.Increment(ref gets) >= 4) observed.TrySetResult();
            return new(request.Method == HttpMethod.Head ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var monitor = OriginMountPresence.MonitorAsync(Settings, stop.Token, handler, TimeSpan.FromMilliseconds(5));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(monitor.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor);
    }

    [Fact]
    public async Task ServerErrorsAreNotEvidenceOfAMissingMount()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var handler = new Handler(_ =>
        {
            if (Interlocked.Increment(ref count) >= 4) observed.TrySetResult();
            return new(HttpStatusCode.ServiceUnavailable);
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var monitor = OriginMountPresence.MonitorAsync(Settings, stop.Token, handler, TimeSpan.FromMilliseconds(5));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(monitor.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor);
    }
    [Fact]
    public async Task NetworkFailuresDoNotTriggerAReconnectOfAHealthySource()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var handler = new Handler(_ =>
        {
            if (Interlocked.Increment(ref count) >= 4) observed.TrySetResult();
            throw new HttpRequestException("fixture network failure");
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var monitor = OriginMountPresence.MonitorAsync(Settings, stop.Token, handler, TimeSpan.FromMilliseconds(5));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(monitor.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor);
    }

}
