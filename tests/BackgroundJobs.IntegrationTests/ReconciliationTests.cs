using System.Reflection;
using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs.IntegrationTests;

public sealed partial class ProcessingTests
{
    private RedisJobStore Uncertain(string marker, string window, List<Func<Task>> delayed, Func<Task>? progress = null)
    {
        var once = 0;
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            if (method.Name != nameof(IDatabase.ScriptEvaluateAsync) || args![0] is not string script ||
                !script.Contains(marker) || Interlocked.Increment(ref once) != 1) return proxy.Call(method, args);
            Task<RedisResult> Send() => (Task<RedisResult>)proxy.Call(method, args)!;
            async Task<RedisResult> Lose()
            {
                if (window == "after") { await Send(); if (progress is not null) await progress(); }
                if (window == "late") delayed.Add(async () => { await Send(); });
                throw new RedisConnectionException(ConnectionFailureType.SocketClosed, "injected uncertain transport");
            }
            return Lose();
        });
        return new RedisJobStore(WithDatabase(database), prefix);
    }

    [Theory]
    [InlineData("before")] [InlineData("after")] [InlineData("late")]
    public async Task Uncertain_creation_confirms_one_immutable_request(string window)
    {
        var delayed = new List<Func<Task>>();
        var unreliable = Uncertain("Invalid creation parameters", window, delayed);
        await unreliable.CreateAsync(Request("a"));
        var before = await Dump();
        foreach (var send in delayed) await send();
        Assert.Equal(before, await Dump());
        Assert.Single((await Detail()).History);
        Assert.Equal(1, (long)await db.StringGetAsync(prefix + ":sequence"));
    }

    [Theory]
    [InlineData("before")] [InlineData("after")] [InlineData("late")]
    public async Task Uncertain_claim_and_completion_preserve_one_attempt_and_original_retry_time(string window)
    {
        await store.CreateAsync(Request("a", new() { RetryDelayMs = 500 }));
        var candidate = (await store.PeekAsync())!;
        var delayed = new List<Func<Task>>();
        var unreliable = Uncertain("Invalid claim arguments", window, delayed);
        var claim = Assert.IsType<ClaimedJob>(await unreliable.TryClaimAsync(candidate, now.AddTicks(1), "owner"));
        foreach (var send in delayed) await send();
        Assert.Single((await Detail()).AttemptHistory);
        delayed.Clear();
        unreliable = Uncertain("Invalid completion arguments", window, delayed);
        var finish = now.AddSeconds(1);
        await unreliable.CompleteAsync(new(claim, finish, "demo failure"));
        var before = await Dump();
        foreach (var send in delayed) await send();
        Assert.Equal(before, await Dump());
        var job = await Detail();
        Assert.Equal(finish.AddMilliseconds(500), job.ScheduledAt);
        Assert.Equal(finish, Assert.Single(job.AttemptHistory).FinishedAt);
        Assert.Equal(3, job.History.Count);
    }

    [Theory]
    [InlineData("before", false)] [InlineData("after", false)] [InlineData("late", false)]
    [InlineData("before", true)] [InlineData("after", true)] [InlineData("late", true)]
    public async Task Uncertain_promotion_or_recovery_has_one_transition_and_fifo_position(string window, bool recovery)
    {
        if (recovery) await Claim();
        else await store.CreateAsync(Request("a", new() { RunAt = now.AddSeconds(1) }));
        var delayed = new List<Func<Task>>();
        var unreliable = Uncertain(recovery ? "Invalid recovery arguments" : "Invalid promotion arguments", window, delayed);
        if (recovery) await unreliable.TryRecoverAsync(Assert.Single(await store.ReadRecoveryAsync()), now.AddSeconds(2));
        else await unreliable.TryPromoteAsync(Assert.Single(await store.ReadScheduledAsync(now.AddSeconds(2), 0)), now.AddSeconds(2));
        var before = await Dump();
        foreach (var send in delayed) await send();
        Assert.Equal(before, await Dump());
        var job = await Detail();
        Assert.Equal(JobState.Enqueued, job.State);
        Assert.Equal(recovery ? 3 : 2, job.History.Count);
        Assert.Equal(recovery ? 2 : 1, (long)await db.StringGetAsync(prefix + ":sequence"));
        if (recovery) Assert.Equal(JobAttemptOutcome.Interrupted, Assert.Single(job.AttemptHistory).Outcome);
    }

    [Theory]
    [InlineData("before", false)] [InlineData("after", false)] [InlineData("late", false)]
    [InlineData("before", true)] [InlineData("after", true)] [InlineData("late", true)]
    public async Task Uncertain_manual_operation_has_one_decision_even_after_another_cycle(string window, bool requeue)
    {
        await InState(requeue ? JobState.Deleted : JobState.Enqueued);
        var delayed = new List<Func<Task>>();
        async Task Advance()
        {
            if (requeue) await store.DeleteAsync("a", new Clock(now.AddSeconds(2)));
            await store.RequeueAsync("a", new Clock(now.AddSeconds(3)));
        }
        var unreliable = Uncertain("Invalid management arguments", window, delayed, Advance);
        Assert.Equal(JobOperationResult.Applied, requeue
            ? await unreliable.RequeueAsync("a", new Clock(now.AddSeconds(1)))
            : await unreliable.DeleteAsync("a", new Clock(now.AddSeconds(1))));
        if (window != "after") await Advance();
        var before = await Dump();
        foreach (var send in delayed) await send();
        Assert.Equal(before, await Dump());
        Assert.Equal(JobState.Enqueued, (await Detail()).State);
        Assert.Equal(requeue ? "2" : "1", (string?)await db.HashGetAsync(prefix + ":job:a", "cycle"));
    }

    [Fact]
    public async Task Lost_completion_response_is_confirmed_after_later_attempt_and_manual_cycle()
    {
        var claim = await Claim(options: new() { RetryDelayMs = 0 });
        var delayed = new List<Func<Task>>();
        async Task Advance()
        {
            await store.TryPromoteAsync(Assert.Single(await store.ReadScheduledAsync(now.AddSeconds(2), 0)), now.AddSeconds(2));
            var next = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddSeconds(3), "next"))!;
            await store.CompleteAsync(new(next, now.AddSeconds(4), null));
            await store.RequeueAsync("a", new Clock(now.AddSeconds(5)));
        }
        var unreliable = Uncertain("Invalid completion arguments", "after", delayed, Advance);
        await unreliable.CompleteAsync(new(claim, now.AddSeconds(1), "first result"));
        var job = await Detail();
        Assert.Equal(JobState.Enqueued, job.State);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(2, job.AttemptHistory.Count);
        Assert.Equal("first result", job.AttemptHistory[0].Error);
    }

    [Fact]
    public async Task Successor_executor_rejects_delayed_predecessor_reservation()
    {
        await store.CreateAsync(Request("a"));
        var delayed = new List<Func<Task>>();
        var predecessor = Uncertain("Invalid claim arguments", "late", delayed);
        await predecessor.BeginSessionAsync();
        var candidate = (await predecessor.PeekAsync())!;
        // Reconciliation succeeds, but the originally timed-out command remains in transit.
        await predecessor.TryClaimAsync(candidate, now.AddTicks(1), "old");
        var successor = new RedisJobStore(connection, prefix);
        await successor.BeginSessionAsync();
        await successor.TryRecoverAsync(Assert.Single(await successor.ReadRecoveryAsync()), now.AddSeconds(1));
        var before = await Dump();
        await Assert.ThrowsAsync<RedisServerException>(async () => { await Assert.Single(delayed)(); });
        Assert.Equal(before, await Dump());
    }
}

public sealed partial class ProcessingTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Manual_lost_absence_or_conflict_decision_remains_stable_after_state_changes(bool missing)
    {
        if (!missing) await InState(JobState.Processing);
        var delayed = new List<Func<Task>>();
        async Task Advance()
        {
            if (missing) await store.CreateAsync(Request("a"));
            else
            {
                await store.TryRecoverAsync(Assert.Single(await store.ReadRecoveryAsync()), now.AddSeconds(2));
                await store.RequeueAsync("a", new Clock(now.AddSeconds(3)));
            }
        }
        var unreliable = Uncertain("Invalid management arguments", "after", delayed, Advance);
        Assert.Equal(missing ? JobOperationResult.NotFound : JobOperationResult.Conflict,
            await unreliable.DeleteAsync("a", new Clock(now.AddSeconds(1))));
        Assert.Equal(JobState.Enqueued, (await Detail()).State);
    }

    [Fact]
    public async Task Failed_reconciliation_read_propagates_ambiguity_and_keeps_applied_operation()
    {
        await store.CreateAsync(Request("a")); var writes = 0;
        var failure = new RedisTimeoutException("reconciliation unavailable", CommandStatus.Unknown);
        async Task<RedisResult> Lost(Task<RedisResult> response) { await response; throw failure; }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            if (method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args![0] is string script)
            {
                if (script.Contains("Invalid management arguments")) { Interlocked.Increment(ref writes); return Lost((Task<RedisResult>)proxy.Call(method, args)!); }
                if (script.Contains("local receiptKey")) return Task.FromException<RedisResult>(failure);
            }
            return proxy.Call(method, args);
        });
        var unreliable = new RedisJobStore(WithDatabase(database), prefix);
        Assert.Same(failure, await Assert.ThrowsAsync<RedisTimeoutException>(() => unreliable.DeleteAsync("a", new Clock(now.AddSeconds(1)))));
        Assert.Equal(1, writes); Assert.Equal(JobState.Deleted, (await Detail()).State);
        Assert.Equal(2, (await Detail()).History.Count);
    }

    [Theory]
    [InlineData("args")] [InlineData("name")] [InlineData("history")] [InlineData("ready")]
    public async Task Creation_receipt_never_confirms_corrupt_immutable_data_or_indexes(string corruption)
    {
        var request = Request("a"); await store.CreateAsync(request);
        if (corruption == "args") await db.HashSetAsync(prefix + ":job:a", "args", "{\"message\":\"tampered\"}");
        else if (corruption == "name") await db.HashSetAsync(prefix + ":job:a", "name", "another-handler");
        else if (corruption == "history") await db.ListSetByIndexAsync(prefix + ":job:a:history", 0, "{}");
        else await db.SortedSetRemoveAsync(prefix + ":ready", "a");
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAsync(request));
        Assert.Equal(before, await Dump());
    }
}

public sealed partial class ProcessingTests
{
    [Theory]
    [InlineData("token")] [InlineData("receipt")] [InlineData("history")] [InlineData("index")]
    public async Task Lost_claim_acknowledgement_never_hides_corrupted_ownership_or_receipt(string corruption)
    {
        await store.CreateAsync(Request("a")); var candidate = (await store.PeekAsync())!;
        var delayed = new List<Func<Task>>();
        async Task Corrupt()
        {
            if (corruption == "token") await db.HashSetAsync(prefix + ":job:a", "attemptToken", "another-owner");
            else if (corruption == "history") await db.ListSetByIndexAsync(prefix + ":job:a:attempts", 0, "{}");
            else if (corruption == "index") await db.SetRemoveAsync(prefix + ":processing", "a");
            else
            {
                var key = Assert.Single(connection.GetServer(connection.GetEndPoints()[0]).Keys(db.Database, prefix + ":job:a:operation:*"));
                await db.StringSetAsync(key, "{}");
            }
        }
        var unreliable = Uncertain("Invalid claim arguments", "after", delayed, Corrupt);
        await Assert.ThrowsAnyAsync<Exception>(() => unreliable.TryClaimAsync(candidate, now.AddTicks(1), "original-owner"));
        Assert.Equal("processing", (string?)await db.HashGetAsync(prefix + ":job:a", "state"));
    }
}

public sealed partial class ProcessingTests
{
    [Theory]
    [InlineData("completed")] [InlineData("deleted")] [InlineData("requeued")]
    public async Task Lost_creation_response_confirms_original_after_concurrent_progress(string progress)
    {
        var delayed = new List<Func<Task>>();
        async Task Advance()
        {
            if (progress == "completed")
            {
                var claim = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddSeconds(1), "worker"))!;
                await store.CompleteAsync(new(claim, now.AddSeconds(2), null));
            }
            else
            {
                await store.DeleteAsync("a", new Clock(now.AddSeconds(1)));
                if (progress == "requeued") await store.RequeueAsync("a", new Clock(now.AddSeconds(2)));
            }
        }
        await Uncertain("Invalid creation parameters", "after", delayed, Advance).CreateAsync(Request("a"));
        var job = await Detail();
        Assert.Equal(progress == "completed" ? JobState.Succeeded : progress == "deleted" ? JobState.Deleted : JobState.Enqueued, job.State);
        Assert.Equal(1, job.History.Count(h => h.Reason == job.History[0].Reason));
        Assert.Equal(Request("a").Prepared.Descriptor.Args.GetRawText(), job.Args.GetRawText());
    }

    [Fact]
    public async Task Lost_requeue_response_survives_fast_execution_and_another_legitimate_requeue()
    {
        await InState(JobState.Succeeded);
        var delayed = new List<Func<Task>>();
        async Task Advance()
        {
            var claimed = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddSeconds(2), "fast-worker"))!;
            await store.CompleteAsync(new(claimed, now.AddSeconds(3), null));
            await store.RequeueAsync("a", new Clock(now.AddSeconds(4)));
        }
        var result = await Uncertain("Invalid management arguments", "after", delayed, Advance)
            .RequeueAsync("a", new Clock(now.AddSeconds(1)));
        Assert.Equal(JobOperationResult.Applied, result);
        var job = await Detail(); Assert.Equal(0, job.Attempts); Assert.Equal(JobState.Enqueued, job.State);
        Assert.Equal(2, job.History.Count(h => h.Reason == "manual requeue"));
        Assert.Equal(2, job.AttemptHistory.Count);
        Assert.Equal("2", (string?)await db.HashGetAsync(prefix + ":job:a", "cycle"));
    }
}

public sealed partial class ProcessingTests
{
    [Fact]
    public async Task Executor_generation_reconciliation_retains_same_envelope_across_failed_rounds()
    {
        var envelopes = new List<string>(); var attempts = 0;
        async Task<RedisResult> Lose(Task<RedisResult> response) { await response; throw new RedisTimeoutException("lost generation acknowledgement", CommandStatus.Unknown); }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            if (method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args![0] is string script && script.Contains("Executor generation changed"))
            {
                envelopes.Add(string.Join("|", ((RedisValue[])args[2]!).Select(v => v.ToString())));
                var sent = (Task<RedisResult>)proxy.Call(method, args)!;
                if (++attempts <= 2) return Lose(sent);
                return sent;
            }
            return proxy.Call(method, args);
        });
        var executor = new RedisJobStore(WithDatabase(database), prefix);
        await Assert.ThrowsAsync<RedisTimeoutException>(executor.BeginSessionAsync);
        var generation = await db.StringGetAsync(prefix + ":executor");
        await executor.BeginSessionAsync();
        Assert.Equal(generation, await db.StringGetAsync(prefix + ":executor"));
        Assert.Single(envelopes.Distinct()); Assert.Equal(3, attempts);
    }
}

public sealed partial class ProcessingTests
{
    [Fact]
    public async Task Corrupted_completion_receipt_decision_cannot_announce_success()
    {
        var completion = new AttemptCompletion(await Claim(), now.AddSeconds(1), null);
        await store.CompleteAsync(completion);
        var key = Assert.Single(connection.GetServer(connection.GetEndPoints()[0]).Keys(db.Database, prefix + ":job:a:operation:complete-*"));
        var receipt = System.Text.Json.Nodes.JsonNode.Parse((string)(await db.StringGetAsync(key))!)!;
        receipt["result"] = 2;
        await db.StringSetAsync(key, receipt.ToJsonString());
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.CompleteAsync(completion));
        Assert.Equal(before, await Dump());
    }
}
