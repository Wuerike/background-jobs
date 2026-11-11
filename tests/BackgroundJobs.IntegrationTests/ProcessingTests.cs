using System.Globalization;
using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs.IntegrationTests;

public sealed partial class ProcessingTests : IAsyncLifetime
{
    private readonly string prefix = "processing-" + Guid.NewGuid().ToString("N");
    private ConnectionMultiplexer connection = null!;
    private RedisJobStore store = null!;
    private IDatabase db = null!;
    private readonly JobRegistry registry = new();
    private readonly JobDefinition<Args> definition = new("job");
    private readonly DateTimeOffset now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    public sealed record Args(string Message);
    public async Task InitializeAsync()
    {
        connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "127.0.0.1:6379");
        db = connection.GetDatabase(); store = new RedisJobStore(connection, prefix);
        registry.Register(definition, (_, _) => Task.CompletedTask);
    }
    public async Task DisposeAsync()
    {
        if (connection is null) return;
        try
        {
            var keys = connection.GetServer(connection.GetEndPoints()[0]).Keys(db.Database, prefix + ":*").ToArray();
            if (keys.Length > 0) await db.KeyDeleteAsync(keys);
        }
        finally { await connection.DisposeAsync(); }
    }
    private NewJob Request(string id, JobOptions? options = null) => new(id,
        new JobPreparation(registry.Contracts, new Clock(now)).Prepare(definition, new Args("Olá 🦊"), options));
    private async Task<ClaimedJob> Claim(string id = "a", JobOptions? options = null)
    {
        await store.CreateAsync(Request(id, options));
        var candidate = Assert.IsType<JobCandidate>(await store.PeekAsync());
        return Assert.IsType<ClaimedJob>(await store.TryClaimAsync(candidate, now.AddTicks(1), "token-" + id));
    }
    private async Task<JobDetails> Detail(string id = "a") => Assert.IsType<JobDetails>(await store.GetAsync(id));
    private async Task<string[]> Dump()
    {
        var keys = connection.GetServer(connection.GetEndPoints()[0]).Keys(db.Database, prefix + ":*").OrderBy(k => k.ToString()).ToArray();
        var values = new List<string>();
        // Redis can rehash a set during a read-only scan, changing DUMP ordering.
        // Compare its logical members canonically; other key dumps retain exact bytes.
        foreach (var key in keys) values.Add(key.ToString() + ":" +
            (await db.KeyTypeAsync(key) == RedisType.Set
                ? System.Text.Json.JsonSerializer.Serialize((await db.SetMembersAsync(key)).Select(v => v.ToString()).Order(StringComparer.Ordinal))
                : Convert.ToBase64String((await db.KeyDumpAsync(key))!)));
        return values.ToArray();
    }

    [Fact]
    public async Task Claim_without_execution_metadata_is_exclusive_atomic_and_preserves_creation()
    {
        var request = Request("a"); await store.CreateAsync(request);
        var candidate = Assert.IsType<JobCandidate>(await store.PeekAsync());
        var identity = await db.HashGetAsync(prefix + ":job:a", "creationIdentity");
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.TryClaimAsync(candidate, now.AddTicks(1), "token-" + i)));
        var claim = Assert.Single(claims, c => c is not null)!;
        var job = await Detail();
        Assert.Equal(1, job.Attempts); Assert.Equal(JobState.Processing, job.State);
        Assert.Equal(new[] { JobState.Enqueued, JobState.Processing }, job.History.Select(h => h.State));
        var attempt = Assert.Single(job.AttemptHistory);
        Assert.Equal(claim.StartedAt, attempt.StartedAt);
        Assert.Null(attempt.FinishedAt); Assert.Null(attempt.Outcome); Assert.Null(attempt.Error);
        Assert.Equal(identity, await db.HashGetAsync(prefix + ":job:a", "creationIdentity"));
        Assert.Null(await db.SortedSetScoreAsync(prefix + ":ready", "a"));
        Assert.True(await db.SetContainsAsync(prefix + ":processing", "a"));
        Assert.Null(job.ScheduledAt);
        foreach (var key in connection.GetServer(connection.GetEndPoints()[0]).Keys(db.Database, prefix + ":*")) Assert.Null(await db.KeyTimeToLiveAsync(key));
        await store.CreateAsync(request);
        Assert.Equal(JobState.Processing, (await Detail()).State);
    }

    [Theory]
    [InlineData(null, 3, 1000, JobState.Succeeded)]
    [InlineData("demo failure", 1, 500, JobState.Failed)]
    [InlineData("demo failure", 3, 0, JobState.Scheduled)]
    [InlineData("demo failure", 3, 500, JobState.Scheduled)]
    public async Task Completion_persists_exact_result_indexes_and_idempotent_confirmation(string? error, int max, int delay, JobState expected)
    {
        var request = Request("a", new JobOptions { MaxAttempts = max, RetryDelayMs = delay });
        var claim = await Claim(options: new JobOptions { MaxAttempts = max, RetryDelayMs = delay });
        var finish = now.AddTicks(12345);
        var result = new AttemptCompletion(claim, finish, error);
        await store.CompleteAsync(result);
        var job = await Detail();
        Assert.Equal(expected, job.State); Assert.Equal(1, job.Attempts);
        Assert.Equal(request.Prepared.Descriptor.Args.GetRawText(), job.Args.GetRawText());
        Assert.Equal(now, job.CreatedAt); Assert.Equal(finish, job.UpdatedAt);
        Assert.Equal(3, job.History.Count);
        var attempt = Assert.Single(job.AttemptHistory);
        Assert.Equal(finish, attempt.FinishedAt); Assert.Equal(error, attempt.Error);
        Assert.Equal(error is null ? JobAttemptOutcome.Succeeded : JobAttemptOutcome.Failed, attempt.Outcome);
        Assert.False(await db.SetContainsAsync(prefix + ":processing", "a"));
        Assert.Null(await db.SortedSetScoreAsync(prefix + ":ready", "a"));
        if (expected == JobState.Scheduled)
        {
            Assert.Equal(finish.AddMilliseconds(delay), job.ScheduledAt);
            Assert.Equal((double)JobCodec.ScheduledScore(job.ScheduledAt!.Value), await db.SortedSetScoreAsync(prefix + ":scheduled", "a"));
        }
        else { Assert.Null(job.ScheduledAt); Assert.Null(await db.SortedSetScoreAsync(prefix + ":scheduled", "a")); }
        var snapshot = await Dump();
        await store.CompleteAsync(result); await store.CompleteAsync(result);
        await store.CreateAsync(request);
        Assert.Equal(snapshot, await Dump());
        await Assert.ThrowsAnyAsync<Exception>(() => store.CompleteAsync(result with { Claim = claim with { Token = "wrong" } }));
        await Assert.ThrowsAnyAsync<Exception>(() => store.CompleteAsync(result with { Claim = claim with { Cycle = "1" } }));
        await Assert.ThrowsAnyAsync<Exception>(() => store.CompleteAsync(result with { FinishedAt = finish.AddTicks(1) }));
        await Assert.ThrowsAnyAsync<Exception>(() => store.CompleteAsync(result with { Error = error is null ? "different" : null }));
        Assert.Equal(snapshot, await Dump());
    }

    [Fact]
    public async Task Empty_future_and_stale_candidates_do_not_write()
    {
        Assert.Null(await store.PeekAsync());
        await store.CreateAsync(Request("future", new JobOptions { RunAt = now.AddHours(1) }));
        var snapshot = await Dump();
        Assert.Null(await store.PeekAsync()); Assert.Equal(snapshot, await Dump());
        await store.CreateAsync(Request("a"));
        var candidate = Assert.IsType<JobCandidate>(await store.PeekAsync());
        await store.TryClaimAsync(candidate, now, "token");
        snapshot = await Dump();
        Assert.Null(await store.TryClaimAsync(candidate, now, "loser"));
        Assert.Equal(snapshot, await Dump());
        Assert.Equal(JobState.Scheduled, (await Detail("future")).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task Claim_wrong_key_types_fail_before_writing(int index)
    {
        await store.CreateAsync(Request("a"));
        var candidate = Assert.IsType<JobCandidate>(await store.PeekAsync());
        RedisKey[] keys = [.. store.Keys("a"), prefix + ":processing"];
        await db.KeyDeleteAsync(keys[index]);
        if (index == 6) await db.SetAddAsync(keys[index], "bad");
        else await db.StringSetAsync(keys[index], "bad");
        var snapshot = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.TryClaimAsync(candidate, now, "token"));
        Assert.Equal(snapshot, await Dump());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task Completion_wrong_key_types_fail_before_writing(int index)
    {
        var claim = await Claim();
        RedisKey[] keys = [.. store.Keys("a"), prefix + ":processing"];
        await db.KeyDeleteAsync(keys[index]);
        if (index == 6) await db.SetAddAsync(keys[index], "bad");
        else await db.StringSetAsync(keys[index], "bad");
        var snapshot = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.CompleteAsync(new(claim, now.AddSeconds(1), null)));
        Assert.Equal(snapshot, await Dump());
    }

    [Theory]
    [InlineData("state", "scheduled")]
    [InlineData("attempts", "3")]
    [InlineData("args", "{\"message\":\"changed\"}")]
    [InlineData("cycle", "1")]
    public async Task Changed_candidate_is_rejected_without_partial_claim(string field, string value)
    {
        await store.CreateAsync(Request("a"));
        var candidate = Assert.IsType<JobCandidate>(await store.PeekAsync());
        await db.HashSetAsync(prefix + ":job:a", field, value);
        var snapshot = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => store.TryClaimAsync(candidate, now, "token"));
        Assert.Equal(snapshot, await Dump());
    }

    [Theory]
    [InlineData("jobs")] [InlineData("scheduled")] [InlineData("processing")]
    public async Task Inconsistent_claim_indexes_are_rejected(string index)
    {
        await store.CreateAsync(Request("a"));
        var candidate = Assert.IsType<JobCandidate>(await store.PeekAsync());
        if (index == "jobs") await db.SetRemoveAsync(prefix + ":jobs", "a");
        if (index == "scheduled") await db.SortedSetAddAsync(prefix + ":scheduled", "a", 0);
        if (index == "processing") await db.SetAddAsync(prefix + ":processing", "a");
        var snapshot = await Dump();
        await Assert.ThrowsAsync<RedisServerException>(() => store.TryClaimAsync(candidate, now, "token"));
        Assert.Equal(snapshot, await Dump());
    }

    [Theory]
    [InlineData("0")] [InlineData("-1")] [InlineData("1.5")] [InlineData("9007199254740992")]
    public async Task Invalid_fifo_position_fails_before_writing(string position)
    {
        await store.CreateAsync(Request("a"));
        var candidate = Assert.IsType<JobCandidate>(await store.PeekAsync());
        await db.SortedSetAddAsync(prefix + ":ready", "a", double.Parse(position, CultureInfo.InvariantCulture));
        var snapshot = await Dump();
        await Assert.ThrowsAsync<RedisServerException>(() => store.TryClaimAsync(candidate, now, "token"));
        Assert.Equal(snapshot, await Dump());
    }

    [Theory]
    [InlineData("0")] [InlineData("01")] [InlineData("not-a-number")] [InlineData("9007199254740992")]
    public async Task Invalid_fifo_sequence_fails_before_writing(string sequence)
    {
        await store.CreateAsync(Request("a"));
        var candidate = Assert.IsType<JobCandidate>(await store.PeekAsync());
        await db.StringSetAsync(prefix + ":sequence", sequence);
        var snapshot = await Dump();
        await Assert.ThrowsAsync<RedisServerException>(() => store.TryClaimAsync(candidate, now, "token"));
        Assert.Equal(snapshot, await Dump());
    }

    [Theory]
    [InlineData("jobs")] [InlineData("ready")] [InlineData("scheduled")] [InlineData("processing")]
    public async Task Invalid_completion_indexes_preserve_open_attempt(string index)
    {
        var claim = await Claim();
        if (index == "jobs") await db.SetRemoveAsync(prefix + ":jobs", "a");
        if (index == "ready") await db.SortedSetAddAsync(prefix + ":ready", "a", 1);
        if (index == "scheduled") await db.SortedSetAddAsync(prefix + ":scheduled", "a", 1);
        if (index == "processing") await db.SetRemoveAsync(prefix + ":processing", "a");
        var snapshot = await Dump();
        await Assert.ThrowsAsync<RedisServerException>(() => store.CompleteAsync(new(claim, now.AddSeconds(1), null)));
        Assert.Equal(snapshot, await Dump());
    }

    [Fact]
    public async Task Concurrent_equivalent_completions_write_once()
    {
        var claim = await Claim();
        var result = new AttemptCompletion(claim, now.AddSeconds(1), null);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.CompleteAsync(result)));
        var job = await Detail();
        Assert.Equal(3, job.History.Count); Assert.Single(job.AttemptHistory);
        Assert.Equal(JobState.Succeeded, job.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Persisted_contract_errors_close_attempt_without_handler_events_and_continue(bool validation)
    {
        var calls = new List<string>();
        var executionRegistry = new JobRegistry();
        executionRegistry.Register(definition, (_, context) => { calls.Add(context.JobId); return Task.CompletedTask; },
            args => { if (args.Message == "invalid") throw new JobValidationException("invalid arguments"); });
        await store.CreateAsync(Request("a", new JobOptions { MaxAttempts = 1 }));
        await store.CreateAsync(Request("b"));
        await db.HashSetAsync(prefix + ":job:a", "args", validation ? "{\"message\":\"invalid\"}" : "{}");
        await using var server = new JobServer(executionRegistry, connection, prefix, workerCount: 1);
        await server.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await Detail("b")).State != JobState.Succeeded) await Task.Delay(5, timeout.Token);
        await server.StopAsync();
        var failed = await Detail();
        Assert.Equal(JobState.Failed, failed.State);
        Assert.NotNull(Assert.Single(failed.AttemptHistory).Error);
        Assert.Equal(new[] { "b" }, calls);
        Assert.Null(server.LastError);
    }

    [Fact]
    public async Task Startup_enumeration_covers_multiple_scan_pages()
    {
        await Task.WhenAll(Enumerable.Range(0, 300).Select(i => store.CreateAsync(Request("id-" + i))));
        var references = new HashSet<string>();
        await foreach (var reference in store.ReadReferencesAsync()) references.Add(reference.Id);
        Assert.Equal(300, references.Count);
        await using var server = new JobServer(new JobRegistry(), connection, prefix);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
        foreach (var id in references) Assert.Contains("job '" + id + "'", error.Message);
        Assert.Equal(300, await db.SortedSetLengthAsync(prefix + ":ready"));
    }

    [Fact]
    public async Task Nonrepresentable_retry_time_leaves_open_attempt_intact()
    {
        var claim = await Claim();
        var snapshot = await Dump();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CompleteAsync(new(claim, DateTimeOffset.MaxValue, "error")));
        Assert.Equal(snapshot, await Dump());
    }

    [Fact]
    public async Task Reads_during_real_transitions_are_complete_snapshots()
    {
        await store.CreateAsync(Request("a"));
        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < 120; i++)
            {
                var job = await Detail();
                Assert.Equal(job.State, job.History[^1].State);
                Assert.Equal(job.UpdatedAt, job.History[^1].At);
                if (job.State == JobState.Enqueued) { Assert.Equal(0, job.Attempts); Assert.Empty(job.AttemptHistory); }
                else
                {
                    Assert.Equal(1, job.Attempts);
                    var attempt = Assert.Single(job.AttemptHistory);
                    Assert.Equal(job.State == JobState.Processing, attempt.FinishedAt is null);
                }
            }
        });
        var candidate = Assert.IsType<JobCandidate>(await store.PeekAsync());
        var claim = Assert.IsType<ClaimedJob>(await store.TryClaimAsync(candidate, now, "token"));
        await store.CompleteAsync(new(claim, now.AddSeconds(1), null));
        await reader;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Startup_checks_missing_handlers_in_pending_and_finalized_jobs(bool finalized)
    {
        if (finalized) { var claim = await Claim(); await store.CompleteAsync(new(claim, now.AddSeconds(1), null)); }
        else await store.CreateAsync(Request("a"));
        await store.CreateAsync(Request("b"));
        var snapshot = await Dump();
        await using var server = new JobServer(new JobRegistry(), connection, prefix);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
        Assert.Contains("job", error.Message); Assert.Contains("a", error.Message); Assert.Contains("b", error.Message);
        Assert.False(server.IsAvailable); Assert.Equal(snapshot, await Dump());
    }

    [Fact]
    public async Task Residual_processing_is_recovered_before_readiness()
    {
        await Claim(options: new JobOptions { MaxAttempts = 1 });
        await using var server = new JobServer(registry, connection, prefix);
        await server.StartAsync();
        Assert.True(server.IsAvailable);
        await server.StopAsync();
        var job = await Detail();
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(JobAttemptOutcome.Interrupted, Assert.Single(job.AttemptHistory).Outcome);
    }

    [Fact]
    public async Task Library_without_http_persists_handler_success_failure_and_continuity()
    {
        var executionRegistry = new JobRegistry();
        executionRegistry.Register(definition, async (_, context) =>
        {
            await Task.Yield();
            Assert.Equal(1, context.Attempt);
            if (context.JobId == "b") throw new InvalidOperationException("demo failure");
        });
        foreach (var id in new[] { "a", "b", "c" }) await store.CreateAsync(Request(id, new JobOptions { MaxAttempts = 1 }));
        await using var server = new JobServer(executionRegistry, connection, prefix, workerCount: 1);
        await server.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await Detail("c")).State != JobState.Succeeded) await Task.Delay(5, timeout.Token);
        await server.StopAsync();
        Assert.Equal(JobState.Succeeded, (await Detail("a")).State);
        var failure = await Detail("b");
        Assert.Equal(JobState.Failed, failure.State);
        Assert.Equal("demo failure", Assert.Single(failure.AttemptHistory).Error);
        Assert.Null(server.LastError);
    }

    [Fact]
    public async Task Library_without_http_executes_and_stops_after_active_attempt_preserving_pending_job()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionRegistry = new JobRegistry();
        executionRegistry.Register(definition, async (_, context) => { Assert.Equal("a", context.JobId); entered.SetResult(); await release.Task; });
        await store.CreateAsync(Request("a")); await store.CreateAsync(Request("b"));
        await using var server = new JobServer(executionRegistry, connection, prefix, workerCount: 1);
        await server.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(JobState.Processing, (await Detail()).State);
        var stop = server.StopAsync(); Assert.False(stop.IsCompleted); Assert.False(server.IsAvailable);
        release.SetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(JobState.Succeeded, (await Detail()).State);
        Assert.Equal(JobState.Enqueued, (await Detail("b")).State);
        Assert.Equal(0, (await Detail("b")).Attempts);
        Assert.True(connection.IsConnected);
    }
}
