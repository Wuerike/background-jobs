using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs.IntegrationTests;

public sealed partial class ProcessingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!await condition()) await Task.Delay(10, timeout.Token);
    }
    private async Task AssertFinished(string id, JobState state = JobState.Succeeded)
    {
        var job = await Detail(id);
        Assert.Equal(state, job.State); Assert.Equal(1, job.Attempts);
        Assert.Equal(new[] { JobState.Enqueued, JobState.Processing, state }, job.History.Select(h => h.State));
        var attempt = Assert.Single(job.AttemptHistory);
        Assert.NotNull(attempt.FinishedAt); Assert.NotNull(attempt.Outcome);
        Assert.Equal(state == JobState.Succeeded ? JobAttemptOutcome.Succeeded : JobAttemptOutcome.Failed, attempt.Outcome);
        Assert.False(await db.SetContainsAsync(prefix + ":processing", id));
        Assert.Null(await db.SortedSetScoreAsync(prefix + ":ready", id));
        if (state != JobState.Scheduled) Assert.Null(await db.SortedSetScoreAsync(prefix + ":scheduled", id));
        Assert.True(await db.SetContainsAsync(prefix + ":jobs", id));
    }

    [Fact]
    public async Task Single_worker_uses_persisted_fifo_sequence_when_times_match_and_ids_sort_differently()
    {
        var ids = new[] { "z", "a", "m" }; var calls = new List<string>(); var execution = new JobRegistry();
        execution.Register(definition, (_, context) => { calls.Add(context.JobId); return Task.CompletedTask; });
        foreach (var id in ids) await store.CreateAsync(Request(id));
        Assert.All(await Task.WhenAll(ids.Select(id => Detail(id))), job => Assert.Equal(now, job.CreatedAt));
        await using var server = new JobServer(execution, connection, prefix, new Clock(now), workerCount: 1);
        await server.StartAsync(); await Until(async () => (await Detail("m")).State == JobState.Succeeded); await server.StopAsync();
        Assert.Equal(ids, calls);
        foreach (var id in ids) await AssertFinished(id);
    }

    [Theory]
    [InlineData(2)] [InlineData(4)]
    public async Task Concurrent_execution_has_consistent_snapshots_and_clean_stop_preserves_pending_for_restart(int workers)
    {
        var ids = Enumerable.Range(0, workers + 3).Select(i => "job-" + i).ToArray();
        foreach (var id in ids) await store.CreateAsync(Request(id));
        var execution = new JobRegistry(); var entered = Channel.CreateUnbounded<string>();
        var release = Signal(); var active = 0; var peak = 0; var gate = new object();
        var invocations = new ConcurrentDictionary<string, int>();
        execution.Register(definition, async (_, context) =>
        {
            invocations.AddOrUpdate(context.JobId, 1, (_, n) => n + 1);
            var running = Interlocked.Increment(ref active); lock (gate) peak = Math.Max(peak, running);
            entered.Writer.TryWrite(context.JobId);
            try { await release.Task; } finally { Interlocked.Decrement(ref active); }
        });
        await using var server = new JobServer(execution, connection, prefix, workerCount: workers);
        try
        {
            await server.StartAsync();
            var started = new HashSet<string>();
            for (var i = 0; i < workers; i++) started.Add(await entered.Reader.ReadAsync().AsTask().WaitAsync(Timeout));
            Assert.Equal(workers, started.Count); Assert.Equal(workers, peak); Assert.Equal(workers, active);
            Assert.Equal(workers, await db.SetLengthAsync(prefix + ":processing"));
            Assert.Equal(3, await db.SortedSetLengthAsync(prefix + ":ready"));
            // Every query observes a whole per-job transition, even while siblings process.
            foreach (var id in ids)
            {
                var job = await Detail(id);
                Assert.Equal(job.State, job.History[^1].State); Assert.Equal(job.UpdatedAt, job.History[^1].At);
                if (started.Contains(id))
                {
                    Assert.Equal(JobState.Processing, job.State); Assert.Equal(1, job.Attempts);
                    var attempt = Assert.Single(job.AttemptHistory); Assert.Null(attempt.FinishedAt); Assert.Null(attempt.Outcome);
                }
                else { Assert.Equal(JobState.Enqueued, job.State); Assert.Equal(0, job.Attempts); Assert.Empty(job.AttemptHistory); }
            }
            var stop = server.StopAsync(); Assert.False(stop.IsCompleted); Assert.False(server.IsAvailable);
            release.SetResult(); await stop.WaitAsync(Timeout);
            foreach (var id in started) await AssertFinished(id);
            foreach (var id in ids.Except(started))
            {
                var pending = await Detail(id); Assert.Equal(JobState.Enqueued, pending.State);
                Assert.Equal(0, pending.Attempts); Assert.Empty(pending.AttemptHistory);
            }
            Assert.True(connection.IsConnected); Assert.Equal("PONG", (string?)await db.ExecuteAsync("PING"));
            await using var restarted = new JobServer(execution, connection, prefix, workerCount: workers);
            await restarted.StartAsync();
            await Until(async () => (await Task.WhenAll(ids.Select(id => Detail(id)))).All(j => j.State == JobState.Succeeded));
            await restarted.StopAsync();
            Assert.Equal(ids.Length, invocations.Count); Assert.All(invocations.Values, n => Assert.Equal(1, n));
            Assert.Equal(workers, peak); Assert.Null(server.LastError); Assert.Null(restarted.LastError);
            foreach (var id in ids) await AssertFinished(id);
            Assert.Equal(ids.Length, await db.SetLengthAsync(prefix + ":jobs"));
            Assert.Equal(0, await db.SortedSetLengthAsync(prefix + ":ready"));
            Assert.Equal(0, await db.SortedSetLengthAsync(prefix + ":scheduled")); Assert.Equal(0, await db.SetLengthAsync(prefix + ":processing"));
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(0)] [InlineData(500)]
    public async Task Parallel_attempt_failures_preserve_retry_eligibility_and_allow_healthy_jobs(int delay)
    {
        var execution = new JobRegistry();
        execution.Register(definition, async (_, context) => { await Task.Yield(); if (context.JobId != "healthy") throw new Exception("demo failure"); });
        await store.CreateAsync(Request("terminal", new() { MaxAttempts = 1 }));
        await store.CreateAsync(Request("retry", new() { RetryDelayMs = delay }));
        await store.CreateAsync(Request("healthy"));
        await using var server = new JobServer(execution, connection, prefix, workerCount: 2);
        await server.StartAsync();
        await Until(async () => (await Detail("healthy")).State == JobState.Succeeded &&
            (await Detail("retry")).State == JobState.Scheduled && (await Detail("terminal")).State == JobState.Failed);
        await server.StopAsync();
        await AssertFinished("healthy"); await AssertFinished("terminal", JobState.Failed); await AssertFinished("retry", JobState.Scheduled);
        var retry = await Detail("retry");
        Assert.Equal(retry.AttemptHistory[0].FinishedAt!.Value.AddMilliseconds(delay), retry.ScheduledAt);
        Assert.Equal("demo failure", retry.AttemptHistory[0].Error); Assert.Null(server.LastError);
    }

    // Forward the real Redis interfaces, pausing only the response containing the queue head.
    // This reproduces a winner moving that head before Peek reads its persisted snapshot.
    public class ForwardingProxy : DispatchProxy
    {
        public object Target = null!;
        public Func<MethodInfo, object?[]?, object?> Forward = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Forward(method!, args);
        public object? Call(MethodInfo method, object?[]? args)
        {
            try { return method.Invoke(Target, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
        public static T Create<T>(T target, Func<ForwardingProxy, MethodInfo, object?[]?, object?> forward) where T : class
        {
            var result = Create<T, ForwardingProxy>(); var proxy = (ForwardingProxy)(object)result;
            proxy.Target = target; proxy.Forward = (method, args) => forward(proxy, method, args); return result;
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Peek_head_can_be_claimed_and_completed_before_snapshot_without_spurious_failure(bool complete)
    {
        await store.CreateAsync(Request("a")); await store.CreateAsync(Request("b"));
        var read = Signal(); var release = Signal(); var paused = 0;
        async Task<RedisValue[]> Pause(Task<RedisValue[]> response)
        {
            var result = await response; read.SetResult(); await release.Task; return result;
        }
        var proxyDb = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            var result = proxy.Call(method, args);
            return method.Name == nameof(IDatabase.SortedSetRangeByRankAsync) && Interlocked.Increment(ref paused) == 1
                ? Pause((Task<RedisValue[]>)result!) : result;
        });
        var proxyConnection = ForwardingProxy.Create<IConnectionMultiplexer>(connection, (proxy, method, args) =>
            method.Name == nameof(IConnectionMultiplexer.GetDatabase) ? proxyDb : proxy.Call(method, args));
        var reader = new RedisJobStore(proxyConnection, prefix);
        var peek = reader.PeekAsync();
        try
        {
            await read.Task.WaitAsync(Timeout);
            var candidate = Assert.IsType<JobCandidate>(await store.PeekAsync());
            var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.TryClaimAsync(candidate, now, "race-" + i)));
            var winner = Assert.Single(claims, c => c is not null)!;
            if (complete) await store.CompleteAsync(new(winner, now, null));
            release.SetResult(); Assert.Null(await peek.WaitAsync(Timeout));
            Assert.Equal("b", (await reader.PeekAsync())!.Job.Id);
            if (!complete) await store.CompleteAsync(new(winner, now, null));
            await AssertFinished("a");
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Real_storage_failure_stops_siblings_and_allows_admitted_work_to_finish_when_storage_is_repaired()
    {
        var entered = Signal(); var release = Signal(); var execution = new JobRegistry(); var calls = new ConcurrentQueue<string>();
        execution.Register(definition, async (_, context) => { calls.Enqueue(context.JobId); entered.SetResult(); await release.Task; });
        await store.CreateAsync(Request("active"));
        await using var server = new JobServer(execution, connection, prefix, workerCount: 2);
        try
        {
            await server.StartAsync(); await entered.Task.WaitAsync(Timeout);
            await db.StringSetAsync(prefix + ":ready", "invalid type");
            await Until(() => Task.FromResult(server.LastError is not null));
            var error = server.LastError; Assert.False(server.IsAvailable);
            var stop = server.StopAsync(); Assert.False(stop.IsCompleted);
            await db.KeyDeleteAsync(prefix + ":ready");
            await store.CreateAsync(Request("pending"));
            release.SetResult(); await stop.WaitAsync(Timeout);
            Assert.Same(error, server.LastError); Assert.Equal(new[] { "active" }, calls);
            await AssertFinished("active");
            var pending = await Detail("pending"); Assert.Equal(JobState.Enqueued, pending.State); Assert.Empty(pending.AttemptHistory);
        }
        finally { release.TrySetResult(); }
    }
}
