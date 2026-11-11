using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using BackgroundJobs.Dashboard;

namespace BackgroundJobs.IntegrationTests;

public sealed partial class ProcessingTests
{
    [Fact]
    public async Task Dashboard_uses_its_configured_storage_without_replacing_the_application_manager()
    {
        await store.CreateAsync(Request("a"));
        IJobManager applicationManager = new RedisJobManager(connection, prefix + "-other");
        var services = new ServiceCollection();
        services.AddSingleton(applicationManager);
        services.AddSingleton<IConnectionMultiplexer>(connection);
        services.AddBackgroundJobsDashboard(prefix);
        await using var provider = services.BuildServiceProvider();
        Assert.Same(applicationManager, provider.GetRequiredService<IJobManager>());
        Assert.Empty(await applicationManager.ListAsync());
        Assert.NotNull(await provider.GetRequiredService<RedisJobManager>().GetAsync("a"));
        Assert.Equal(JobOperationResult.Applied, await provider.GetRequiredService<RedisJobManager>().DeleteAsync("a"));
        Assert.Equal(JobState.Deleted, (await Detail()).State);
    }

    [Fact]
    public async Task Monitoring_reads_all_states_without_changing_processing_or_recovering_attempts()
    {
        foreach (var state in new[] { JobState.Succeeded, JobState.Failed, JobState.Deleted,
                     JobState.Processing, JobState.Enqueued, JobState.Scheduled }) await InState(state, state.ToString());
        var before = await Dump();
        var observation = await new RedisJobMonitor(connection, prefix).ReadAsync();
        Assert.Equal(6, observation.Items.Count);
        Assert.All(observation.Counts.Values, count => Assert.Equal(1, count));
        Assert.Equal(before, await Dump());
        var active = Assert.Single(observation.Items, item => item.State == JobState.Processing);
        Assert.NotNull(active.LastAttempt);
        Assert.Null(active.LastAttempt.Outcome);
        Assert.Null((await Detail("Processing")).AttemptHistory[0].Outcome);
        Assert.Empty((await new RedisJobMonitor(connection, prefix + "-other").ReadAsync()).Items);
    }

    [Fact]
    public async Task Monitoring_classifies_retry_and_requeue_without_losing_last_attempt()
    {
        var claim = await Claim("a", new() { MaxAttempts = 2 });
        await store.CompleteAsync(new(claim, now.AddSeconds(1), "failure"));
        var monitor = new RedisJobMonitor(connection, prefix);
        var item = Assert.Single((await monitor.ReadAsync()).Items);
        Assert.True(item.IsRetry); Assert.Equal("failure", item.LastAttempt!.Error);
        await Manager(now.AddSeconds(2)).DeleteAsync("a");
        await Manager(now.AddSeconds(3)).RequeueAsync("a");
        item = Assert.Single((await monitor.ReadAsync()).Items);
        Assert.False(item.IsRetry); Assert.Equal(0, item.Attempts);
        Assert.Equal("failure", item.LastAttempt!.Error);
    }

    [Theory]
    [InlineData("id")] [InlineData("name")] [InlineData("state")] [InlineData("attempts")]
    [InlineData("maxAttempts")] [InlineData("retryDelayMs")] [InlineData("createdAt")]
    [InlineData("updatedAt")] [InlineData("scheduledAt")] [InlineData("history")]
    [InlineData("missing")] [InlineData("type")]
    public async Task Monitoring_never_returns_partial_success_for_corrupt_projection(string field)
    {
        await store.CreateAsync(Request("a"));
        if (field == "history") await db.ListSetByIndexAsync(prefix + ":job:a:history", 0, "{}");
        else if (field == "missing") await db.KeyDeleteAsync(prefix + ":job:a");
        else if (field == "type") { await db.KeyDeleteAsync(prefix + ":job:a"); await db.StringSetAsync(prefix + ":job:a", "invalid"); }
        else await db.HashSetAsync(prefix + ":job:a", field, field == "name" ? " " : "invalid");
        await Assert.ThrowsAnyAsync<Exception>(() => new RedisJobMonitor(connection, prefix).ReadAsync());
    }

    [Fact]
    public async Task Monitoring_bounds_volume_and_shares_one_compact_observation_between_sessions()
    {
        for (var i = 0; i < 1_000; i++) await store.CreateAsync(Request($"volume-{i:D4}"));
        var monitor = new RedisJobMonitor(connection, prefix);
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.ReadAsync(999));
        var cache = new DashboardObservationCache(monitor, new());
        var watch = Stopwatch.StartNew();
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => cache.ReadAsync()));
        watch.Stop();
        Assert.All(snapshots, observation => Assert.Equal(1_000, observation.Items.Count));
        Assert.Same(snapshots[0], snapshots[1]); Assert.Same(snapshots[0], snapshots[2]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Compact observation took {watch.Elapsed}.");
        Assert.Equal("volume-0000", snapshots[0].Query(new(PageSize: 10)).Items[0].Id);
        await Manager().DeleteAsync("volume-0000");
        cache.Invalidate();
        Assert.Equal(1, (await cache.ReadAsync()).Counts[JobState.Deleted]);
        cache.Dispose();
    }

    [Fact]
    public async Task Monitoring_under_concurrent_management_keeps_each_item_consistent()
    {
        for (var i = 0; i < 30; i++) await store.CreateAsync(Request($"race-{i:D2}"));
        var monitor = new RedisJobMonitor(connection, prefix);
        var reads = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                var snapshot = await monitor.ReadAsync();
                Assert.Equal(30, snapshot.Items.Count);
                Assert.Equal(30, snapshot.Counts.Values.Sum());
                Assert.Equal(30, snapshot.Items.Select(item => item.Id).Distinct().Count());
            }
        });
        foreach (var i in Enumerable.Range(0, 30))
        {
            await Manager().DeleteAsync($"race-{i:D2}");
            await Manager().RequeueAsync($"race-{i:D2}");
        }
        await reads;
    }
}
