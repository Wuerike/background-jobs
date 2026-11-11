using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;

namespace BackgroundJobs.IntegrationTests;

public sealed partial class ProcessingTests
{
    public sealed record LegacyPayment(int Euros);
    public sealed record CompatiblePayment(int Euros, int Multiplier = 1);
    public sealed record CurrentPayment(int Cents, string Currency);
    public sealed record RequiredPayment(int Euros, string Currency);
    public sealed record ChangedPayment(string Euros);
    public sealed record RenamedPayment(int Amount);
    public sealed record RemovedPayment();

    [Fact]
    public async Task Persisted_contract_resolves_moved_implementation_and_explicit_optional_default()
    {
        var oldDefinition = new JobDefinition<LegacyPayment>("payment");
        var oldRegistry = new JobRegistry(); oldRegistry.Register(oldDefinition, (_, _) => Task.CompletedTask);
        var id = await new RedisJobClient(oldRegistry.Contracts, connection, prefix).EnqueueAsync(oldDefinition, new LegacyPayment(12));
        var original = (await Manager().GetAsync(id))!;
        var current = new JobRegistry(); var effect = 0;
        Task RenamedMethod(CompatiblePayment args, JobContext _) { effect = args.Euros * args.Multiplier; return Task.CompletedTask; }
        current.Register(new JobDefinition<CompatiblePayment>("payment"), RenamedMethod);
        await using var server = new JobServer(current, connection, prefix, workerCount: 1);
        await server.StartAsync();
        await Until(async () => (await Manager().GetAsync(id))!.State == JobState.Succeeded);
        Assert.Equal(12, effect);
        Assert.Equal("{\"euros\":12}", original.Args.GetRawText());
        Assert.Equal(original.Args.GetRawText(), (await Manager().GetAsync(id))!.Args.GetRawText());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Legacy_adapter_coexists_with_new_contract_through_retry_recovery_and_requeue(bool recover)
    {
        var legacy = new JobDefinition<LegacyPayment>("payment.v1");
        var modern = new JobDefinition<CurrentPayment>("payment.v2");
        var oldRegistry = new JobRegistry(); oldRegistry.Register(legacy, (_, _) => Task.CompletedTask);
        // An old executor is cleanly stopped before another registry is installed.
        await using (var producer = new JobServer(oldRegistry, connection, prefix)) { await producer.StartAsync(); await producer.StopAsync(); }
        var oldClient = new RedisJobClient(oldRegistry.Contracts, connection, prefix);
        var id = await oldClient.EnqueueAsync(legacy, new LegacyPayment(7), new() { RetryDelayMs = 10 });
        var before = (await Manager().GetAsync(id))!;
        if (recover) await store.TryClaimAsync((await store.PeekAsync())!, DateTimeOffset.UtcNow, "terminated-producer");
        var current = new JobRegistry(); var effects = new System.Collections.Concurrent.ConcurrentQueue<(string Name, int Attempt, int Cents, string Currency)>();
        current.Register(legacy, (args, context) =>
        {
            if (context.Attempt == 1) throw new InvalidOperationException("transient business error");
            // v1 euros always mean EUR; adapting to cents must preserve that meaning.
            var adapted = new CurrentPayment(checked(args.Euros * 100), "EUR");
            effects.Enqueue(("v1", context.Attempt, adapted.Cents, adapted.Currency)); return Task.CompletedTask;
        });
        current.Register(modern, (args, context) => { effects.Enqueue(("v2", context.Attempt, args.Cents, args.Currency)); return Task.CompletedTask; });
        var newId = await new RedisJobClient(current.Contracts, connection, prefix).EnqueueAsync(modern, new CurrentPayment(900, "USD"));
        await using var server = new JobServer(current, connection, prefix, workerCount: 1);
        await server.StartAsync();
        await Until(async () => (await Manager().GetAsync(id))!.State == JobState.Succeeded && (await Manager().GetAsync(newId))!.State == JobState.Succeeded);
        var completed = (await Manager().GetAsync(id))!;
        Assert.Equal(recover ? JobAttemptOutcome.Interrupted : JobAttemptOutcome.Failed, completed.AttemptHistory[0].Outcome);
        Assert.Equal(2, completed.Attempts);
        await new RedisJobManager(connection, prefix).RequeueAsync(id);
        await Until(async () => (await Manager().GetAsync(id)) is { State: JobState.Succeeded, AttemptHistory.Count: 4 });
        var repeated = (await Manager().GetAsync(id))!;
        Assert.Equal(before.Name, repeated.Name); Assert.Equal(before.Args.GetRawText(), repeated.Args.GetRawText());
        Assert.Equal(new[] { 1, 2, 1, 2 }, repeated.AttemptHistory.Select(a => a.Attempt));
        Assert.Equal(completed.History, repeated.History.Take(completed.History.Count));
        Assert.Equal(completed.AttemptHistory, repeated.AttemptHistory.Take(completed.AttemptHistory.Count));
        Assert.Equal(2, effects.Count(e => e.Name == "v1" && e.Cents == 700 && e.Currency == "EUR"));
        Assert.Single(effects, e => e.Name == "v2" && e.Cents == 900 && e.Currency == "USD");
    }

    [Theory]
    [InlineData("required")] [InlineData("type")] [InlineData("rename")] [InlineData("remove")]
    [InlineData("validation")] [InlineData("unexpected-validator")]
    public async Task Incompatible_persisted_arguments_exhaust_budget_without_invoking_handler_or_blocking_other_jobs(string change)
    {
        var legacy = new JobDefinition<LegacyPayment>("payment");
        var oldRegistry = new JobRegistry(); oldRegistry.Register(legacy, (_, _) => Task.CompletedTask);
        var id = await new RedisJobClient(oldRegistry.Contracts, connection, prefix).EnqueueAsync(legacy, new LegacyPayment(5), new() { MaxAttempts = 2, RetryDelayMs = 30 });
        var current = new JobRegistry(); var calls = 0;
        Task Handler<T>(T _, JobContext context) { Interlocked.Increment(ref calls); return Task.CompletedTask; }
        switch (change)
        {
            case "required": current.Register(new JobDefinition<RequiredPayment>("payment"), Handler); break;
            case "type": current.Register(new JobDefinition<ChangedPayment>("payment"), Handler); break;
            case "rename": current.Register(new JobDefinition<RenamedPayment>("payment"), Handler); break;
            case "remove": current.Register(new JobDefinition<RemovedPayment>("payment"), Handler); break;
            default: current.Register(legacy, Handler, _ =>
            {
                if (change == "validation") throw new JobValidationException("incompatible value");
                throw new InvalidOperationException("validator bug");
            }); break;
        }
        var healthy = new JobDefinition<LegacyPayment>("healthy"); current.Register(healthy, (_, _) => Task.CompletedTask);
        var other = await new RedisJobClient(current.Contracts, connection, prefix).EnqueueAsync(healthy, new LegacyPayment(3));
        await using var server = new JobServer(current, connection, prefix, workerCount: 1);
        await server.StartAsync();
        await Until(async () => (await Manager().GetAsync(id))!.State == JobState.Failed && (await Manager().GetAsync(other))!.State == JobState.Succeeded);
        Assert.Equal(0, calls); Assert.True(server.IsAvailable); Assert.Null(server.LastError);
        var failed = (await Manager().GetAsync(id))!;
        Assert.Equal(2, failed.Attempts); Assert.All(failed.AttemptHistory, a => { Assert.Equal(JobAttemptOutcome.Failed, a.Outcome); Assert.False(string.IsNullOrEmpty(a.Error)); });
        Assert.True(failed.AttemptHistory[1].StartedAt >= failed.AttemptHistory[0].FinishedAt!.Value.AddMilliseconds(30));
        Assert.Equal("{\"euros\":5}", failed.Args.GetRawText());
    }

    [Theory]
    [InlineData(JobState.Scheduled)] [InlineData(JobState.Enqueued)] [InlineData(JobState.Processing)]
    [InlineData(JobState.Succeeded)] [InlineData(JobState.Failed)] [InlineData(JobState.Deleted)]
    public async Task Missing_handler_in_each_state_blocks_all_changes_and_restored_registry_preserves_data(JobState state)
    {
        await InState(state);
        for (var i = 0; i < 140; i++) await store.CreateAsync(Request("page-" + i));
        var another = new JobDefinition<Args>("another-name");
        registry.Register(another, (_, _) => Task.CompletedTask);
        await new RedisJobClient(registry.Contracts, connection, prefix).EnqueueAsync(another, new Args("other"));
        var absent = new JobRegistry(); absent.Contracts.Add(definition);
        var before = await Dump();
        await using (var blocked = new JobServer(absent, connection, prefix))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.StartAsync());
            Assert.Contains("job", error.Message); Assert.Contains("a", error.Message); Assert.Contains("page-139", error.Message); Assert.Contains("another-name", error.Message);
            Assert.False(blocked.IsAvailable); Assert.Equal(before, await Dump());
            Assert.Equal(142, (await Manager().ListAsync()).Count); Assert.NotNull(await Manager().GetAsync("a"));
        }
        await using var restored = new JobServer(registry, connection, prefix);
        await restored.StartAsync();
        await Until(async () => (await Manager().GetAsync("page-139"))!.State == JobState.Succeeded);
        if (state == JobState.Processing) Assert.Equal(JobAttemptOutcome.Interrupted, (await Detail()).AttemptHistory[0].Outcome);
        else if (state is JobState.Succeeded or JobState.Failed or JobState.Deleted) Assert.Equal(state, (await Detail()).State);
        else await Until(async () => (await Detail()).State == JobState.Succeeded);
    }
}

public sealed partial class ProcessingTests
{
    [Fact]
    public async Task Scheduled_legacy_contract_runs_after_registry_replacement_while_new_version_remains_distinct()
    {
        var oldDefinition = new JobDefinition<LegacyPayment>("payment.v1");
        var newDefinition = new JobDefinition<CurrentPayment>("payment.v2");
        var producerRegistry = new JobRegistry(); var oldCalls = 0;
        producerRegistry.Register(oldDefinition, (_, _) => { oldCalls++; return Task.CompletedTask; });
        var id = await new RedisJobClient(producerRegistry.Contracts, connection, prefix, new Clock(now))
            .EnqueueAsync(oldDefinition, new LegacyPayment(11), new() { RunAt = now.AddSeconds(1) });
        await using (var producer = new JobServer(producerRegistry, connection, prefix, new Clock(now)))
        { await producer.StartAsync(); await producer.StopAsync(); }
        Assert.Equal(0, oldCalls); Assert.Equal(JobState.Scheduled, (await Manager().GetAsync(id))!.State);
        var original = (await Manager().GetAsync(id))!;
        var consumerRegistry = new JobRegistry(); var effects = new System.Collections.Concurrent.ConcurrentBag<string>();
        consumerRegistry.Register(oldDefinition, (args, _) => { effects.Add("EUR:" + args.Euros * 100); return Task.CompletedTask; });
        consumerRegistry.Register(newDefinition, (args, _) => { effects.Add(args.Currency + ":" + args.Cents); return Task.CompletedTask; });
        var modern = await new RedisJobClient(consumerRegistry.Contracts, connection, prefix, new Clock(now.AddSeconds(2)))
            .EnqueueAsync(newDefinition, new CurrentPayment(99, "USD"));
        await using var consumer = new JobServer(consumerRegistry, connection, prefix, new Clock(now.AddSeconds(2)), workerCount: 1);
        await consumer.StartAsync();
        await Until(async () => (await Manager().GetAsync(id))!.State == JobState.Succeeded && (await Manager().GetAsync(modern))!.State == JobState.Succeeded);
        Assert.Contains("EUR:1100", effects); Assert.Contains("USD:99", effects);
        var completed = (await Manager().GetAsync(id))!;
        Assert.Equal(original.Args.GetRawText(), completed.Args.GetRawText()); Assert.Equal(original.Name, completed.Name);
        Assert.True(Assert.Single(completed.AttemptHistory).StartedAt >= original.ScheduledAt);
    }
}

public sealed partial class ProcessingTests
{
    [Theory]
    [InlineData(JobState.Scheduled)] [InlineData(JobState.Enqueued)] [InlineData(JobState.Processing)]
    [InlineData(JobState.Succeeded)] [InlineData(JobState.Failed)] [InlineData(JobState.Deleted)]
    public async Task Existing_records_without_operation_receipts_or_executor_generation_need_no_migration(JobState state)
    {
        await InState(state);
        // Job hashes and lists retain the existing format. Only auxiliary receipts
        // are absent in data written by applications without reconciliation.
        var receipts = connection.GetServer(connection.GetEndPoints()[0]).Keys(db.Database, prefix + ":job:*:operation:*").ToArray();
        if (receipts.Length != 0) await db.KeyDeleteAsync(receipts);
        var original = await Detail();
        var manager = new RedisJobManager(connection, prefix);
        Assert.Equal(original.Args.GetRawText(), (await manager.GetAsync("a"))!.Args.GetRawText());
        await using var server = new JobServer(registry, connection, prefix, workerCount: 1);
        await server.StartAsync();
        if (state is JobState.Succeeded or JobState.Failed or JobState.Deleted or JobState.Processing)
        {
            await Until(async () => (await manager.GetAsync("a"))!.State is JobState.Succeeded or JobState.Failed or JobState.Deleted);
            await manager.RequeueAsync("a");
        }
        await Until(async () => (await manager.GetAsync("a"))!.State == JobState.Succeeded);
        var final = (await manager.GetAsync("a"))!;
        Assert.Equal(original.Name, final.Name); Assert.Equal(original.Args.GetRawText(), final.Args.GetRawText());
        Assert.Equal(original.CreatedAt, final.CreatedAt); Assert.Equal(original.History, final.History.Take(original.History.Count));
    }
}
