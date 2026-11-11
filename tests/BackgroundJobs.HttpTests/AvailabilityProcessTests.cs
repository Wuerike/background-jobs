using System.Net;
using System.Text.Json;
using Xunit.Abstractions;

namespace BackgroundJobs.HttpTests;

public sealed partial class ProcessLifecycleTests
{
    [Fact]
    public async Task Redis_returns_to_same_demo_process_without_reinvoking_finished_function()
    {
        await using var host = new ProcessHost();
        try
        {
            await host.Start(); var pid = host.Demo.Id;
            var retry = await host.Create("fail-n-times", new { failTimes = 1 }, new { retryDelayMs = 2500 });
            await host.State(retry, JobState.Scheduled);
            var active = await host.Create("succeed-after", new { delayMs = 6500 });
            await host.State(active, JobState.Processing);
            var pending = await host.Create("log-message", new { message = "after reconnection" });
            var runAt = DateTimeOffset.UtcNow.AddSeconds(12);
            var future = await host.Create("log-message", new { message = "future" }, new { runAt });
            await host.StopRedis(force: true);
            await ProcessHost.Until(async () => { using var response = await host.Http.GetAsync("/health"); return response.StatusCode == HttpStatusCode.ServiceUnavailable; });
            await ProcessHost.Until(async () => (await host.Events()).Any(e => e.GetProperty("jobId").GetString() == active && e.GetProperty("type").GetString() == "finished"));
            Assert.DoesNotContain(await host.Events(), e => e.GetProperty("jobId").GetString() == pending && e.GetProperty("type").GetString() == "started");
            Assert.False(host.Demo.HasExited); Assert.Equal(pid, host.Demo.Id);
            await host.StartRedis(host.DataDirectory);
            await ProcessHost.Until(async () => { using var response = await host.Http.GetAsync("/health"); return response.StatusCode == HttpStatusCode.OK; });
            var completed = await host.State(active, JobState.Succeeded);
            Assert.Single(completed.AttemptHistory); Assert.Equal(JobAttemptOutcome.Succeeded, completed.AttemptHistory[0].Outcome);
            await host.State(pending, JobState.Succeeded);
            var retried = await host.State(retry, JobState.Succeeded);
            Assert.Equal(2, retried.Attempts);
            Assert.True(retried.AttemptHistory[1].StartedAt >= retried.AttemptHistory[0].FinishedAt!.Value.AddMilliseconds(2500));
            var scheduled = await host.State(future, JobState.Succeeded);
            Assert.True(scheduled.AttemptHistory[0].StartedAt >= runAt);
            Assert.Single(await host.Events(), e => e.GetProperty("jobId").GetString() == active && e.GetProperty("type").GetString() == "started");
            output.WriteLine($"Same demo PID {pid}; Redis restarted with AOF; health 503 -> 200; live result confirmed once; pending, expired retry and future schedule completed.");
        }
        finally { output.WriteLine(host.Log); }
    }

    [Theory]
    [InlineData("TERM")] [InlineData("INT")]
    public async Task Stop_during_redis_outage_leaves_pending_work_for_successor(string signal)
    {
        await using var host = new ProcessHost();
        try
        {
            await host.Start();
            var active = await host.Create("succeed-after", new { delayMs = 6000 });
            await host.State(active, JobState.Processing);
            var pending = await host.Create("log-message", new { message = "successor only" });
            await host.StopRedis(force: true);
            await ProcessHost.Until(async () => { using var response = await host.Http.GetAsync("/health"); return response.StatusCode == HttpStatusCode.ServiceUnavailable; });
            await host.SignalDemo(signal);
            await host.Demo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, host.Demo.ExitCode);
            await host.StartRedis(host.DataDirectory);
            await ProcessHost.Until(async () => host.Connection.IsConnected);
            Assert.Equal(JobState.Enqueued, (await host.Manager.GetAsync(pending))!.State);
            Assert.Equal(JobState.Processing, (await host.Manager.GetAsync(active))!.State);
            await host.StartDemo();
            await host.State(pending, JobState.Succeeded);
            var recovered = await host.State(active, JobState.Succeeded);
            Assert.Equal(JobAttemptOutcome.Interrupted, recovered.AttemptHistory[0].Outcome);
            Assert.Equal(2, recovered.Attempts);
            output.WriteLine($"SIG{signal} during suspension drained handler and exited; unconfirmed attempt recovered by successor; pending never claimed by stopping process.");
        }
        finally { output.WriteLine(host.Log); }
    }
}
