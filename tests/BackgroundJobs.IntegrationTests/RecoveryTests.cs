using System.Globalization;
using System.Text.Json;
using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs.IntegrationTests;

public sealed partial class ProcessingTests
{
    private async Task<RecoveryCandidate> Residue(string id = "a") =>
        Assert.Single(await store.ReadRecoveryAsync(), c => c.Job.Id == id);

    [Theory]
    [InlineData(1)] [InlineData(3)]
    public async Task Recovery_closes_only_open_attempt_preserves_creation_and_budget_and_invalidates_completion(int max)
    {
        var claim = await Claim(options: new JobOptions { MaxAttempts = max, RetryDelayMs = 60000 });
        var before = await Detail(); var candidate = await Residue();
        var identity = await db.HashGetAsync(prefix + ":job:a", "creationIdentity");
        var at = now.AddTicks(23456);
        Assert.Equal(RecoveryResult.Recovered, await store.TryRecoverAsync(candidate, at));
        var job = await Detail();
        Assert.Equal(max == 1 ? JobState.Failed : JobState.Enqueued, job.State);
        Assert.Equal(before.Id, job.Id); Assert.Equal(before.Name, job.Name);
        Assert.Equal(before.Args.GetRawText(), job.Args.GetRawText());
        Assert.Equal(before.CreatedAt, job.CreatedAt); Assert.Equal(before.MaxAttempts, job.MaxAttempts);
        Assert.Equal(before.RetryDelayMs, job.RetryDelayMs); Assert.Equal(1, job.Attempts);
        Assert.Equal(at, job.UpdatedAt); Assert.Null(job.ScheduledAt);
        Assert.Equal(before.History, job.History.Take(before.History.Count));
        var attempt = Assert.Single(job.AttemptHistory);
        Assert.Equal(1, attempt.Attempt); Assert.Equal(claim.StartedAt, attempt.StartedAt);
        Assert.Equal(at, attempt.FinishedAt); Assert.Equal(JobAttemptOutcome.Interrupted, attempt.Outcome);
        Assert.Contains("interrupted", attempt.Error);
        Assert.Equal(identity, await db.HashGetAsync(prefix + ":job:a", "creationIdentity"));
        Assert.Equal("0", await db.HashGetAsync(prefix + ":job:a", "cycle"));
        foreach (var field in new[] { "attemptToken", "attemptIndex", "completionIdentity" })
            Assert.False(await db.HashExistsAsync(prefix + ":job:a", field));
        Assert.False(await store.HasProcessingAsync());
        Assert.Null(await db.SortedSetScoreAsync(prefix + ":scheduled", "a"));
        Assert.Equal(max != 1, await db.SortedSetScoreAsync(prefix + ":ready", "a") is not null);
        var dump = await Dump();
        Assert.Empty(await store.ReadRecoveryAsync());
        Assert.Equal(RecoveryResult.AlreadyAdvanced, await store.TryRecoverAsync(candidate, at.AddSeconds(1)));
        await Assert.ThrowsAnyAsync<Exception>(() => store.CompleteAsync(new(claim, at.AddSeconds(1), null)));
        Assert.Equal(dump, await Dump());
        if (max == 1) Assert.Equal(JobOperationResult.Applied, await store.RequeueAsync("a", new Clock(at.AddSeconds(2))));
        var next = (await store.TryClaimAsync((await store.PeekAsync())!, at.AddSeconds(3), "new"))!;
        Assert.Equal(max == 1 ? 1 : 2, next.Attempt);
        dump = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.CompleteAsync(new(claim, at.AddSeconds(4), null)));
        Assert.Equal(dump, await Dump());
        await store.CompleteAsync(new(next, at.AddSeconds(5), null));
        Assert.Equal(JobState.Succeeded, (await Detail()).State);
        foreach (var key in connection.GetServer(connection.GetEndPoints()[0]).Keys(db.Database, prefix + ":*"))
            Assert.Null(await db.KeyTimeToLiveAsync(key));
    }

    [Fact]
    public async Task Recovered_jobs_join_fifo_tail_and_stale_candidate_cannot_modify_a_manual_cycle()
    {
        await Claim(); var old = await Residue();
        await store.CreateAsync(Request("ready"));
        var position = await db.SortedSetScoreAsync(prefix + ":ready", "ready");
        await store.TryRecoverAsync(old, now.AddSeconds(1));
        Assert.Equal(position, await db.SortedSetScoreAsync(prefix + ":ready", "ready"));
        Assert.True(position < await db.SortedSetScoreAsync(prefix + ":ready", "a"));
        Assert.Equal("ready", (await store.PeekAsync())!.Job.Id);
        Assert.Equal(JobOperationResult.Applied, await store.DeleteAsync("a", new Clock(now.AddSeconds(2))));
        Assert.Equal(JobOperationResult.Applied, await store.RequeueAsync("a", new Clock(now.AddSeconds(3))));
        var before = await Dump();
        Assert.Equal(RecoveryResult.AlreadyAdvanced, await store.TryRecoverAsync(old, now.AddSeconds(4)));
        Assert.Equal(before, await Dump());
    }

    [Fact]
    public async Task Recovery_preserves_prior_cycles_failures_and_repeated_attempt_numbers()
    {
        var first = await Claim(options: new JobOptions { MaxAttempts = 2, RetryDelayMs = 0 });
        await store.CompleteAsync(new(first, now.AddSeconds(1), "first failure"));
        await store.TryPromoteAsync(Assert.Single(await store.ReadScheduledAsync(now.AddSeconds(1), 0)), now.AddSeconds(1));
        var second = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddSeconds(2), "second"))!;
        await store.CompleteAsync(new(second, now.AddSeconds(3), null));
        await store.RequeueAsync("a", new Clock(now.AddSeconds(4)));
        var third = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddSeconds(5), "third"))!;
        await store.CompleteAsync(new(third, now.AddSeconds(6), "another failure"));
        await store.TryPromoteAsync(Assert.Single(await store.ReadScheduledAsync(now.AddSeconds(6), 0)), now.AddSeconds(6));
        await store.TryClaimAsync((await store.PeekAsync())!, now.AddSeconds(7), "fourth");
        var before = await Detail(); var candidate = await Residue();
        await store.TryRecoverAsync(candidate, now.AddSeconds(8));
        var job = await Detail();
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(new[] { 1, 2, 1, 2 }, job.AttemptHistory.Select(a => a.Attempt));
        Assert.Equal(before.AttemptHistory.Take(3), job.AttemptHistory.Take(3));
        Assert.Equal(before.History, job.History.Take(before.History.Count));
        Assert.Equal("1", await db.HashGetAsync(prefix + ":job:a", "cycle"));
        Assert.Equal(JobOperationResult.Applied, await store.RequeueAsync("a", new Clock(now.AddSeconds(9))));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task Recovery_wrong_key_types_never_partially_write(int index)
    {
        await Claim(); var candidate = await Residue();
        RedisKey[] keys = [.. store.Keys("a"), prefix + ":processing"];
        await db.KeyDeleteAsync(keys[index]);
        if (index == 6) await db.SetAddAsync(keys[index], "wrong");
        else await db.StringSetAsync(keys[index], "wrong");
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.TryRecoverAsync(candidate, now.AddSeconds(1)));
        Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData("token")] [InlineData("index")] [InlineData("cycle")] [InlineData("counter")]
    [InlineData("closed")] [InlineData("missing-attempt")] [InlineData("missing-set")]
    [InlineData("ready")] [InlineData("scheduled")] [InlineData("missing-job-index")]
    [InlineData("orphan")] [InlineData("history")]
    public async Task Corrupt_residues_fail_startup_without_mutating_or_invoking(string corruption)
    {
        await Claim();
        var key = prefix + ":job:a";
        switch (corruption)
        {
            case "token": await db.HashSetAsync(key, "attemptToken", ""); break;
            case "index": await db.HashSetAsync(key, "attemptIndex", 1); break;
            case "cycle": await db.HashSetAsync(key, "cycle", "01"); break;
            case "counter": await db.HashSetAsync(key, "attempts", 2); break;
            case "closed": await db.ListSetByIndexAsync(key + ":attempts", 0, JsonSerializer.Serialize(new {
                attempt = 1, startedAt = JobCodec.DateText(now.AddTicks(1)), finishedAt = JobCodec.DateText(now.AddSeconds(1)), outcome = "interrupted", error = "test" })); break;
            case "missing-attempt": await db.KeyDeleteAsync(key + ":attempts"); break;
            case "missing-set": await db.SetRemoveAsync(prefix + ":processing", "a"); break;
            case "ready": await db.SortedSetAddAsync(prefix + ":ready", "a", 1); break;
            case "scheduled": await db.SortedSetAddAsync(prefix + ":scheduled", "a", 1); break;
            case "missing-job-index": await db.SetRemoveAsync(prefix + ":jobs", "a"); break;
            case "orphan": await db.SetAddAsync(prefix + ":processing", "orphan"); break;
            case "history": await db.ListSetByIndexAsync(key + ":history", 0, "{}"); break;
        }
        var before = await Dump(); var calls = 0; var execution = new JobRegistry();
        execution.Register(definition, (_, _) => { calls++; return Task.CompletedTask; });
        await using var server = new JobServer(execution, connection, prefix);
        await Assert.ThrowsAnyAsync<Exception>(() => server.StartAsync());
        Assert.False(server.IsAvailable); Assert.NotNull(server.LastError); Assert.Equal(0, calls);
        Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData("00")] [InlineData("-1")] [InlineData("bad")]
    [InlineData("9007199254740991")] [InlineData("9007199254740992")]
    public async Task Invalid_or_exhausted_recovery_fifo_never_writes(string sequence)
    {
        await Claim(); var candidate = await Residue();
        await db.StringSetAsync(prefix + ":sequence", sequence);
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.TryRecoverAsync(candidate, now.AddSeconds(1)));
        Assert.Equal(before, await Dump());
    }

    [Fact]
    public async Task Recovery_validates_tail_and_time_but_terminal_does_not_need_fifo_capacity_or_increment_cycle()
    {
        await Claim(); var candidate = await Residue(); var before = await Dump();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TryRecoverAsync(candidate, now));
        Assert.Equal(before, await Dump());
        await db.SortedSetAddAsync(prefix + ":ready", "other", 100);
        before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.TryRecoverAsync(candidate, now.AddSeconds(1)));
        Assert.Equal(before, await Dump());
        await db.SortedSetRemoveAsync(prefix + ":ready", "other");
        await db.HashSetAsync(prefix + ":job:a", "cycle", long.MaxValue.ToString(CultureInfo.InvariantCulture));
        candidate = await Residue();
        await store.TryRecoverAsync(candidate, now.AddTicks(2));
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), await db.HashGetAsync(prefix + ":job:a", "cycle"));
        // A distinct exhausted job never enters ready, even with a full sequence.
        var next = await store.TryClaimAsync((await store.PeekAsync())!, now.AddTicks(3), "next");
        await store.CompleteAsync(new(next!, now.AddTicks(4), null));
        await Claim("terminal", new JobOptions { MaxAttempts = 1 });
        var terminal = await Residue("terminal");
        await db.StringSetAsync(prefix + ":sequence", "9007199254740991");
        await store.TryRecoverAsync(terminal, now.AddSeconds(1));
        Assert.Equal(JobState.Failed, (await Detail("terminal")).State);
    }

    [Fact]
    public async Task Bounded_non_destructive_batches_recover_all_jobs_and_preserve_other_prefixes()
    {
        for (var i = 0; i < 270; i++) await Claim("job-" + i);
        var other = new RedisJobStore(connection, prefix + "-other");
        try
        {
            await other.CreateAsync(Request("foreign"));
            var original = await Dump(); var first = await store.ReadRecoveryAsync();
            Assert.Equal(128, first.Count); Assert.Equal(128, first.Select(c => c.Job.Id).Distinct().Count());
            Assert.Equal(original, await Dump());
            var count = 0;
            while (true)
            {
                var batch = await store.ReadRecoveryAsync();
                Assert.InRange(batch.Count, 0, 128);
                if (batch.Count == 0) break;
                foreach (var item in batch) { await store.TryRecoverAsync(item, now.AddSeconds(1)); count++; }
            }
            Assert.Equal(270, count); Assert.False(await store.HasProcessingAsync());
            Assert.Equal(JobState.Enqueued, (await other.GetAsync("foreign"))!.State);
            var before = await Dump();
            foreach (var item in first) Assert.Equal(RecoveryResult.AlreadyAdvanced, await store.TryRecoverAsync(item, now.AddSeconds(2)));
            Assert.Equal(before, await Dump());
        }
        finally
        {
            var keys = connection.GetServer(connection.GetEndPoints()[0]).Keys(db.Database, prefix + "-other:*").ToArray();
            if (keys.Length > 0) await db.KeyDeleteAsync(keys);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Uncertain_recovery_is_fatal_and_new_server_respects_actual_persisted_result(bool applied)
    {
        await Claim(options: new JobOptions { MaxAttempts = 1 });
        var failure = new IOException("lost recovery acknowledgement"); var attempts = 0;
        async Task<RedisResult> Lose(Task<RedisResult>? response)
        { if (response is not null) await response; throw failure; }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            if (method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args![0] is string script && script.Contains("Invalid recovery arguments"))
            { Interlocked.Increment(ref attempts); return Lose(applied ? (Task<RedisResult>)proxy.Call(method, args)! : null); }
            return proxy.Call(method, args);
        });
        await using (var server = new JobServer(registry, new RedisJobStore(WithDatabase(database), prefix)))
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => server.StartAsync()));
            Assert.Same(failure, server.LastError); Assert.False(server.IsAvailable); Assert.Equal(1, attempts);
        }
        Assert.Equal(applied ? JobState.Failed : JobState.Processing, (await Detail()).State);
        await using var restart = new JobServer(registry, connection, prefix);
        await restart.StartAsync(); await restart.StopAsync();
        var job = await Detail();
        Assert.Equal(JobState.Failed, job.State); Assert.Equal(3, job.History.Count);
        Assert.Equal(JobAttemptOutcome.Interrupted, Assert.Single(job.AttemptHistory).Outcome);
    }

    [Fact]
    public async Task Missing_finalized_handler_prevents_any_recovery_write()
    {
        var finished = await Claim("finished"); await store.CompleteAsync(new(finished, now.AddSeconds(1), null));
        await Claim();
        var onlyPending = new JobRegistry();
        // The name differs only on a finalized record; no residue may be changed.
        await db.HashSetAsync(prefix + ":job:finished", "name", "removed-contract");
        onlyPending.Register(definition, (_, _) => Task.CompletedTask);
        var before = await Dump();
        await using var server = new JobServer(onlyPending, connection, prefix);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
        Assert.Contains("removed-contract", failure.Message); Assert.Contains("finished", failure.Message);
        Assert.Equal(before, await Dump());
    }
    [Fact]
    public async Task Selection_deduplicates_scan_members_without_writing()
    {
        await Claim("a"); await Claim("b");
        async IAsyncEnumerable<RedisValue> RepeatedMembers()
        {
            await Task.Yield();
            yield return "a"; yield return "a"; yield return "b"; yield return "b";
        }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
            method.Name == nameof(IDatabase.SetScanAsync) ? RepeatedMembers() : proxy.Call(method, args));
        var selected = new RedisJobStore(WithDatabase(database), prefix);
        var before = await Dump(); var candidates = await selected.ReadRecoveryAsync();
        Assert.Equal(new[] { "a", "b" }, candidates.Select(c => c.Job.Id));
        Assert.Equal(before, await Dump());
    }

    [Fact]
    public async Task Stop_after_first_confirmed_recovery_preserves_partial_progress_for_new_instance()
    {
        await Claim("a", new JobOptions { MaxAttempts = 1 });
        await Claim("b", new JobOptions { MaxAttempts = 1 });
        var entered = Signal(); var release = Signal(); var calls = 0;
        async Task<RedisResult> Pause(Task<RedisResult> response)
        { var result = await response; entered.TrySetResult(); await release.Task; return result; }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            var response = proxy.Call(method, args);
            if (method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args![0] is string script &&
                script.Contains("Invalid recovery arguments") && Interlocked.Increment(ref calls) == 1)
                return Pause((Task<RedisResult>)response!);
            return response;
        });
        await using (var server = new JobServer(registry, new RedisJobStore(WithDatabase(database), prefix)))
        {
            try
            {
                var start = server.StartAsync(); await entered.Task.WaitAsync(Timeout);
                Assert.False(server.IsAvailable);
                var partial = new[] { await Detail("a"), await Detail("b") };
                var confirmed = Assert.Single(partial, j => j.State == JobState.Failed);
                Assert.Single(partial, j => j.State == JobState.Processing);
                var stop = server.StopAsync(); Assert.False(stop.IsCompleted);
                release.SetResult(); await Task.WhenAll(start, stop).WaitAsync(Timeout);
                Assert.Equal(1, calls); Assert.Null(server.LastError);
                await using var restart = new JobServer(registry, connection, prefix);
                await restart.StartAsync(); await restart.StopAsync();
                Assert.Equal(JsonSerializer.Serialize(confirmed), JsonSerializer.Serialize(await Detail(confirmed.Id)));
                foreach (var id in new[] { "a", "b" })
                {
                    var job = await Detail(id); Assert.Equal(JobState.Failed, job.State);
                    Assert.Equal(3, job.History.Count); Assert.Equal(JobAttemptOutcome.Interrupted, Assert.Single(job.AttemptHistory).Outcome);
                }
            }
            finally { release.TrySetResult(); }
        }
    }

    [Fact]
    public async Task Corruption_after_first_transition_does_not_roll_back_prior_recovery_or_partially_write_next()
    {
        await Claim("a", new JobOptions { MaxAttempts = 1 });
        await Claim("b", new JobOptions { MaxAttempts = 1 });
        string? corrupted = null; string[]? afterCorruption = null;
        async Task<RedisResult> CorruptNext(Task<RedisResult> response)
        {
            var result = await response;
            var remaining = await db.SetMembersAsync(prefix + ":processing");
            if (remaining.Length == 1)
            {
                corrupted = remaining[0].ToString();
                await db.HashSetAsync(prefix + ":job:" + corrupted, "attemptToken", "changed-without-progress");
                afterCorruption = await Dump();
            }
            return result;
        }
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
        {
            var result = proxy.Call(method, args);
            return method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args![0] is string script && script.Contains("Invalid recovery arguments")
                ? CorruptNext((Task<RedisResult>)result!) : result;
        });
        await using var server = new JobServer(registry, new RedisJobStore(WithDatabase(database), prefix));
        await Assert.ThrowsAnyAsync<Exception>(() => server.StartAsync());
        Assert.NotNull(afterCorruption); Assert.Equal(afterCorruption, await Dump());
        Assert.False(server.IsAvailable); Assert.NotNull(server.LastError);
        Assert.Equal(JobState.Processing, (await Detail(corrupted!)).State);
        Assert.Equal(JobState.Failed, (await Detail(corrupted == "a" ? "b" : "a")).State);
    }

    [Theory]
    [InlineData("immutable")] [InlineData("attempt-prefix")] [InlineData("outcome")]
    public async Task Stale_recovery_rejects_arbitrary_changes_instead_of_claiming_progress(string corruption)
    {
        await Claim(); var old = await Residue();
        await store.TryRecoverAsync(old, now.AddSeconds(1));
        var key = prefix + ":job:a";
        if (corruption == "immutable") await db.HashSetAsync(key, "args", "{\"private\":\"secret\"}");
        else
        {
            var attempt = (await Detail()).AttemptHistory[0];
            await db.ListSetByIndexAsync(key + ":attempts", 0, JsonSerializer.Serialize(new {
                attempt = 1, startedAt = JobCodec.DateText(corruption == "attempt-prefix" ? now : attempt.StartedAt),
                finishedAt = JobCodec.DateText(attempt.FinishedAt!.Value), outcome = corruption == "outcome" ? "failed" : "interrupted", error = "diagnostic" }));
        }
        var before = await Dump();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => store.TryRecoverAsync(old, now.AddSeconds(2)));
        Assert.DoesNotContain("secret", failure.Message); Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData("terminal-before-limit")] [InlineData("failed-to-ready")]
    [InlineData("interrupted-to-scheduled")] [InlineData("missing-diagnostic")]
    public async Task Invalid_interruption_snapshots_are_not_accepted_by_management_or_workers(string corruption)
    {
        await Claim(); var candidate = await Residue();
        await store.TryRecoverAsync(candidate, now.AddSeconds(1));
        var key = prefix + ":job:a";
        var job = await Detail();
        if (corruption == "terminal-before-limit" || corruption == "interrupted-to-scheduled")
        {
            var state = corruption == "terminal-before-limit" ? "failed" : "scheduled";
            await db.SortedSetRemoveAsync(prefix + ":ready", "a");
            await db.HashSetAsync(key, "state", state);
            if (state == "scheduled")
            {
                await db.HashSetAsync(key, "scheduledAt", JobCodec.DateText(now.AddSeconds(2)));
                await db.SortedSetAddAsync(prefix + ":scheduled", "a", JobCodec.ScheduledScore(now.AddSeconds(2)));
            }
            await db.ListSetByIndexAsync(key + ":history", 2, JsonSerializer.Serialize(new { state, at = JobCodec.DateText(job.UpdatedAt), reason = "invalid recovery" }));
        }
        else
        {
            var attempt = job.AttemptHistory[0];
            await db.ListSetByIndexAsync(key + ":attempts", 0, JsonSerializer.Serialize(new {
                attempt = 1, startedAt = JobCodec.DateText(attempt.StartedAt), finishedAt = JobCodec.DateText(attempt.FinishedAt!.Value),
                outcome = corruption == "failed-to-ready" ? "failed" : "interrupted", error = corruption == "missing-diagnostic" ? null : "failure" }));
        }
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.RequeueAsync("a", new Clock(now.AddSeconds(3))));
        await Assert.ThrowsAnyAsync<Exception>(() => store.DeleteAsync("a", new Clock(now.AddSeconds(3))));
        await using var server = new JobServer(registry, connection, prefix);
        await Assert.ThrowsAnyAsync<Exception>(() => server.StartAsync());
        Assert.Equal(before, await Dump());
    }

}
