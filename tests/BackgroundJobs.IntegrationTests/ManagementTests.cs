using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs.IntegrationTests;

public sealed partial class ProcessingTests
{
    private RedisJobManager Manager(DateTimeOffset? at = null) => new(connection, prefix, new Clock(at ?? now.AddSeconds(1)));

    private async Task InState(JobState state, string id = "a")
    {
        if (state == JobState.Scheduled) { await store.CreateAsync(Request(id, new() { RunAt = now.AddSeconds(2) })); return; }
        await store.CreateAsync(Request(id, new() { MaxAttempts = 1 }));
        if (state == JobState.Enqueued) return;
        if (state == JobState.Deleted) { Assert.Equal(JobOperationResult.Applied, await Manager().DeleteAsync(id)); return; }
        var claim = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddTicks(1), "initial"))!;
        if (state != JobState.Processing) await store.CompleteAsync(new(claim, now.AddTicks(2), state == JobState.Failed ? "failure" : null));
    }

    [Theory]
    [InlineData(JobState.Scheduled)] [InlineData(JobState.Enqueued)] [InlineData(JobState.Processing)]
    [InlineData(JobState.Succeeded)] [InlineData(JobState.Failed)] [InlineData(JobState.Deleted)]
    public async Task Manual_operations_decide_by_state_and_preserve_immutable_data(JobState state)
    {
        await InState(state);
        var manager = Manager();
        var original = await Detail(); var before = await Dump();
        var allowedDelete = state is JobState.Scheduled or JobState.Enqueued;
        Assert.Equal(allowedDelete ? JobOperationResult.Applied : JobOperationResult.Conflict, await manager.DeleteAsync("a"));
        if (!allowedDelete) Assert.Equal(before, await Dump());
        var deleted = await Detail();
        if (allowedDelete)
        {
            Assert.Equal(JobState.Deleted, deleted.State); Assert.Null(deleted.ScheduledAt);
            Assert.Equal(original.Attempts, deleted.Attempts);
            Assert.Equal(original.History, deleted.History.Take(original.History.Count));
            Assert.Equal(original.AttemptHistory, deleted.AttemptHistory);
        }
        var eligible = deleted.State is JobState.Succeeded or JobState.Failed or JobState.Deleted;
        before = await Dump();
        Assert.Equal(eligible ? JobOperationResult.Applied : JobOperationResult.Conflict, await manager.RequeueAsync("a"));
        if (!eligible) Assert.Equal(before, await Dump());
        var result = await Detail();
        if (eligible)
        {
            Assert.Equal(JobState.Enqueued, result.State); Assert.Equal(0, result.Attempts); Assert.Null(result.ScheduledAt);
            Assert.Equal(deleted.History, result.History.Take(deleted.History.Count));
            Assert.Equal(deleted.AttemptHistory, result.AttemptHistory);
            Assert.Equal("1", (await db.HashGetAsync(prefix + ":job:a", "cycle")).ToString());
        }
        Assert.Equal(original.Args.GetRawText(), result.Args.GetRawText()); Assert.Equal(original.Name, result.Name);
        Assert.Equal(original.MaxAttempts, result.MaxAttempts); Assert.Equal(original.RetryDelayMs, result.RetryDelayMs);
        Assert.Equal(original.CreatedAt, result.CreatedAt);
        Assert.True(await db.SetContainsAsync(prefix + ":jobs", "a"));
    }

    [Fact]
    public async Task List_pages_all_states_and_requeue_counts_without_loading_payloads()
    {
        Assert.Empty(await Manager().ListAsync());
        var states = Enum.GetValues<JobState>();
        foreach (var state in new[] { JobState.Succeeded, JobState.Failed, JobState.Processing, JobState.Deleted, JobState.Enqueued, JobState.Scheduled })
            await InState(state, state.ToString());
        for (var i = 0; i < 260; i++) await store.CreateAsync(Request("page-" + i));
        var before = await Dump(); var list = await Manager().ListAsync();
        Assert.Equal(266, list.Count); Assert.Equal(list.Count, list.Select(x => x.Id).Distinct().Count());
        Assert.Equal(states.Order(), list.Where(x => !x.Id.StartsWith("page-")).Select(x => x.State).Order());
        Assert.Equal(before, await Dump());
        Assert.Throws<NotSupportedException>(() => ((IList<JobSummary>)list).Clear());
        Assert.Empty(await new RedisJobManager(connection, prefix + "-other").ListAsync());
    }

    [Theory]
    [InlineData("id")] [InlineData("name")] [InlineData("state")] [InlineData("attempts")] [InlineData("missing")] [InlineData("type")]
    public async Task List_rejects_indexed_invalid_projection(string field)
    {
        await store.CreateAsync(Request("a"));
        if (field == "missing") await db.KeyDeleteAsync(prefix + ":job:a");
        else if (field == "type") { await db.KeyDeleteAsync(prefix + ":job:a"); await db.StringSetAsync(prefix + ":job:a", "bad"); }
        else await db.HashSetAsync(prefix + ":job:a", field, field == "name" ? " " : "invalid");
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => Manager().ListAsync()); Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData("")] [InlineData(" unknown ")] [InlineData("opaque/id:🦊")]
    public async Task Manual_absence_is_opaque_and_read_only(string id)
    {
        var before = await Dump();
        Assert.Equal(JobOperationResult.NotFound, await Manager().DeleteAsync(id));
        Assert.Equal(JobOperationResult.NotFound, await Manager().RequeueAsync(id));
        Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Indexed_absence_is_corruption_not_not_found(bool requeue)
    {
        await db.SetAddAsync(prefix + ":jobs", "missing");
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => requeue ? Manager().RequeueAsync("missing") : Manager().DeleteAsync("missing"));
        Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)]
    [InlineData(false, 4)] [InlineData(false, 5)] [InlineData(false, 6)] [InlineData(false, 7)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)]
    [InlineData(true, 4)] [InlineData(true, 5)] [InlineData(true, 6)] [InlineData(true, 7)]
    public async Task Manual_wrong_types_never_write_partially(bool requeue, int index)
    {
        await InState(requeue ? JobState.Deleted : JobState.Enqueued);
        RedisKey[] keys = [.. store.Keys("a"), prefix + ":processing"];
        await db.KeyDeleteAsync(keys[index]);
        if (index == 6) await db.SetAddAsync(keys[index], "bad"); else await db.StringSetAsync(keys[index], "bad");
        var before = await Dump();
        await Assert.ThrowsAnyAsync<Exception>(() => requeue ? Manager().RequeueAsync("a") : Manager().DeleteAsync("a"));
        Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData("jobs")] [InlineData("ready")] [InlineData("processing")] [InlineData("scheduled")]
    [InlineData("cycle")] [InlineData("history")] [InlineData("attempts")] [InlineData("time")]
    public async Task Manual_corrupt_metadata_never_becomes_conflict_or_partial_write(string corruption)
    {
        await InState(JobState.Enqueued);
        switch (corruption)
        {
            case "jobs": await db.SetRemoveAsync(prefix + ":jobs", "a"); break;
            case "ready": await db.SortedSetRemoveAsync(prefix + ":ready", "a"); break;
            case "processing": await db.SetAddAsync(prefix + ":processing", "a"); break;
            case "scheduled": await db.SortedSetAddAsync(prefix + ":scheduled", "a", 1); break;
            case "cycle": await db.HashSetAsync(prefix + ":job:a", "cycle", "01"); break;
            case "history": await db.ListSetByIndexAsync(prefix + ":job:a:history", 0, "{}"); break;
            case "attempts": await db.HashSetAsync(prefix + ":job:a", "attempts", "1"); break;
        }
        var before = await Dump();
        var manager = Manager(corruption == "time" ? now.AddTicks(-1) : null);
        await Assert.ThrowsAnyAsync<Exception>(() => manager.DeleteAsync("a")); Assert.Equal(before, await Dump());
    }

    [Theory]
    [InlineData("0", "1")] [InlineData("9007199254740992", "9007199254740993")]
    [InlineData("9223372036854775806", "9223372036854775807")]
    public async Task Requeue_increments_cycle_exactly_across_full_long_range(string initial, string expected)
    {
        await InState(JobState.Deleted);
        await db.HashSetAsync(prefix + ":job:a", "cycle", initial);
        Assert.Equal(JobOperationResult.Applied, await Manager().RequeueAsync("a"));
        Assert.Equal(expected, (await db.HashGetAsync(prefix + ":job:a", "cycle")).ToString());
        var claim = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddSeconds(2), "exact"))!;
        await store.CompleteAsync(new(claim, now.AddSeconds(3), null));
    }

    [Theory]
    [InlineData("cycle", "9223372036854775807")] [InlineData("cycle", "9223372036854775808")]
    [InlineData("cycle", "-1")] [InlineData("cycle", "00")]
    [InlineData("sequence", "9007199254740991")] [InlineData("sequence", "00")]
    public async Task Requeue_rejects_exhausted_or_invalid_counters_without_writing(string field, string value)
    {
        await InState(JobState.Deleted);
        if (field == "cycle") await db.HashSetAsync(prefix + ":job:a", field, value);
        else await db.StringSetAsync(prefix + ":sequence", value);
        var before = await Dump(); await Assert.ThrowsAnyAsync<Exception>(() => Manager().RequeueAsync("a")); Assert.Equal(before, await Dump());
    }

    [Fact]
    public async Task Requeue_uses_final_valid_fifo_position_and_then_rejects_overflow()
    {
        await InState(JobState.Deleted);
        await db.StringSetAsync(prefix + ":sequence", "9007199254740990");
        Assert.Equal(JobOperationResult.Applied, await Manager().RequeueAsync("a"));
        Assert.Equal(9007199254740991d, await db.SortedSetScoreAsync(prefix + ":ready", "a"));
        await Manager().DeleteAsync("a");
        var before = await Dump(); await Assert.ThrowsAnyAsync<Exception>(() => Manager().RequeueAsync("a")); Assert.Equal(before, await Dump());
    }

    [Fact]
    public async Task Requeue_preserves_original_creation_replay_and_places_job_after_ready_tail()
    {
        var request = Request("a", new() { RunAt = now.AddHours(1) }); await store.CreateAsync(request);
        await Manager().DeleteAsync("a"); await store.CreateAsync(Request("b"));
        await Manager().RequeueAsync("a");
        var before = await Dump(); await store.CreateAsync(request); Assert.Equal(before, await Dump());
        Assert.Equal("b", (await store.PeekAsync())!.Job.Id); Assert.Null((await Detail()).ScheduledAt);
        var claim = (await store.TryClaimAsync((await store.PeekAsync())!, now.AddSeconds(2), "b"))!;
        await store.CompleteAsync(new(claim, now.AddSeconds(3), null));
        Assert.Equal("a", (await store.PeekAsync())!.Job.Id);
    }

    [Fact]
    public async Task Multiple_real_cycles_reset_budget_and_preserve_all_errors_and_attempts()
    {
        var calls = new List<int>(); var execution = new JobRegistry();
        execution.Register(definition, (_, context) => { calls.Add(context.Attempt); return context.Attempt == 1 ? Task.FromException(new Exception("first")) : Task.CompletedTask; });
        var manager = new RedisJobManager(connection, prefix);
        var id = await new RedisJobClient(execution.Contracts, connection, prefix).EnqueueAsync(definition, new Args("original"), new() { MaxAttempts = 2, RetryDelayMs = 1 });
        await using var server = new JobServer(execution, connection, prefix, workerCount: 1);
        await server.StartAsync();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            await Until(async () => (await manager.GetAsync(id))!.State == JobState.Succeeded);
            var before = (await manager.GetAsync(id))!;
            Assert.Equal(2, before.Attempts); Assert.Equal((cycle + 1) * 2, before.AttemptHistory.Count);
            Assert.Equal(before.Attempts, Assert.Single(await manager.ListAsync()).Attempts);
            Assert.All(before.AttemptHistory.Where(a => a.Attempt == 1), a => Assert.Equal("first", a.Error));
            if (cycle == 2) break;
            Assert.Equal(JobOperationResult.Applied, await manager.RequeueAsync(id));
            var after = (await manager.GetAsync(id))!;
            Assert.Equal(before.AttemptHistory, after.AttemptHistory.Take(before.AttemptHistory.Count));
            Assert.Equal(before.History, after.History.Take(before.History.Count));
        }
        await server.StopAsync(); Assert.Equal(new[] { 1, 2, 1, 2, 1, 2 }, calls); Assert.Null(server.LastError);
    }
}

public sealed partial class ProcessingTests
{
    [Fact]
    public async Task Single_worker_starts_already_ready_job_before_requeued_job_with_equal_timestamps()
    {
        await store.CreateAsync(Request("a")); await Manager(now).DeleteAsync("a");
        await store.CreateAsync(Request("b")); await Manager(now).RequeueAsync("a");
        var calls = new List<string>(); var execution = new JobRegistry();
        execution.Register(definition, (_, context) => { calls.Add(context.JobId); return Task.CompletedTask; });
        await using var server = new JobServer(execution, connection, prefix, new Clock(now), workerCount: 1);
        await server.StartAsync();
        await Until(async () => (await Detail("a")).State == JobState.Succeeded);
        await server.StopAsync();
        Assert.Equal(new[] { "b", "a" }, calls); Assert.Null(server.LastError);
        Assert.All((await Detail("a")).History, change => Assert.Equal(now, change.At));
    }
}
