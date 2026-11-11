using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs.IntegrationTests;

public sealed partial class ProcessingTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Same_server_resumes_without_reinvoking_a_live_or_finished_function(bool stopDuringOutage)
    {
        var started = Signal(); var finish = Signal(); var finished = Signal();
        var outage = 0; var calls = 0;
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
            method.Name == nameof(IDatabase.SortedSetRangeByScoreWithScoresAsync) && Volatile.Read(ref outage) != 0
                ? Task.FromException<SortedSetEntry[]>(new RedisConnectionException(ConnectionFailureType.SocketClosed, "offline"))
                : method.Name == nameof(IDatabase.ScriptEvaluateAsync) && Volatile.Read(ref outage) != 0
                ? Task.FromException<RedisResult>(new RedisConnectionException(ConnectionFailureType.SocketClosed, "offline"))
                : proxy.Call(method, args));
        var execution = new JobRegistry();
        execution.Register(definition, async (_, _) =>
        {
            Interlocked.Increment(ref calls); started.TrySetResult();
            await finish.Task; finished.TrySetResult();
        });
        var client = new RedisJobClient(execution.Contracts, connection, prefix);
        var id = await client.EnqueueAsync(definition, new Args("active"));
        await using var server = new JobServer(execution, new RedisJobStore(WithDatabase(database), prefix), workerCount: 1);
        await server.StartAsync(); await started.Task.WaitAsync(Timeout);
        var pending = await client.EnqueueAsync(definition, new Args("pending"));
        Volatile.Write(ref outage, 1);
        await Until(() => Task.FromResult(!server.IsAvailable && server.LastError is not null));
        var origin = server.LastError;
        finish.TrySetResult(); await finished.Task.WaitAsync(Timeout);
        Assert.Equal(JobState.Processing, (await Manager().GetAsync(id))!.State);
        Assert.Equal(1, calls);
        if (stopDuringOutage)
        {
            await server.StopAsync().WaitAsync(Timeout);
            Assert.False(server.IsAvailable); Assert.Same(origin, server.LastError);
            Volatile.Write(ref outage, 0);
            Assert.Equal(1, calls);
            Assert.Equal(JobState.Enqueued, (await Manager().GetAsync(pending))!.State);
            await using var successor = new JobServer(execution, connection, prefix, workerCount: 1);
            await successor.StartAsync();
            await Until(async () => (await Manager().GetAsync(pending))!.State == JobState.Succeeded);
            Assert.Equal(JobAttemptOutcome.Interrupted, (await Manager().GetAsync(id))!.AttemptHistory[0].Outcome);
        }
        else
        {
            Volatile.Write(ref outage, 0);
            await Until(async () => server.IsAvailable && (await Manager().GetAsync(pending))!.State == JobState.Succeeded);
            Assert.Null(server.LastError);
            Assert.Equal(2, calls);
            var job = (await Manager().GetAsync(id))!;
            Assert.Single(job.AttemptHistory);
            Assert.Equal(JobAttemptOutcome.Succeeded, job.AttemptHistory[0].Outcome);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Initial_transport_failure_waits_for_return_or_stop_without_readiness(bool stop)
    {
        var outage = 1;
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
            method.Name == nameof(IDatabase.SortedSetRangeByScoreWithScoresAsync) && Volatile.Read(ref outage) != 0
                ? Task.FromException<SortedSetEntry[]>(new RedisConnectionException(ConnectionFailureType.SocketClosed, "offline"))
                : method.Name == nameof(IDatabase.ScriptEvaluateAsync) && Volatile.Read(ref outage) != 0
                ? Task.FromException<RedisResult>(new RedisTimeoutException("offline", CommandStatus.Unknown))
                : proxy.Call(method, args));
        await store.CreateAsync(Request("a"));
        await using var server = new JobServer(registry, new RedisJobStore(WithDatabase(database), prefix));
        var starting = server.StartAsync();
        Assert.Same(starting, server.StartAsync());
        await Until(() => Task.FromResult(server.LastError is not null));
        Assert.False(server.IsAvailable); Assert.False(starting.IsCompleted);
        if (stop)
        {
            await server.StopAsync().WaitAsync(Timeout); await starting.WaitAsync(Timeout);
            Assert.False(server.IsAvailable); Assert.NotNull(server.LastError);
            Assert.Equal(JobState.Enqueued, (await Detail()).State);
        }
        else
        {
            Volatile.Write(ref outage, 0);
            await starting.WaitAsync(Timeout);
            await Until(() => Task.FromResult(server.IsAvailable && server.LastError is null));
            await Until(async () => (await Detail()).State == JobState.Succeeded);
        }
    }
}

public sealed partial class ProcessingTests
{
    [Fact]
    public async Task Repeated_outages_preserve_first_episode_error_and_fixed_worker_limit()
    {
        var outage = 0; var calls = 0; var active = 0; var peak = 0;
        var gates = new[] { Signal(), Signal() };
        var invocations = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var error = new RedisConnectionException(ConnectionFailureType.SocketClosed, "first observation");
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
            Volatile.Read(ref outage) == 0 ? proxy.Call(method, args) : method.Name switch
            {
                nameof(IDatabase.ScriptEvaluateAsync) => Task.FromException<RedisResult>(error),
                nameof(IDatabase.SortedSetRangeByScoreWithScoresAsync) => Task.FromException<SortedSetEntry[]>(error),
                _ => proxy.Call(method, args)
            });
        var execution = new JobRegistry();
        execution.Register(definition, async (args, context) =>
        {
            invocations.AddOrUpdate(context.JobId, 1, (_, count) => count + 1);
            Interlocked.Increment(ref calls);
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref peak); if (current <= previous) break; }
            while (Interlocked.CompareExchange(ref peak, current, previous) != previous);
            await gates[int.Parse(args.Message)].Task;
            Interlocked.Decrement(ref active);
        });
        await using var server = new JobServer(execution, new RedisJobStore(WithDatabase(database), prefix), workerCount: 2);
        await server.StartAsync();
        var client = new RedisJobClient(execution.Contracts, connection, prefix);
        for (var episode = 0; episode < 2; episode++)
        {
            var ids = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.EnqueueAsync(definition, new Args(episode.ToString()))));
            await Until(() => Task.FromResult(Volatile.Read(ref calls) == episode * 6 + 2));
            Volatile.Write(ref outage, 1);
            await Until(() => Task.FromResult(!server.IsAvailable && server.LastError is not null));
            Assert.Same(error, server.LastError);
            gates[episode].TrySetResult();
            await Until(() => Task.FromResult(Volatile.Read(ref active) == 0));
            Assert.Equal(episode * 6 + 2, Volatile.Read(ref calls));
            Volatile.Write(ref outage, 0);
            await Until(async () => server.IsAvailable && (await Manager().ListAsync()).Where(j => ids.Contains(j.Id)).All(j => j.State == JobState.Succeeded));
            Assert.Null(server.LastError);
        }
        Assert.Equal(12, calls); Assert.Equal(2, peak); Assert.All(invocations.Values, count => Assert.Equal(1, count));
        foreach (var item in await Manager().ListAsync()) Assert.Equal(JobAttemptOutcome.Succeeded, Assert.Single((await Manager().GetAsync(item.Id))!.AttemptHistory).Outcome);
    }
}

public sealed partial class ProcessingTests
{
    [Fact]
    public async Task Hosted_start_cancellation_closes_initial_reconnection_wait_and_preserves_pending_job()
    {
        await store.CreateAsync(Request("a"));
        var database = ForwardingProxy.Create<IDatabase>(db, (proxy, method, args) =>
            method.Name == nameof(IDatabase.ScriptEvaluateAsync)
                ? Task.FromException<RedisResult>(new RedisConnectionException(ConnectionFailureType.SocketClosed, "initial outage"))
                : proxy.Call(method, args));
        await using var server = new JobServer(registry, new RedisJobStore(WithDatabase(database), prefix));
        var hosted = new BackgroundJobs.Hosting.JobServerHostedService(server);
        using var cancellation = new CancellationTokenSource();
        var starting = hosted.StartAsync(cancellation.Token);
        await Until(() => Task.FromResult(server.LastError is not null));
        cancellation.Cancel();
        await starting.WaitAsync(Timeout); await server.StopAsync().WaitAsync(Timeout);
        Assert.False(server.IsAvailable); Assert.NotNull(server.LastError);
        Assert.Equal(JobState.Enqueued, (await Detail()).State);
    }
}
