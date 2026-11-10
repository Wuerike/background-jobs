using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs.IntegrationTests;

public sealed partial class ProcessingTests
{
    private async Task<ScheduledCandidate> Scheduled(string id = "a", DateTimeOffset? due = null)
    {
        await store.CreateAsync(Request(id, new() { RunAt = due ?? now.AddTicks(12345) }));
        return Assert.Single(await store.ReadScheduledAsync(due ?? now.AddTicks(12345), 0), c => c.Job.Id == id);
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00.0000001+00:00")]
    [InlineData("1969-12-31T23:59:59.9999999+00:00")]
    [InlineData("1970-01-01T00:00:00.0000000+00:00")]
    [InlineData("2026-11-10T12:00:00.0012345+00:00")]
    [InlineData("9999-12-31T23:59:59.9999999+00:00")]
    public async Task Promotion_observes_exact_ticks_across_supported_date_range(string date)
    {
        var due = DateTimeOffset.Parse(date);
        var request = new NewJob("a", new JobPreparation(registry.Contracts, new Clock(due.AddTicks(-1)))
            .Prepare(definition, new Args("exact"), new() { RunAt = due.ToOffset(TimeSpan.Zero) }));
        await store.CreateAsync(request);
        var batch = await store.ReadScheduledAsync(due, 0);
        var candidate = Assert.Single(batch);
        var before = await Dump();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TryPromoteAsync(candidate, due.AddTicks(-1)));
        Assert.Equal(before, await Dump());
        Assert.Equal(PromotionResult.Promoted, await store.TryPromoteAsync(candidate, due));
        var job = await Detail();
        Assert.Equal(JobState.Enqueued, job.State); Assert.Null(job.ScheduledAt);
        Assert.Equal(0, job.Attempts); Assert.Empty(job.AttemptHistory);
        Assert.Equal(new[] { JobState.Scheduled, JobState.Enqueued }, job.History.Select(h => h.State));
        Assert.Equal(due, job.UpdatedAt); Assert.Equal("schedule eligible", job.History[^1].Reason);
        before = await Dump();
        Assert.Equal(PromotionResult.LostRace, await store.TryPromoteAsync(candidate, due));
        await store.CreateAsync(request);
        Assert.Equal(before, await Dump());
        foreach (var key in connection.GetServer(connection.GetEndPoints()[0]).Keys(db.Database, prefix + ":*"))
            Assert.Null(await db.KeyTimeToLiveAsync(key));
    }

    [Fact]
    public async Task Bounded_selection_can_advance_past_future_candidates_with_equal_score()
    {
        var due = now.AddTicks(10001);
        for (var i = 0; i < 129; i++) await store.CreateAsync(Request("a" + i.ToString("D3"), new() { RunAt = due.AddTicks(1) }));
        await store.CreateAsync(Request("z", new() { RunAt = due }));
        var first = await store.ReadScheduledAsync(due, 0);
        Assert.Equal(128, first.Count); Assert.All(first, c => Assert.True(c.Job.ScheduledAt > due));
        var next = await store.ReadScheduledAsync(due, first.Count);
        Assert.Equal(2, next.Count);
        var eligible = Assert.Single(next, c => c.Job.ScheduledAt <= due);
        await store.TryPromoteAsync(eligible, due);
        Assert.Equal(129, await db.SortedSetLengthAsync(prefix + ":scheduled"));
        Assert.Equal("z", (await store.PeekAsync())!.Job.Id);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task Promotion_wrong_key_types_reject_without_partial_writes(int index)
    {
        var candidate = await Scheduled();
        RedisKey[] keys = [.. store.Keys("a"), prefix + ":processing"];
        await db.KeyDeleteAsync(keys[index]);
        if (index == 6) await db.SetAddAsync(keys[index], "bad");
        else await db.StringSetAsync(keys[index], "bad");
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.TryPromoteAsync(candidate, now.AddSeconds(1)));
        Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData("00")] [InlineData("-1")] [InlineData("1.0")]
    [InlineData("9007199254740991")] [InlineData("9007199254740992")]
    public async Task Promotion_invalid_or_exhausted_sequence_rejects_without_writes(string sequence)
    {
        var candidate = await Scheduled();
        await db.StringSetAsync(prefix + ":sequence", sequence);
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.TryPromoteAsync(candidate, now.AddSeconds(1)));
        Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData("jobs")] [InlineData("scheduled")] [InlineData("score")]
    [InlineData("ready")] [InlineData("processing")] [InlineData("snapshot")]
    public async Task Promotion_corrupt_indexes_and_changed_snapshot_fail_without_writes(string corruption)
    {
        var candidate = await Scheduled();
        switch (corruption)
        {
            case "jobs": await db.SetRemoveAsync(prefix + ":jobs", "a"); break;
            case "scheduled": await db.SortedSetRemoveAsync(prefix + ":scheduled", "a"); break;
            case "score": await db.SortedSetAddAsync(prefix + ":scheduled", "a", candidate.Score + 1); break;
            case "ready": await db.SortedSetAddAsync(prefix + ":ready", "a", 1); break;
            case "processing": await db.SetAddAsync(prefix + ":processing", "a"); break;
            default: await db.HashSetAsync(prefix + ":job:a", "args", "{}"); break;
        }
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.TryPromoteAsync(candidate, now.AddSeconds(1)));
        Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData("budget")] [InlineData("open")] [InlineData("score")]
    public async Task Selection_rejects_invalid_scheduled_metadata_without_writes(string corruption)
    {
        var claim = await Claim(options: new() { RetryDelayMs = 0 });
        await store.CompleteAsync(new(claim, now.AddTicks(2), "error"));
        if (corruption == "budget") await db.HashSetAsync(prefix + ":job:a", "attempts", "3");
        if (corruption == "open") await db.ListSetByIndexAsync(prefix + ":job:a:attempts", 0,
            "{\"attempt\":1,\"startedAt\":\"2026-11-10T12:00:00.0000001+00:00\",\"finishedAt\":null,\"outcome\":null,\"error\":null}");
        if (corruption == "score") await db.SortedSetAddAsync(prefix + ":scheduled", "a", 1);
        var before = await Dump();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadScheduledAsync(now.AddSeconds(1), 0));
        Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Retry_reentry_rejects_old_generation_before_snapshot_or_claim(bool beforeSnapshot)
    {
        await store.CreateAsync(Request("a", new() { RetryDelayMs = 0 }));
        var old = (await store.PeekAsync())!;
        var read = Signal(); var release = Signal(); var paused = 0;
        async Task<SortedSetEntry[]> Pause(Task<SortedSetEntry[]> response)
        { var result = await response; read.SetResult(); await release.Task; return result; }
        var proxyDb = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            var result = proxy.Call(method, args);
            return method.Name == nameof(IDatabase.SortedSetRangeByRankWithScoresAsync) && Interlocked.Increment(ref paused) == 1
                ? Pause((Task<SortedSetEntry[]>)result!) : result;
        });
        var proxyConnection = ForwardingProxy.Create<IConnectionMultiplexer>(connection, (proxy, method, args) =>
            method.Name == nameof(IConnectionMultiplexer.GetDatabase) ? proxyDb : proxy.Call(method, args));
        var peek = beforeSnapshot ? new RedisJobStore(proxyConnection, prefix).PeekAsync() : null;
        try
        {
            if (peek is not null) await read.Task.WaitAsync(Timeout);
            var winner = (await store.TryClaimAsync(old, now.AddTicks(1), "winner"))!;
            var completion = new AttemptCompletion(winner, now.AddTicks(2), "error");
            await store.CompleteAsync(completion);
            var scheduled = Assert.Single(await store.ReadScheduledAsync(now.AddTicks(2), 0));
            await store.TryPromoteAsync(scheduled, now.AddTicks(2));
            var before = await Dump();
            await store.CompleteAsync(completion); // Same completion remains confirmable after promotion.
            Assert.Null(await store.TryClaimAsync(old, now.AddTicks(3), "loser"));
            release.TrySetResult();
            if (peek is not null) Assert.Null(await peek.WaitAsync(Timeout));
            Assert.Equal(before, await Dump());
            var current = (await store.PeekAsync())!;
            Assert.True(current.Position > old.Position);
            var next = (await store.TryClaimAsync(current, now.AddTicks(3), "next"))!;
            Assert.Equal(2, next.Attempt);
            before = await Dump();
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CompleteAsync(completion));
            Assert.Equal(before, await Dump());
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(1, 0)] [InlineData(3, 0)] [InlineData(10, 0)] [InlineData(3, 500)]
    public async Task Library_executes_retry_budget_and_preserves_all_transitions(int max, int delay)
    {
        var execution = new JobRegistry(); var calls = new List<int>();
        execution.Register(definition, async (_, context) =>
        { calls.Add(context.Attempt); await Task.Yield(); if (context.Attempt < max) throw new Exception("retry"); });
        IJobClient client = new RedisJobClient(execution.Contracts, connection, prefix);
        IJobManager manager = new RedisJobManager(connection, prefix);
        var id = await client.EnqueueAsync(definition, new Args("retry"), new() { MaxAttempts = max, RetryDelayMs = delay });
        await using var server = new JobServer(execution, connection, prefix, workerCount: 2);
        await server.StartAsync();
        await Until(async () => (await manager.GetAsync(id))!.State == JobState.Succeeded);
        await server.StopAsync();
        var job = (await manager.GetAsync(id))!;
        Assert.Equal(Enumerable.Range(1, max), calls); Assert.Equal(max, job.Attempts);
        Assert.Equal(max, job.AttemptHistory.Count); Assert.Equal(3 * max, job.History.Count);
        Assert.All(job.AttemptHistory.SkipLast(1), a => Assert.Equal("retry", a.Error));
        for (var i = 1; i < max; i++) Assert.True(job.AttemptHistory[i].StartedAt >= job.AttemptHistory[i-1].FinishedAt!.Value.AddMilliseconds(delay));
        Assert.Null(job.ScheduledAt); Assert.Null(server.LastError);
        Assert.Equal(0, await db.SortedSetLengthAsync(prefix + ":ready"));
        Assert.Equal(0, await db.SortedSetLengthAsync(prefix + ":scheduled"));
        Assert.Equal(0, await db.SetLengthAsync(prefix + ":processing"));
    }

    [Theory]
    [InlineData(1000)] [InlineData(60000)]
    public async Task Retry_delay_is_measured_from_persisted_finish_with_controlled_clock(int delay)
    {
        var claim = await Claim(options: new() { RetryDelayMs = delay });
        var finish = now.AddTicks(12);
        await store.CompleteAsync(new(claim, finish, "error"));
        var due = finish.AddMilliseconds(delay);
        var scheduled = Assert.Single(await store.ReadScheduledAsync(due, 0));
        var before = await Dump();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TryPromoteAsync(scheduled, due.AddTicks(-1)));
        Assert.Equal(before, await Dump());
        await store.TryPromoteAsync(scheduled, due);
        Assert.Equal(claim.Job.CreatedAt, (await Detail()).CreatedAt);
        Assert.Equal(1, (await Detail()).Attempts);
        Assert.Equal(2, (await store.TryClaimAsync((await store.PeekAsync())!, due, "retry"))!.Attempt);
    }

    [Fact]
    public async Task Scheduler_drains_multiple_batches_while_worker_busy_and_preserves_fifo()
    {
        var entered = Signal(); var release = Signal(); var execution = new JobRegistry();
        var calls = new List<string>();
        execution.Register(definition, async (args, _) =>
        { calls.Add(args.Message); if (args.Message == "active") { entered.SetResult(); await release.Task; } });
        var client = new RedisJobClient(execution.Contracts, connection, prefix);
        await client.EnqueueAsync(definition, new Args("active"));
        await using var server = new JobServer(execution, connection, prefix, workerCount: 1);
        try
        {
            await server.StartAsync(); await entered.Task.WaitAsync(Timeout);
            await client.EnqueueAsync(definition, new Args("ready"));
            for (var i = 0; i < 130; i++) await store.CreateAsync(Request("due-" + i, new() { RunAt = now.AddSeconds(1) }));
            await store.CreateAsync(Request("future", new() { RunAt = DateTimeOffset.UtcNow.AddHours(1) }));
            await Until(async () => await db.SortedSetLengthAsync(prefix + ":ready") == 131);
            Assert.Equal(new[] { "active" }, calls); Assert.Equal(JobState.Scheduled, (await Detail("future")).State);
            release.SetResult();
            await Until(async () => await db.SortedSetLengthAsync(prefix + ":ready") == 0 && await db.SetLengthAsync(prefix + ":processing") == 0);
            await server.StopAsync();
            Assert.Equal("ready", calls[1]); Assert.Equal(132, calls.Count); Assert.Null(server.LastError);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Clean_stop_persists_failed_attempt_and_next_instance_continues_retry()
    {
        var entered = Signal(); var release = Signal(); var execution = new JobRegistry(); var calls = new List<int>();
        execution.Register(definition, async (_, context) =>
        { calls.Add(context.Attempt); if (context.Attempt == 1) { entered.SetResult(); await release.Task; throw new Exception("error"); } });
        var client = new RedisJobClient(execution.Contracts, connection, prefix);
        var id = await client.EnqueueAsync(definition, new Args("retry"), new() { RetryDelayMs = 0 });
        await using var server = new JobServer(execution, connection, prefix, workerCount: 1);
        try
        {
            await server.StartAsync(); await entered.Task.WaitAsync(Timeout);
            var stop = server.StopAsync(); release.SetResult(); await stop.WaitAsync(Timeout);
            Assert.Equal(JobState.Scheduled, (await store.GetAsync(id))!.State); Assert.Equal(new[] { 1 }, calls);
            await using var next = new JobServer(execution, connection, prefix, workerCount: 1);
            await next.StartAsync(); await Until(async () => (await store.GetAsync(id))!.State == JobState.Succeeded);
            await next.StopAsync(); Assert.Equal(new[] { 1, 2 }, calls); Assert.Null(next.LastError);
        }
        finally { release.TrySetResult(); }
    }
}

public sealed partial class ProcessingTests
{
    private sealed class AdvancingClock(DateTimeOffset at) : TimeProvider
    {
        private long ticks = at.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan delay) => Interlocked.Add(ref ticks, delay.Ticks);
    }

    [Fact]
    public async Task Library_schedules_future_and_starts_past_equal_and_overdue_jobs()
    {
        var clock = new AdvancingClock(now); var execution = new JobRegistry();
        var starts = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();
        execution.Register(definition, (_, context) => { starts[context.JobId] = clock.GetUtcNow(); return Task.CompletedTask; });
        IJobClient client = new RedisJobClient(execution.Contracts, connection, prefix, clock);
        var future = await client.EnqueueAsync(definition, new Args("future"), new() { RunAt = now.AddMinutes(1) });
        var past = await client.EnqueueAsync(definition, new Args("past"), new() { RunAt = now.AddSeconds(-1) });
        var equal = await client.EnqueueAsync(definition, new Args("equal"), new() { RunAt = now });
        var overdue = await client.EnqueueAsync(definition, new Args("overdue"), new() { RunAt = now.AddTicks(1) });
        Assert.Equal(JobState.Enqueued, (await store.GetAsync(past))!.State);
        Assert.Equal(JobState.Enqueued, (await store.GetAsync(equal))!.State);
        clock.Advance(TimeSpan.FromTicks(1));
        await using var server = new JobServer(execution, connection, prefix, clock, workerCount: 1);
        await server.StartAsync();
        await Until(() => Task.FromResult(starts.Count == 3));
        Assert.False(starts.ContainsKey(future)); Assert.Equal(JobState.Scheduled, (await store.GetAsync(future))!.State);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Until(async () => (await store.GetAsync(future))!.State == JobState.Succeeded);
        await server.StopAsync(); Assert.True(starts[future] >= now.AddMinutes(1)); Assert.Null(server.LastError);
    }

    [Theory]
    [InlineData("sync")] [InlineData("async")] [InlineData("json")] [InlineData("validation")]
    public async Task Argument_and_function_failures_use_retry_budget_without_blocking_other_jobs(string failure)
    {
        var execution = new JobRegistry(); var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        execution.Register(definition, (args, context) =>
        {
            calls.Enqueue(context.JobId);
            if (context.JobId == "bad") return failure == "sync" ? throw new Exception("sync") : Task.FromException(new Exception("async"));
            return Task.CompletedTask;
        }, args => { if (args.Message == "bad") throw new JobValidationException("invalid"); });
        await store.CreateAsync(Request("bad", new() { MaxAttempts = 2, RetryDelayMs = 0 }));
        await store.CreateAsync(Request("healthy"));
        if (failure is "json" or "validation") await db.HashSetAsync(prefix + ":job:bad", "args", failure == "json" ? "{}" : "{\"message\":\"bad\"}");
        await using var server = new JobServer(execution, connection, prefix, workerCount: 2);
        await server.StartAsync();
        await Until(async () => (await Detail("bad")).State == JobState.Failed && (await Detail("healthy")).State == JobState.Succeeded);
        Assert.True(server.IsAvailable); await server.StopAsync();
        var job = await Detail("bad");
        Assert.Equal(2, job.Attempts); Assert.All(job.AttemptHistory, a => Assert.NotNull(a.Error));
        Assert.Equal(failure is "json" or "validation" ? 0 : 2, calls.Count(id => id == "bad"));
        Assert.Single(calls, id => id == "healthy"); Assert.Null(server.LastError);
    }

    [Fact]
    public async Task Retry_enters_behind_already_ready_jobs_and_preserves_creation_and_attempts()
    {
        var first = await Claim(options: new() { RetryDelayMs = 0 });
        var completion = new AttemptCompletion(first, now.AddTicks(2), "failure");
        await store.CompleteAsync(completion);
        await store.CreateAsync(Request("ready"));
        var scheduled = Assert.Single(await store.ReadScheduledAsync(now.AddTicks(2), 0));
        await store.TryPromoteAsync(scheduled, now.AddTicks(2));
        Assert.Equal("ready", (await store.PeekAsync())!.Job.Id);
        var healthy = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddTicks(3), "healthy"))!;
        await store.CompleteAsync(new(healthy, now.AddTicks(4), null));
        Assert.Equal("a", (await store.PeekAsync())!.Job.Id);
        var retried = await Detail();
        Assert.Equal(1, retried.Attempts); Assert.Equal(first.Job.CreatedAt, retried.CreatedAt);
        Assert.Equal("failure", Assert.Single(retried.AttemptHistory).Error);
        var before = await Dump();
        Assert.Equal(PromotionResult.LostRace, await store.TryPromoteAsync(scheduled, now.AddTicks(5)));
        Assert.Equal(before, await Dump());
    }
}

public sealed partial class ProcessingTests
{
    [Theory]
    [InlineData(2)] [InlineData(4)]
    public async Task Concurrent_retries_preserve_worker_limit_and_exclusivity_per_job(int workers)
    {
        var firstWave = Signal(); var active = 0; var peak = 0;
        var perJob = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var invocations = new System.Collections.Concurrent.ConcurrentDictionary<(string, int), int>();
        var execution = new JobRegistry(); var gate = new object();
        execution.Register(definition, async (_, context) =>
        {
            Assert.Equal(1, perJob.AddOrUpdate(context.JobId, 1, (_, n) => n+1));
            invocations.AddOrUpdate((context.JobId, context.Attempt), 1, (_, n) => n+1);
            var running = Interlocked.Increment(ref active);
            lock (gate) peak = Math.Max(peak, running);
            if (running == workers) firstWave.TrySetResult();
            try { await firstWave.Task; await Task.Yield(); if (context.Attempt < 3) throw new Exception("retry"); }
            finally { perJob.AddOrUpdate(context.JobId, 0, (_, n) => n-1); Interlocked.Decrement(ref active); }
        });
        for (var i = 0; i < 6; i++) await store.CreateAsync(Request(i.ToString(), new() { RetryDelayMs = 0 }));
        await using var server = new JobServer(execution, connection, prefix, workerCount: workers);
        try
        {
            await server.StartAsync(); await firstWave.Task.WaitAsync(Timeout);
            await Until(async () =>
            {
                var jobs = await Task.WhenAll(Enumerable.Range(0,6).Select(i => Detail(i.ToString())));
                return jobs.All(j => j.State == JobState.Succeeded);
            });
            await server.StopAsync();
            Assert.Equal(workers, peak); Assert.Equal(0, active); Assert.All(perJob.Values, n => Assert.Equal(0, n));
            Assert.Equal(18, invocations.Count); Assert.All(invocations.Values, n => Assert.Equal(1, n));
            Assert.Null(server.LastError);
        }
        finally { firstWave.TrySetResult(); }
    }

    [Fact]
    public async Task Single_worker_observes_fifo_for_immediate_scheduled_and_retried_entries()
    {
        var firstEntered = Signal(); var releaseFirst = Signal(); var secondEntered = Signal(); var releaseSecond = Signal();
        var calls = new List<(string, int)>(); var execution = new JobRegistry();
        execution.Register(definition, async (_, context) =>
        {
            calls.Add((context.JobId, context.Attempt));
            if (context.JobId == "a" && context.Attempt == 1)
            { firstEntered.SetResult(); await releaseFirst.Task; throw new Exception("retry"); }
            if (context.JobId == "b") { secondEntered.SetResult(); await releaseSecond.Task; }
        });
        await store.CreateAsync(Request("a", new() { RetryDelayMs = 0 }));
        await store.CreateAsync(Request("b"));
        await store.CreateAsync(Request("c", new() { RunAt = now.AddTicks(1) }));
        await using var server = new JobServer(execution, connection, prefix, workerCount: 1);
        try
        {
            await server.StartAsync(); await firstEntered.Task.WaitAsync(Timeout);
            Assert.Equal(JobState.Enqueued, (await Detail("c")).State);
            releaseFirst.SetResult(); await secondEntered.Task.WaitAsync(Timeout);
            await Until(async () => (await Detail("a")).State == JobState.Enqueued);
            releaseSecond.SetResult(); await Until(async () => (await Detail("a")).State == JobState.Succeeded);
            await server.StopAsync();
            Assert.Equal(new[] { ("a", 1), ("b", 1), ("c", 1), ("a", 2) }, calls); Assert.Null(server.LastError);
        }
        finally { releaseFirst.TrySetResult(); releaseSecond.TrySetResult(); }
    }
}

public sealed partial class ProcessingTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Promotion_progress_proof_does_not_hide_corrupted_payload_or_indexes(bool indexes)
    {
        var candidate = await Scheduled();
        await store.TryPromoteAsync(candidate, now.AddSeconds(1));
        if (indexes) await db.SortedSetRemoveAsync(prefix + ":ready", "a");
        else await db.HashSetAsync(prefix + ":job:a", "args", "{}");
        var before = await Dump();
        await Assert.ThrowsAsync<RedisServerException>(() => store.TryPromoteAsync(candidate, now.AddSeconds(2)));
        Assert.Equal(before, await Dump());
    }
}

public sealed partial class ProcessingTests
{
    [Fact]
    public async Task Concurrent_promotions_assign_one_fifo_position_and_one_history_entry()
    {
        var candidate = await Scheduled();
        var results = await Task.WhenAll(Enumerable.Range(0,8).Select(_ => store.TryPromoteAsync(candidate, now.AddSeconds(1))));
        Assert.Single(results, r => r == PromotionResult.Promoted);
        Assert.Equal(7, results.Count(r => r == PromotionResult.LostRace));
        Assert.Equal(2, (await Detail()).History.Count);
        Assert.Equal("1", (await db.StringGetAsync(prefix + ":sequence")).ToString());
    }

    [Fact]
    public async Task Running_workers_discard_old_generation_while_retry_is_again_at_head_without_fatal_error()
    {
        await store.CreateAsync(Request("a", new() { RetryDelayMs = 0 }));
        var oldRead = Signal(); var releaseOld = Signal(); var newRead = Signal(); var releaseNew = Signal();
        var secondEntered = Signal(); var releaseHandler = Signal(); var oldPaused = 0; var newPaused = 0;
        var calls = new System.Collections.Concurrent.ConcurrentQueue<int>();
        async Task<SortedSetEntry[]> Pause(Task<SortedSetEntry[]> response)
        {
            var head = await response;
            if (head.Length == 0) return head;
            if (Interlocked.CompareExchange(ref oldPaused, 1, 0) == 0)
            { oldRead.TrySetResult(); await releaseOld.Task; }
            else if (head[0].Score > 1 && Interlocked.CompareExchange(ref newPaused, 1, 0) == 0)
            { newRead.TrySetResult(); await releaseNew.Task; }
            return head;
        }
        var proxyDb = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            var result = proxy.Call(method, args);
            return method.Name == nameof(IDatabase.SortedSetRangeByRankWithScoresAsync) ? Pause((Task<SortedSetEntry[]>)result!) : result;
        });
        var proxyConnection = ForwardingProxy.Create<IConnectionMultiplexer>(connection, (proxy, method, args) =>
            method.Name == nameof(IConnectionMultiplexer.GetDatabase) ? proxyDb : proxy.Call(method, args));
        var execution = new JobRegistry();
        execution.Register(definition, async (_, context) =>
        {
            calls.Enqueue(context.Attempt);
            if (context.Attempt == 1) throw new Exception("retry");
            secondEntered.TrySetResult(); await releaseHandler.Task;
        });
        await using var server = new JobServer(execution, new RedisJobStore(proxyConnection, prefix), workerCount: 2);
        try
        {
            await server.StartAsync(); await oldRead.Task.WaitAsync(Timeout); await newRead.Task.WaitAsync(Timeout);
            Assert.Equal(JobState.Enqueued, (await Detail()).State);
            releaseOld.SetResult(); await secondEntered.Task.WaitAsync(Timeout); releaseNew.SetResult();
            Assert.True(server.IsAvailable); Assert.Null(server.LastError);
            releaseHandler.SetResult(); await Until(async () => (await Detail()).State == JobState.Succeeded);
            await server.StopAsync(); Assert.Equal(new[] { 1, 2 }, calls); Assert.Null(server.LastError);
        }
        finally { releaseOld.TrySetResult(); releaseNew.TrySetResult(); releaseHandler.TrySetResult(); }
    }
}
