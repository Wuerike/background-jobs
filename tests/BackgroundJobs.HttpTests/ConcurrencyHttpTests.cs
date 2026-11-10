using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using BackgroundJobs.Demo.Events;
using BackgroundJobs.Demo.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

namespace BackgroundJobs.HttpTests;

public sealed partial class ConcurrencyHttpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public class ConnectionProxy : DispatchProxy
    {
        public IConnectionMultiplexer Connection = null!;
        public IDatabase Database = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IConnectionMultiplexer.GetDatabase)) return Database;
            // The test owns this connection and drains the host before cleaning its keys.
            if (method.Name is nameof(IDisposable.Dispose) or nameof(IAsyncDisposable.DisposeAsync))
                return method.ReturnType == typeof(ValueTask) ? ValueTask.CompletedTask : null;
            try { return method.Invoke(Connection, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
    }
    private sealed class Factory(IConnectionMultiplexer connection, string? setting,
        Action<IServiceCollection>? configure) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["WORKER_COUNT"] = setting }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IConnectionMultiplexer>(); services.AddSingleton(connection);
                configure?.Invoke(services);
            });
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        public readonly string Isolation = "http-concurrency-" + Guid.NewGuid().ToString("N") + ":";
        public ConnectionMultiplexer Connection = null!;
        public HttpClient Http = null!;
        public JobServer Server = null!;
        public string Prefix => Isolation + "background-jobs";
        private Factory? factory;
        public async Task Start(string? setting, Action<IServiceCollection>? configure = null)
        {
            Connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "127.0.0.1:6379");
            var connection = DispatchProxy.Create<IConnectionMultiplexer, ConnectionProxy>();
            var proxy = (ConnectionProxy)(object)connection;
            proxy.Connection = Connection; proxy.Database = Connection.GetDatabase().WithKeyPrefix(Isolation);
            factory = new Factory(connection, setting, configure);
            Http = factory.CreateClient(); Server = factory.Services.GetRequiredService<JobServer>();
        }
        public async Task<string> Create(string name, object args)
        {
            using var response = await Http.PostAsJsonAsync("/demo/jobs", new { job = name, @params = args });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await Body(response)).GetProperty("jobId").GetString()!;
        }
        public async Task<JsonElement> Detail(string id)
        {
            using var response = await Http.GetAsync("/api/jobs/" + id);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await Body(response);
        }
        public async Task WaitFinished(IEnumerable<string> ids)
        {
            using var timeout = new CancellationTokenSource(Timeout);
            while (true)
            {
                var details = await Task.WhenAll(ids.Select(Detail));
                if (details.All(j => j.GetProperty("state").GetString() == "succeeded")) return;
                await Task.Delay(10, timeout.Token);
            }
        }
        public async Task<JsonElement[]> Events()
        {
            using var response = await Http.GetAsync("/demo/events");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await Body(response)).GetProperty("items").EnumerateArray().ToArray();
        }
        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (factory is not null) await factory.DisposeAsync();
            if (Connection is null) return;
            try
            {
                var keys = Connection.GetServer(Connection.GetEndPoints()[0]).Keys(Connection.GetDatabase().Database, Isolation + "*").ToArray();
                if (keys.Length > 0) await Connection.GetDatabase().KeyDeleteAsync(keys);
            }
            finally { await Connection.DisposeAsync(); }
        }
    }
    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    private static void AssertSuccess(JsonElement job)
    {
        Assert.Equal("succeeded", job.GetProperty("state").GetString()); Assert.Equal(1, job.GetProperty("attempts").GetInt32());
        Assert.Equal(new[] { "enqueued", "processing", "succeeded" }, job.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("state").GetString()));
        var attempt = Assert.Single(job.GetProperty("attemptHistory").EnumerateArray());
        Assert.Equal(1, attempt.GetProperty("attempt").GetInt32()); Assert.Equal("succeeded", attempt.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("error").ValueKind); Assert.Equal(JsonValueKind.String, attempt.GetProperty("finishedAt").ValueKind);
    }
    private static int Peak(JsonElement[] events, IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(); var running = new HashSet<string>(); var peak = 0;
        foreach (var e in events)
        {
            var id = e.GetProperty("jobId").GetString()!;
            Assert.Contains(id, ids); Assert.Equal(1, e.GetProperty("attempt").GetInt32());
            if (e.GetProperty("type").GetString() == "started")
            {
                Assert.True(seen.Add(id), "Duplicate invocation: " + id); Assert.True(running.Add(id)); peak = Math.Max(peak, running.Count);
            }
            else if (e.GetProperty("type").GetString() == "finished")
            {
                Assert.True(running.Remove(id)); Assert.Equal("ok", e.GetProperty("detail").GetProperty("outcome").GetString());
            }
        }
        Assert.Empty(running); Assert.Equal(ids.Order(), seen.Order()); return peak;
    }

    [Fact]
    public async Task Program_with_one_worker_emits_fifo_messages_and_single_attempts()
    {
        await using var host = new Host(); await host.Start("1"); var ids = new List<string>();
        foreach (var message in new[] { "A", "B", "C" }) ids.Add(await host.Create("log-message", new { message }));
        await host.WaitFinished(ids);
        var events = await host.Events(); Assert.Equal(9, events.Length); Assert.Equal(1, Peak(events, ids));
        Assert.Equal(new[] { "A", "B", "C" }, events.Where(e => e.GetProperty("type").GetString() == "message").Select(e => e.GetProperty("detail").GetProperty("message").GetString()));
        foreach (var id in ids)
        {
            AssertSuccess(await host.Detail(id));
            Assert.Equal(new[] { "started", "message", "finished" }, events.Where(e => e.GetProperty("jobId").GetString() == id).Select(e => e.GetProperty("type").GetString()));
        }
    }

    [Theory]
    [InlineData("2", 2)] [InlineData(null, 4)] [InlineData("4", 4)]
    public async Task Program_configures_real_server_and_demo_waits_overlap_within_the_worker_limit(string? setting, int workers)
    {
        await using var host = new Host(); await host.Start(setting); var ids = new List<string>();
        for (var i = 0; i < 6; i++) ids.Add(await host.Create("succeed-after", new { delayMs = 750 }));
        await host.WaitFinished(ids);
        var events = await host.Events(); Assert.Equal(12, events.Length); Assert.Equal(workers, Peak(events, ids));
        foreach (var id in ids) AssertSuccess(await host.Detail(id));
        Assert.True(host.Server.IsAvailable); Assert.Null(host.Server.LastError);
    }

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("abc")] [InlineData("1.5")] [InlineData("1e1")]
    [InlineData("0")] [InlineData("-2")] [InlineData("2147483648")]
    public async Task Invalid_worker_configuration_fails_host_start_with_configuration_diagnostic(string setting)
    {
        await using var host = new Host();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => host.Start(setting));
        Assert.Contains("WORKER_COUNT", error.ToString());
        Assert.Equal(0, await host.Connection.GetDatabase().SetLengthAsync(host.Prefix + ":jobs"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Shutdown_or_fatal_failure_removes_health_and_drains_both_active_functions(bool fatal)
    {
        var entered = Channel.CreateUnbounded<string>();
        var releases = new ConcurrentDictionary<string, TaskCompletionSource>();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Host();
        await host.Start("2", services =>
        {
            var registry = new JobRegistry();
            registry.Register(DemoJobs.SucceedAfter, async (_, context) =>
            {
                var release = releases.GetOrAdd(context.JobId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
                entered.Writer.TryWrite(context.JobId); await Task.WhenAny(release.Task, cleanup.Task);
            }, DemoJobValidation.ValidateSucceedAfter);
            services.RemoveAll<JobRegistry>(); services.AddSingleton(registry);
        });
        try
        {
            var ids = new List<string>();
            for (var i = 0; i < 4; i++) ids.Add(await host.Create("succeed-after", new { delayMs = 0 }));
            var activeIds = new List<string>();
            for (var i = 0; i < 2; i++) activeIds.Add(await entered.Reader.ReadAsync().AsTask().WaitAsync(Timeout));
            var db = host.Connection.GetDatabase();
            Task stop;
            if (fatal)
            {
                await db.HashSetAsync(host.Prefix + ":job:" + activeIds[0], "attemptToken", "invalid");
                releases[activeIds[0]].SetResult();
                using var timeout = new CancellationTokenSource(Timeout);
                while (host.Server.LastError is null) await Task.Delay(10, timeout.Token);
                stop = host.Server.StopAsync(); Assert.False(stop.IsCompleted);
            }
            else { stop = host.Server.StopAsync(); Assert.False(stop.IsCompleted); }
            using var health = await host.Http.GetAsync("/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
            if (!fatal)
            {
                foreach (var release in releases.Values) release.TrySetResult();
                await stop.WaitAsync(Timeout);
                foreach (var id in activeIds) AssertSuccess(await host.Detail(id));
            }
            else
            {
                releases[activeIds[1]].SetResult(); await stop.WaitAsync(Timeout); Assert.NotNull(host.Server.LastError);
                AssertSuccess(await host.Detail(activeIds[1]));
                var uncertain = await host.Detail(activeIds[0]);
                Assert.Equal("processing", uncertain.GetProperty("state").GetString());
                Assert.Equal(JsonValueKind.Null, uncertain.GetProperty("attemptHistory")[0].GetProperty("error").ValueKind);
            }
            foreach (var id in ids.Except(activeIds))
            {
                var job = await host.Detail(id); Assert.Equal("enqueued", job.GetProperty("state").GetString());
                Assert.Equal(0, job.GetProperty("attempts").GetInt32()); Assert.Empty(job.GetProperty("attemptHistory").EnumerateArray());
            }
        }
        finally { cleanup.TrySetResult(); }
    }
}
