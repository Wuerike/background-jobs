using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs.IntegrationTests;

public sealed partial class ProcessingTests
{
    private IConnectionMultiplexer WithDatabase(IDatabase database) => ForwardingProxy.Create<IConnectionMultiplexer>(connection,
        (proxy, method, args) => method.Name == nameof(IConnectionMultiplexer.GetDatabase) ? database : proxy.Call(method, args));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Deleted_ready_generation_loses_before_snapshot_or_claim_even_when_same_id_returns(bool beforeSnapshot)
    {
        await store.CreateAsync(Request("a")); var old = (await store.PeekAsync())!;
        var entered = Signal(); var release = Signal(); var once = 0;
        async Task<SortedSetEntry[]> Pause(Task<SortedSetEntry[]> response)
        { var result = await response; entered.TrySetResult(); await release.Task; return result; }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            var result = proxy.Call(method, args);
            return method.Name == nameof(IDatabase.SortedSetRangeByRankWithScoresAsync) && Interlocked.Increment(ref once) == 1
                ? Pause((Task<SortedSetEntry[]>)result!) : result;
        });
        var pending = beforeSnapshot ? new RedisJobStore(WithDatabase(database), prefix).PeekAsync() : null;
        try
        {
            if (pending is not null) await entered.Task.WaitAsync(Timeout);
            await Manager().DeleteAsync("a"); await Manager().RequeueAsync("a");
            var before = await Dump();
            Assert.Null(await store.TryClaimAsync(old, now.AddSeconds(2), "old"));
            release.TrySetResult(); if (pending is not null) Assert.Null(await pending.WaitAsync(Timeout));
            Assert.Equal(before, await Dump());
            var current = (await store.PeekAsync())!;
            Assert.True(current.Position > old.Position); Assert.Equal("1", current.Cycle);
            var claim = (await store.TryClaimAsync(current, now.AddSeconds(2), "current"))!;
            await store.CompleteAsync(new(claim, now.AddSeconds(3), null));
            Assert.Single((await Detail()).AttemptHistory);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Running_worker_loses_claim_after_confirmed_deletion_and_continues_healthy_work()
    {
        await store.CreateAsync(Request("a"));
        var entered = Signal(); var release = Signal(); var once = 0;
        async Task<RedisResult> Pause(Func<Task<RedisResult>> invoke)
        { entered.TrySetResult(); await release.Task; return await invoke(); }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            if (method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args![0] is string script && script.Contains("Invalid claim arguments") && Interlocked.Increment(ref once) == 1)
                return Pause(() => (Task<RedisResult>)proxy.Call(method, args)!);
            return proxy.Call(method, args);
        });
        var calls = new List<string>(); var execution = new JobRegistry();
        execution.Register(definition, (_, context) => { calls.Add(context.JobId); return Task.CompletedTask; });
        await using var server = new JobServer(execution, new RedisJobStore(WithDatabase(database), prefix), workerCount: 1);
        try
        {
            await server.StartAsync(); await entered.Task.WaitAsync(Timeout);
            Assert.Equal(JobOperationResult.Applied, await new RedisJobManager(connection, prefix).DeleteAsync("a"));
            await store.CreateAsync(Request("healthy")); release.SetResult();
            await Until(async () => (await Detail("healthy")).State == JobState.Succeeded);
            Assert.True(server.IsAvailable); Assert.Null(server.LastError);
            await server.StopAsync(); Assert.Equal(new[] { "healthy" }, calls);
            Assert.Empty((await Detail()).AttemptHistory); Assert.Equal(JobState.Deleted, (await Detail()).State);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Reserved_job_conflicts_without_cancelling_handler_or_breaking_server()
    {
        var entered = Signal(); var release = Signal(); var execution = new JobRegistry();
        execution.Register(definition, async (_, _) => { entered.SetResult(); await release.Task; });
        await store.CreateAsync(Request("a"));
        await using var server = new JobServer(execution, connection, prefix, workerCount: 1);
        try
        {
            await server.StartAsync(); await entered.Task.WaitAsync(Timeout);
            var manager = new RedisJobManager(connection, prefix); var before = await Dump();
            Assert.Equal(JobOperationResult.Conflict, await manager.DeleteAsync("a"));
            Assert.Equal(JobOperationResult.Conflict, await manager.RequeueAsync("a")); Assert.Equal(before, await Dump());
            release.SetResult(); await Until(async () => (await Detail()).State == JobState.Succeeded);
            Assert.True(server.IsAvailable); Assert.Null(server.LastError);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Scheduled_selection_or_promotion_discards_confirmed_deletion(bool beforeSnapshot)
    {
        var candidate = await Scheduled(); var entered = Signal(); var release = Signal();
        async Task<SortedSetEntry[]> Pause(Task<SortedSetEntry[]> response)
        { var result = await response; entered.SetResult(); await release.Task; return result; }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            var result = proxy.Call(method, args);
            return method.Name == nameof(IDatabase.SortedSetRangeByScoreWithScoresAsync) ? Pause((Task<SortedSetEntry[]>)result!) : result;
        });
        var pending = beforeSnapshot ? new RedisJobStore(WithDatabase(database), prefix).ReadScheduledAsync(now.AddSeconds(1), 0) : null;
        try
        {
            if (pending is not null) await entered.Task.WaitAsync(Timeout);
            await Manager().DeleteAsync("a"); var before = await Dump();
            release.TrySetResult();
            if (pending is not null)
            {
                var batch = await pending.WaitAsync(Timeout); Assert.Empty(batch); Assert.Equal(1, batch.ExaminedCount);
            }
            Assert.Equal(PromotionResult.LostRace, await store.TryPromoteAsync(candidate, now.AddSeconds(2)));
            Assert.Equal(before, await Dump()); Assert.Null(await store.PeekAsync());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Promotion_winner_remains_deletable_and_old_candidate_cannot_revive_it()
    {
        var candidate = await Scheduled(); await store.TryPromoteAsync(candidate, now.AddSeconds(1));
        await Manager().DeleteAsync("a"); var before = await Dump();
        Assert.Equal(PromotionResult.LostRace, await store.TryPromoteAsync(candidate, now.AddSeconds(2)));
        Assert.Equal(before, await Dump()); Assert.Null(await store.PeekAsync());
        await Manager().RequeueAsync("a"); before = await Dump();
        Assert.Equal(PromotionResult.LostRace, await store.TryPromoteAsync(candidate, now.AddSeconds(2)));
        Assert.Equal(before, await Dump());
    }

    [Fact]
    public async Task Old_retry_candidate_cannot_promote_new_cycle_even_with_identical_due_and_score()
    {
        var claim = await Claim(options: new() { RetryDelayMs = 0 });
        await store.CompleteAsync(new(claim, now.AddTicks(2), "old error"));
        var old = Assert.Single(await store.ReadScheduledAsync(now.AddTicks(2), 0));
        var manager = Manager(now.AddTicks(2)); await manager.DeleteAsync("a"); await manager.RequeueAsync("a");
        var first = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddTicks(2), "new"))!;
        Assert.Equal(1, first.Attempt); await store.CompleteAsync(new(first, now.AddTicks(2), "new error"));
        var current = Assert.Single(await store.ReadScheduledAsync(now.AddTicks(2), 0));
        Assert.Equal(old.Score, current.Score); Assert.Equal(old.Job.ScheduledAt, current.Job.ScheduledAt);
        var before = await Dump();
        Assert.Equal(PromotionResult.LostRace, await store.TryPromoteAsync(old, now.AddTicks(2))); Assert.Equal(before, await Dump());
        Assert.Equal(PromotionResult.Promoted, await store.TryPromoteAsync(current, now.AddTicks(2)));
        // Deleting a retry of a manually restarted cycle preserves both failures.
        await manager.DeleteAsync("a"); await manager.RequeueAsync("a");
        var job = await Detail(); Assert.Equal(0, job.Attempts); Assert.Equal(2, job.AttemptHistory.Count);
        Assert.Equal(new[] { "old error", "new error" }, job.AttemptHistory.Select(a => a.Error));
    }

    [Fact]
    public async Task Old_completion_confirms_only_its_result_without_changing_new_cycle()
    {
        var old = new AttemptCompletion(await Claim(), now.AddTicks(2), null); await store.CompleteAsync(old);
        await Manager().RequeueAsync("a");
        async Task Reject()
        {
            var before = await Dump(); await store.CompleteAsync(old); Assert.Equal(before, await Dump());
        }
        await Reject();
        var next = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddSeconds(2), "new"))!;
        await Reject(); await store.CompleteAsync(new(next, now.AddSeconds(3), null)); await Reject();
        Assert.Equal(new[] { 1, 1 }, (await Detail()).AttemptHistory.Select(a => a.Attempt));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Simultaneous_manual_mutations_have_one_winner_without_duplicate_history(bool requeue)
    {
        await InState(requeue ? JobState.Deleted : JobState.Enqueued);
        var allEntered = Signal(); var release = Signal(); var entries = 0;
        async Task<RedisResult> Pause(Func<Task<RedisResult>> invoke)
        { if (Interlocked.Increment(ref entries) == 8) allEntered.SetResult(); await release.Task; return await invoke(); }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            if (method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args![0] is string script && script.Contains("Invalid management arguments"))
                return Pause(() => (Task<RedisResult>)proxy.Call(method, args)!);
            return proxy.Call(method, args);
        });
        var manager = new RedisJobManager(WithDatabase(database), prefix, new Clock(now.AddSeconds(1)));
        var before = await Detail();
        var pending = Enumerable.Range(0, 8).Select(_ => requeue ? manager.RequeueAsync("a") : manager.DeleteAsync("a")).ToArray();
        try
        {
            await allEntered.Task.WaitAsync(Timeout); release.SetResult(); var results = await Task.WhenAll(pending).WaitAsync(Timeout);
            Assert.Single(results, r => r == JobOperationResult.Applied); Assert.Equal(7, results.Count(r => r == JobOperationResult.Conflict));
            var after = await Detail(); Assert.Equal(before.History.Count + 1, after.History.Count);
            if (requeue) Assert.Equal("1", (await db.HashGetAsync(prefix + ":job:a", "cycle")).ToString());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Scheduler_advances_multiple_pages_after_entire_selected_page_is_deleted()
    {
        var due = now.AddTicks(10001);
        for (var i = 0; i < 129; i++) await store.CreateAsync(Request("a" + i.ToString("D3"), new() { RunAt = due.AddTicks(1) }));
        for (var i = 0; i < 140; i++) await store.CreateAsync(Request("z" + i.ToString("D3"), new() { RunAt = due }));
        var selected = Signal(); var release = Signal(); var reads = 0; SortedSetEntry[] firstPage = [];
        async Task<SortedSetEntry[]> Pause(Task<SortedSetEntry[]> response)
        {
            var result = await response;
            if (Interlocked.Increment(ref reads) == 1) { firstPage = result; selected.SetResult(); await release.Task; }
            return result;
        }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            var result = proxy.Call(method, args);
            return method.Name == nameof(IDatabase.SortedSetRangeByScoreWithScoresAsync) ? Pause((Task<SortedSetEntry[]>)result!) : result;
        });
        var calls = new System.Collections.Concurrent.ConcurrentBag<string>(); var execution = new JobRegistry();
        execution.Register(definition, (_, context) => { calls.Add(context.JobId); return Task.CompletedTask; });
        await using var server = new JobServer(execution, new RedisJobStore(WithDatabase(database), prefix), new Clock(due), workerCount: 2);
        var start = server.StartAsync();
        try
        {
            await selected.Task.WaitAsync(Timeout); Assert.Equal(128, firstPage.Length);
            foreach (var entry in firstPage) Assert.Equal(JobOperationResult.Applied, await Manager(due).DeleteAsync((string)entry.Element!));
            release.SetResult(); await start.WaitAsync(Timeout);
            await Until(() => Task.FromResult(calls.Count == 140));
            Assert.True(server.IsAvailable); Assert.Null(server.LastError);
            Assert.All(calls, id => Assert.StartsWith("z", id)); Assert.Equal(140, calls.Distinct().Count());
            await server.StopAsync(); Assert.Equal(1, await db.SortedSetLengthAsync(prefix + ":scheduled"));
            Assert.InRange(reads, 3, 50);
        }
        finally { release.TrySetResult(); }
    }
}

public sealed partial class ProcessingTests
{
    [Fact]
    public async Task Listing_deduplicates_scan_and_keeps_each_projected_item_atomic_during_mutation()
    {
        await store.CreateAsync(Request("a"));
        var captured = Signal(); var release = Signal();
        async IAsyncEnumerable<RedisValue> DuplicateScan() { yield return "a"; await Task.Yield(); yield return "a"; }
        async Task<RedisValue[]> Pause(Task<RedisValue[]> response)
        { var result = await response; captured.SetResult(); await release.Task; return result; }
        var reads = 0;
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            if (method.Name == nameof(IDatabase.SetScanAsync)) return DuplicateScan();
            var result = proxy.Call(method, args);
            if (method.Name == nameof(IDatabase.HashGetAsync) && args![1] is RedisValue[])
            { Interlocked.Increment(ref reads); return Pause((Task<RedisValue[]>)result!); }
            return result;
        });
        var pending = new RedisJobManager(WithDatabase(database), prefix).ListAsync();
        try
        {
            await captured.Task.WaitAsync(Timeout); await Manager().DeleteAsync("a"); release.SetResult();
            var item = Assert.Single(await pending.WaitAsync(Timeout));
            Assert.Equal(JobState.Enqueued, item.State); Assert.Equal(0, item.Attempts); Assert.Equal(1, reads);
            Assert.Equal(JobState.Deleted, (await Detail()).State);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Uncertain_manual_confirmation_is_propagated_without_repeating_the_write(bool requeue)
    {
        await InState(requeue ? JobState.Deleted : JobState.Enqueued);
        var writes = 0; var error = new IOException("confirmation lost");
        async Task<RedisResult> Fail(Task<RedisResult> response) { await response; throw error; }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            var result = proxy.Call(method, args);
            if (method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args![0] is string script && script.Contains("Invalid management arguments"))
            { Interlocked.Increment(ref writes); return Fail((Task<RedisResult>)result!); }
            return result;
        });
        var manager = new RedisJobManager(WithDatabase(database), prefix, new Clock(now.AddSeconds(1)));
        var before = await Detail();
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => requeue ? manager.RequeueAsync("a") : manager.DeleteAsync("a")));
        Assert.Equal(1, writes); Assert.Equal(before.History.Count + 1, (await Detail()).History.Count);
    }

    [Theory]
    [InlineData("args")] [InlineData("history")] [InlineData("attempts")] [InlineData("cycle")]
    public async Task Management_snapshot_tampering_is_not_retried_as_a_race(string field)
    {
        await store.CreateAsync(Request("a")); var read = Signal(); var release = Signal(); var once = 0;
        async Task<RedisResult> Pause(Task<RedisResult> response)
        { var result = await response; read.SetResult(); await release.Task; return result; }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            var result = proxy.Call(method, args);
            return method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args![0] is string script && script.Contains("Operação atômica") && Interlocked.Increment(ref once) == 1
                ? Pause((Task<RedisResult>)result!) : result;
        });
        var pending = new RedisJobManager(WithDatabase(database), prefix).DeleteAsync("a");
        try
        {
            await read.Task.WaitAsync(Timeout);
            if (field == "history") await db.ListSetByIndexAsync(prefix + ":job:a:history", 0, "{}");
            else await db.HashSetAsync(prefix + ":job:a", field, field == "args" ? "{}" : "2");
            var before = await Dump(); release.SetResult();
            await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(Timeout)); Assert.Equal(before, await Dump());
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Scheduled_score_corruption_is_rejected_even_for_a_conflicting_manual_operation(bool requeue)
    {
        await Scheduled(); await db.SortedSetAddAsync(prefix + ":scheduled", "a", 1);
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => requeue ? Manager().RequeueAsync("a") : Manager().DeleteAsync("a"));
        Assert.Equal(before, await Dump());
    }

    [Fact]
    public async Task Retry_can_be_deleted_and_requeued_in_successive_manual_cycles_with_old_attempts_preserved()
    {
        await store.CreateAsync(Request("a", new() { RetryDelayMs = 0 }));
        var manager = Manager(now.AddSeconds(1));
        for (var i = 0; i < 3; i++)
        {
            var claim = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddSeconds(1), "cycle-" + i))!;
            Assert.Equal(1, claim.Attempt); await store.CompleteAsync(new(claim, now.AddSeconds(1), "failure-" + i));
            Assert.Single(await store.ReadScheduledAsync(now.AddSeconds(1), 0));
            Assert.Equal(JobOperationResult.Applied, await manager.DeleteAsync("a"));
            Assert.Equal(JobOperationResult.Applied, await manager.RequeueAsync("a"));
            var job = await Detail(); Assert.Equal(0, job.Attempts); Assert.Equal(i + 1, job.AttemptHistory.Count);
        }
        Assert.Equal(new[] { "failure-0", "failure-1", "failure-2" }, (await Detail()).AttemptHistory.Select(a => a.Error));
    }
}

public sealed partial class ProcessingTests
{
    [Theory]
    [InlineData("score")] [InlineData("date")] [InlineData("attempts")] [InlineData("token")]
    public async Task Old_promotion_rejects_corruption_in_snapshot_that_would_otherwise_prove_progress(string field)
    {
        var candidate = await Scheduled(); await store.TryPromoteAsync(candidate, now.AddSeconds(1));
        var claim = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddSeconds(1), "active"))!;
        if (field == "token") await db.HashSetAsync(prefix + ":job:a", "attemptToken", "");
        else
        {
            await store.CompleteAsync(new(claim, now.AddSeconds(2), "retry"));
            if (field == "score") await db.SortedSetAddAsync(prefix + ":scheduled", "a", 1);
            if (field == "date") await db.HashSetAsync(prefix + ":job:a", "scheduledAt", "invalid");
            if (field == "attempts") await db.HashSetAsync(prefix + ":job:a", "attempts", "2");
        }
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.TryPromoteAsync(candidate, now.AddSeconds(3)));
        Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Selection_rechecks_changed_score_and_distinguishes_new_cycle_from_corruption(bool legitimate)
    {
        await Scheduled(); var read = Signal(); var release = Signal();
        async Task<SortedSetEntry[]> Pause(Task<SortedSetEntry[]> response)
        { var result = await response; read.SetResult(); await release.Task; return result; }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            var result = proxy.Call(method, args);
            return method.Name == nameof(IDatabase.SortedSetRangeByScoreWithScoresAsync) ? Pause((Task<SortedSetEntry[]>)result!) : result;
        });
        var pending = new RedisJobStore(WithDatabase(database), prefix).ReadScheduledAsync(now.AddSeconds(10), 0);
        try
        {
            await read.Task.WaitAsync(Timeout);
            if (legitimate)
            {
                await Manager().DeleteAsync("a"); await Manager().RequeueAsync("a");
                var claim = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddSeconds(2), "new"))!;
                await store.CompleteAsync(new(claim, now.AddSeconds(3), "new retry"));
            }
            else await db.SortedSetAddAsync(prefix + ":scheduled", "a", 1);
            var before = await Dump(); release.SetResult();
            if (legitimate) Assert.Empty(await pending.WaitAsync(Timeout));
            else await Assert.ThrowsAsync<InvalidDataException>(() => pending.WaitAsync(Timeout));
            Assert.Equal(before, await Dump());
        }
        finally { release.TrySetResult(); }
    }
}

public sealed partial class ProcessingTests
{
    [Fact]
    public async Task Listing_sees_complete_creation_while_creation_acknowledgement_is_pending()
    {
        var persisted = Signal(); var release = Signal();
        async Task<RedisResult> Pause(Task<RedisResult> response)
        { var result = await response; persisted.TrySetResult(); await release.Task; return result; }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            var result = proxy.Call(method, args);
            return method.Name == nameof(IDatabase.ScriptEvaluateAsync) ? Pause((Task<RedisResult>)result!) : result;
        });
        var pending = new RedisJobStore(WithDatabase(database), prefix).CreateAsync(Request("a"));
        try
        {
            await persisted.Task.WaitAsync(Timeout); Assert.False(pending.IsCompleted);
            var item = Assert.Single(await Manager().ListAsync()); var detail = await Detail();
            Assert.Equal(detail.Id, item.Id); Assert.Equal(detail.Name, item.Name); Assert.Equal(detail.State, item.State); Assert.Equal(detail.Attempts, item.Attempts);
            Assert.Single(detail.History); Assert.Empty(detail.AttemptHistory);
            release.SetResult(); await pending.WaitAsync(Timeout);
        }
        finally { release.TrySetResult(); }
    }
}
