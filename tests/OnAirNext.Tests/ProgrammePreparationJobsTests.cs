using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ProgrammePreparationJobsTests
{
    private static ProgrammeDraft Draft() => new(new(Guid.NewGuid().ToString("N"), 1, "Remembrance", ["4"],
        CalendarRuleKind.SpecialDay, new(2026, 11, 10), new(2026, 11, 10), TimeOnly.MinValue, TimeOnly.MinValue,
        AllDay: true), Path.Combine(Path.GetTempPath(), "programme"));
    private static async Task<ProgrammePreparationJob> AwaitState(ProgrammePreparationJobs jobs, string id, string state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var job = jobs.Get(id);
            if (job.State == state) return job;
            if (job.FinishedAt is not null) throw new InvalidOperationException("Unexpected job result: " + job.State + " " + job.Error);
            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task PreparationIsSerializedIdempotentAndCancelledBeforePublication()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = 0;
        async Task<PreparedProgramme> Prepare(ProgrammeDraft draft, CancellationToken token)
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(draft, []); }
        Task<ProgrammePublication> Publish(PreparedProgramme programme, long version, string id, string actor)
        { Interlocked.Increment(ref published); return Task.FromResult(new ProgrammePublication(programme, version + 1, true, actor, DateTimeOffset.UtcNow)); }
        await using var jobs = new ProgrammePreparationJobs(Prepare, Publish);
        var draft = Draft(); var id = Guid.NewGuid().ToString("N");
        Assert.Equal("Preparing", jobs.Start(draft, 0, id, "operator").State);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(id, jobs.Start(draft, 0, id, "operator").Id);
        Assert.Throws<ArgumentException>(() => jobs.Start(draft, 1, id, "operator"));
        Assert.Throws<InvalidOperationException>(() => jobs.Start(Draft(), 0, Guid.NewGuid().ToString("N"), "operator"));
        jobs.Cancel(id);
        await AwaitState(jobs, id, "Cancelled");
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task OnceCommitBeginsCancellationCannotReportAnUncommittedResult()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PreparedProgramme> Prepare(ProgrammeDraft draft, CancellationToken token) => Task.FromResult(new PreparedProgramme(draft, []));
        async Task<ProgrammePublication> Publish(PreparedProgramme programme, long version, string id, string actor)
        { committing.TrySetResult(); await release.Task; return new(programme, version + 1, true, actor, DateTimeOffset.UtcNow); }
        await using var jobs = new ProgrammePreparationJobs(Prepare, Publish);
        var id = Guid.NewGuid().ToString("N"); jobs.Start(Draft(), 0, id, "operator");
        await committing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Committing", jobs.Cancel(id).State);
        release.SetResult();
        var result = await AwaitState(jobs, id, "Published");
        Assert.True(result.Publication!.Active);
        Assert.Equal(1, result.Publication.Version);
    }
}
