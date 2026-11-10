using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BackgroundJobs.HttpTests;

public sealed partial class ConcurrencyHttpTests
{
    private static async Task<string> CreateWithOptions(Host host, string job, object args, object options)
    {
        using var response = await host.Http.PostAsJsonAsync("/demo/jobs", new { job, @params = args, options });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Body(response)).GetProperty("jobId").GetString()!;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Real_composition_schedules_future_without_early_start_and_past_immediately(bool past)
    {
        await using var host = new Host(); await host.Start("1");
        var due = DateTimeOffset.UtcNow.AddSeconds(past ? -2 : 2);
        var elapsed = Stopwatch.StartNew();
        var id = await CreateWithOptions(host, "succeed-after", new { delayMs = 0 }, new { runAt = due.ToOffset(TimeSpan.FromHours(3)) });
        if (!past)
        {
            var pending = await host.Detail(id);
            Assert.Equal("scheduled", pending.GetProperty("state").GetString());
            Assert.Equal(due, pending.GetProperty("scheduledAt").GetDateTimeOffset());
            Assert.Equal(0, pending.GetProperty("attempts").GetInt32());
        }
        await host.WaitFinished([id]);
        var job = await host.Detail(id);
        Assert.Equal("succeeded", job.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("scheduledAt").ValueKind);
        var attempt = Assert.Single(job.GetProperty("attemptHistory").EnumerateArray());
        var started = attempt.GetProperty("startedAt").GetDateTimeOffset();
        Assert.True(started >= due);
        if (past) Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), elapsed.Elapsed.ToString());
        else Assert.InRange(started - due, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Equal(past ? new[] { "enqueued", "processing", "succeeded" } : new[] { "scheduled", "enqueued", "processing", "succeeded" },
            job.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("state").GetString()));
        using var response = await host.Http.GetAsync("/demo/events");
        var events = (await Body(response)).GetProperty("items").EnumerateArray().Where(e => e.GetProperty("jobId").GetString() == id).ToArray();
        Assert.Equal(new[] { "started", "finished" }, events.Select(e => e.GetProperty("type").GetString()));
        Assert.True(events[0].GetProperty("at").GetDateTimeOffset() >= due);
        Assert.Null(host.Server.LastError);
    }

    [Theory]
    [InlineData(2, 500)] [InlineData(10, 500)] [InlineData(10, -1)]
    public async Task Real_composition_retries_to_success_or_exhaustion_without_blocking_healthy_job(int failTimes, int delay)
    {
        await using var host = new Host(); await host.Start("2");
        var id = delay < 0 ? await host.Create("fail-n-times", new { failTimes }) :
            await CreateWithOptions(host, "fail-n-times", new { failTimes }, new { maxAttempts = 3, retryDelayMs = delay });
        var healthy = await host.Create("log-message", new { message = "healthy" });
        using (var timeout = new CancellationTokenSource(Timeout))
        {
            while ((await host.Detail(id)).GetProperty("state").GetString() is not ("succeeded" or "failed"))
                await Task.Delay(10, timeout.Token);
        }
        await host.WaitFinished([healthy]);
        var job = await host.Detail(id);
        Assert.Equal(failTimes == 2 ? "succeeded" : "failed", job.GetProperty("state").GetString());
        Assert.Equal("succeeded", (await host.Detail(healthy)).GetProperty("state").GetString());
        Assert.Equal(3, job.GetProperty("attempts").GetInt32());
        Assert.Equal(3, job.GetProperty("maxAttempts").GetInt32());
        var expectedDelay = delay < 0 ? 1000 : delay;
        Assert.Equal(expectedDelay, job.GetProperty("retryDelayMs").GetInt32());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("scheduledAt").ValueKind);
        var attempts = job.GetProperty("attemptHistory").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, attempts.Select(a => a.GetProperty("attempt").GetInt32()));
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(i == 2 && failTimes == 2 ? "succeeded" : "failed", attempts[i].GetProperty("outcome").GetString());
            Assert.Equal(i == 2 && failTimes == 2 ? null : "demo failure", attempts[i].GetProperty("error").GetString());
            if (i > 0) Assert.True(attempts[i].GetProperty("startedAt").GetDateTimeOffset() - attempts[i-1].GetProperty("finishedAt").GetDateTimeOffset() >= TimeSpan.FromMilliseconds(expectedDelay));
        }
        Assert.Equal(new[] { "enqueued", "processing", "scheduled", "enqueued", "processing", "scheduled", "enqueued", "processing", failTimes == 2 ? "succeeded" : "failed" },
            job.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("state").GetString()));
        using var response = await host.Http.GetAsync("/demo/events");
        var events = (await Body(response)).GetProperty("items").EnumerateArray().Where(e => e.GetProperty("jobId").GetString() == id).ToArray();
        Assert.Equal(6, events.Length);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("started", events[2*i].GetProperty("type").GetString());
            Assert.Equal("finished", events[2*i+1].GetProperty("type").GetString());
            Assert.Equal(i+1, events[2*i].GetProperty("attempt").GetInt32());
            if (i > 0) Assert.True(events[2*i].GetProperty("at").GetDateTimeOffset() - events[2*i-1].GetProperty("at").GetDateTimeOffset() >= TimeSpan.FromMilliseconds(expectedDelay));
        }
        Assert.Null(host.Server.LastError);
    }
}
