using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BackgroundJobs.Demo.Events;
using BackgroundJobs.Demo.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace BackgroundJobs.HttpTests;

public sealed class ExecutionHttpTests
{
    private sealed class Factory(Action<IServiceCollection> configure) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(configure);
    }
    private sealed class Host : IAsyncDisposable
    {
        public readonly string Prefix = "http-execution-" + Guid.NewGuid().ToString("N");
        public ConnectionMultiplexer Connection = null!;
        public HttpClient Http = null!;
        public JobServer Server = null!;
        private Factory factory = null!;
        public async Task Start(Action<JobRegistry, DemoEvents>? register = null, int workerCount = 1)
        {
            Connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "127.0.0.1:6379");
            var registry = new JobRegistry(); var events = new DemoEvents();
            if (register is null) new DemoJobHandlers(events).Register(registry);
            else register(registry, events);
            factory = new Factory(services =>
            {
                services.RemoveAll<IConnectionMultiplexer>(); services.AddSingleton<IConnectionMultiplexer>(Connection);
                services.RemoveAll<JobRegistry>(); services.AddSingleton(registry);
                services.RemoveAll<DemoEvents>(); services.AddSingleton(events);
                services.RemoveAll<IJobClient>(); services.AddSingleton<IJobClient>(new RedisJobClient(registry.Contracts, Connection, Prefix));
                services.RemoveAll<IJobManager>(); services.AddSingleton<IJobManager>(new RedisJobManager(Connection, Prefix));
                services.RemoveAll<JobServer>(); services.AddSingleton(_ => new JobServer(registry, Connection, Prefix, workerCount: workerCount));
            });
            Http = factory.CreateClient();
            Server = factory.Services.GetRequiredService<JobServer>();
        }
        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (factory is not null) await factory.DisposeAsync();
            if (Connection is null) return;
            try
            {
                var keys = Connection.GetServer(Connection.GetEndPoints()[0]).Keys(Connection.GetDatabase().Database, Prefix + ":*").ToArray();
                if (keys.Length > 0) await Connection.GetDatabase().KeyDeleteAsync(keys);
            }
            finally { await Connection.DisposeAsync(); }
        }
        public async Task<string> Create(string name, object args, object? options = null)
        {
            using var response = await Http.PostAsJsonAsync("/demo/jobs", new { job = name, @params = args, options = options ?? new { } });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await Body(response)).GetProperty("jobId").GetString()!;
        }
        public async Task<JsonElement> WaitState(string id, string state)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                using var response = await Http.GetAsync("/api/jobs/" + id, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var job = await Body(response);
                if (job.GetProperty("state").GetString() == state) return job;
                await Task.Delay(10, timeout.Token);
            }
        }
    }
    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Acceptance_success_terminal_failure_continuity_retry_pending_and_real_start_latency()
    {
        await using var host = new Host(); await host.Start();
        using var health = await host.Http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok", (await Body(health)).GetProperty("status").GetString());
        var start = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var successId = await host.Create("succeed-after", new { delayMs = 0 });
        var success = await host.WaitState(successId, "succeeded");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"Ready job completed in {elapsed.Elapsed}.");
        Assert.Equal(1, success.GetProperty("attempts").GetInt32());
        Assert.Equal(new[] { "enqueued", "processing", "succeeded" }, success.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("state").GetString()));
        var attempt = Assert.Single(success.GetProperty("attemptHistory").EnumerateArray());
        Assert.Equal("succeeded", attempt.GetProperty("outcome").GetString());
        Assert.Null(attempt.GetProperty("error").GetString());
        Assert.True(attempt.GetProperty("startedAt").GetDateTimeOffset() >= start);
        var failureId = await host.Create("fail-n-times", new { failTimes = 1 }, new { maxAttempts = 1 });
        var failure = await host.WaitState(failureId, "failed");
        Assert.Equal(1, failure.GetProperty("attempts").GetInt32());
        Assert.Equal("demo failure", failure.GetProperty("attemptHistory")[0].GetProperty("error").GetString());
        var messageId = await host.Create("log-message", new { message = " Olá 🦊 " });
        await host.WaitState(messageId, "succeeded");
        var retryId = await host.Create("fail-n-times", new { failTimes = 1 }, new { maxAttempts = 3, retryDelayMs = 500 });
        var retry = await host.WaitState(retryId, "scheduled");
        Assert.Equal(1, retry.GetProperty("attempts").GetInt32());
        Assert.Equal(retry.GetProperty("attemptHistory")[0].GetProperty("finishedAt").GetDateTimeOffset().AddMilliseconds(500), retry.GetProperty("scheduledAt").GetDateTimeOffset());
        using var response = await host.Http.GetAsync("/demo/events");
        var events = (await Body(response)).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(9, events.Length);
        foreach (var e in events)
        {
            Assert.Equal(new[] { "at", "attempt", "detail", "jobId", "type" }, e.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(1, e.GetProperty("attempt").GetInt32());
            Assert.Equal(TimeSpan.Zero, e.GetProperty("at").GetDateTimeOffset().Offset);
        }
        Assert.Equal(new[] { "started", "finished" }, events.Where(e => e.GetProperty("jobId").GetString() == successId).Select(e => e.GetProperty("type").GetString()));
        Assert.Equal("error", events.Single(e => e.GetProperty("jobId").GetString() == failureId && e.GetProperty("type").GetString() == "finished").GetProperty("detail").GetProperty("outcome").GetString());
        Assert.Equal(" Olá 🦊 ", events.Single(e => e.GetProperty("type").GetString() == "message").GetProperty("detail").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Processing_snapshot_and_shutdown_health_are_observable_while_http_remains_available()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Host();
        await host.Start((registry, events) => registry.Register(DemoJobs.SucceedAfter, async (_, context) =>
        {
            events.Add("started", context); entered.SetResult(); await release.Task; events.Add("finished", context, new { outcome = "ok" });
        }, DemoJobValidation.ValidateSucceedAfter));
        try
        {
            var id = await host.Create("succeed-after", new { delayMs = 0 });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var job = await host.WaitState(id, "processing");
            Assert.Equal(1, job.GetProperty("attempts").GetInt32());
            var attempt = Assert.Single(job.GetProperty("attemptHistory").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, attempt.GetProperty("finishedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, attempt.GetProperty("outcome").ValueKind);
            var stop = host.Server.StopAsync(); Assert.False(stop.IsCompleted);
            using var response = await host.Http.GetAsync("/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("unavailable", (await Body(response)).GetProperty("status").GetString());
            release.SetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(3));
            await host.WaitState(id, "succeeded");
        }
        finally { release.TrySetResult(); }
    }

    public static IEnumerable<object[]> InvalidInputs()
    {
        foreach (var (name, field, max) in new[] { ("succeed-after", "delayMs", 60000), ("fail-n-times", "failTimes", 10) })
        {
            foreach (var value in new[] { "null", "\"1\"", "true", "1.0", "1e0", "-1", (max+1).ToString(), "2147483648" })
                yield return ["{\"job\":\"" + name + "\",\"params\":{\"" + field + "\":" + value + "}}"];
            foreach (var args in new[] { "{}", "null", "[]", "{\"" + field + "\":0,\"" + field + "\":1}", "{\"" + field + "\":0,\"other\":0}", "{\"" + field.ToUpperInvariant() + "\":0}" })
                yield return ["{\"job\":\"" + name + "\",\"params\":" + args + "}"];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task New_job_contracts_reject_invalid_input_without_persisting(string json)
    {
        await using var host = new Host(); await host.Start();
        using var response = await host.Http.PostAsync("/demo/jobs", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await Body(response)).GetProperty("error").GetString()));
        Assert.Equal(0, await host.Connection.GetDatabase().SetLengthAsync(host.Prefix + ":jobs"));
    }

    [Fact]
    public async Task Events_are_isolated_per_host_and_fatal_storage_errors_remove_health()
    {
        await using var first = new Host(); await first.Start();
        await using var second = new Host(); await second.Start();
        var id = await first.Create("log-message", new { message = "first only" });
        await first.WaitState(id, "succeeded");
        using var empty = await second.Http.GetAsync("/demo/events");
        Assert.Empty((await Body(empty)).GetProperty("items").EnumerateArray());
        // An invalid queue type forces a real polling infrastructure failure.
        await first.Connection.GetDatabase().StringSetAsync(first.Prefix + ":ready", "invalid");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (first.Server.LastError is null) await Task.Delay(10, timeout.Token);
        using var health = await first.Http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Equal("unavailable", (await Body(health)).GetProperty("status").GetString());
    }
}
