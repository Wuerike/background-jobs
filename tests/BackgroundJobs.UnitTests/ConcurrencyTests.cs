using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using BackgroundJobs.Hosting;
using BackgroundJobs.Internal;

namespace BackgroundJobs.UnitTests;

public sealed partial class ConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private sealed record Args(string Message);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JobCandidate Candidate(string id, string json = "{\"message\":\"ok\"}")
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        return new(new(id, "job", JsonDocument.Parse(json).RootElement, JobState.Enqueued, 0, 1, 0,
            null, now, now, [new(JobState.Enqueued, now, "created")], []), id, "0", 1);
    }
    private sealed class Store(params JobCandidate[] jobs) : IProcessingStore
    {
        private readonly object gate = new();
        private readonly Queue<JobCandidate> ready = new(jobs);
        public readonly ConcurrentQueue<string> Reservations = new();
        public readonly ConcurrentQueue<AttemptCompletion> Completions = new();
        public Func<int, Task<JobCandidate?>>? Peek;
        public Func<JobCandidate, Task>? BeforeClaim;
        public Func<ClaimedJob, Task>? AcknowledgeClaim;
        public Func<AttemptCompletion, Task>? Complete;
        public TaskCompletionSource? Initialization;
        public bool Residual;
        public JobReference[] References = [];
        public Func<DateTimeOffset, long, Task<IReadOnlyList<ScheduledCandidate>>>? SelectScheduled;
        public Func<ScheduledCandidate, DateTimeOffset, Task<PromotionResult>>? Promote;
        public int Checks, Scans, Reads, Losers, Selections, Promotions;
        public int Pending { get { lock (gate) return ready.Count; } }
        public async Task<bool> HasProcessingAsync()
        {
            Interlocked.Increment(ref Checks);
            if (Initialization is not null) await Initialization.Task;
            return Residual;
        }
        public async IAsyncEnumerable<JobReference> ReadReferencesAsync()
        {
            Interlocked.Increment(ref Scans);
            await Task.CompletedTask;
            foreach (var reference in References) yield return reference;
        }
        public async Task<ScheduledBatch> ReadScheduledAsync(DateTimeOffset now, long offset)
        {
            Interlocked.Increment(ref Selections);
            return new ScheduledBatch((await (SelectScheduled?.Invoke(now, offset) ?? Task.FromResult<IReadOnlyList<ScheduledCandidate>>([]))).ToList());
        }
        public Task<PromotionResult> TryPromoteAsync(ScheduledCandidate candidate, DateTimeOffset now)
        {
            Interlocked.Increment(ref Promotions);
            return Promote!(candidate, now);
        }
        public Task<JobCandidate?> PeekAsync()
        {
            var call = Interlocked.Increment(ref Reads);
            var peek = Peek;
            if (peek is not null) return peek(call);
            lock (gate) return Task.FromResult(ready.TryPeek(out var job) ? job : null);
        }
        public async Task<ClaimedJob?> TryClaimAsync(JobCandidate candidate, DateTimeOffset at, string token)
        {
            Reservations.Enqueue(candidate.Job.Id);
            if (BeforeClaim is not null) await BeforeClaim(candidate);
            lock (gate)
            {
                if (!ready.TryPeek(out var head) || head != candidate) { Interlocked.Increment(ref Losers); return null; }
                ready.Dequeue();
            }
            var claim = new ClaimedJob(candidate.Job, 1, "0", token, at);
            if (AcknowledgeClaim is not null) await AcknowledgeClaim(claim);
            return claim;
        }
        public async Task CompleteAsync(AttemptCompletion completion)
        {
            if (Complete is not null) await Complete(completion);
            Completions.Enqueue(completion);
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(int.MinValue)]
    public void Invalid_worker_count_fails_before_accessing_storage(int count)
    {
        var store = new Store();
        Assert.Equal("workerCount", Assert.Throws<ArgumentOutOfRangeException>(() => new JobServer(new(), store, workerCount: count)).ParamName);
        Assert.Equal(0, store.Checks); Assert.Equal(0, store.Reads);
        Assert.Equal("workerCount", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JobServer(new(), (StackExchange.Redis.IConnectionMultiplexer)null!, "prefix", TimeProvider.System, workerCount: count)).ParamName);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(null)]
    public async Task Worker_slots_bound_parallel_invocations_and_each_free_slot_makes_progress(int? configured)
    {
        var count = configured ?? 4;
        var jobs = Enumerable.Range(0, count + 3).Select(i => Candidate(i.ToString())).ToArray();
        var store = new Store(jobs);
        var entered = Channel.CreateUnbounded<string>();
        var releases = jobs.ToDictionary(j => j.Job.Id, _ => Signal());
        var invocations = new ConcurrentDictionary<string, int>();
        var registry = new JobRegistry();
        var active = 0; var peak = 0;
        registry.Register(new JobDefinition<Args>("job"), async (_, context) =>
        {
            invocations.AddOrUpdate(context.JobId, 1, (_, n) => n + 1);
            var running = Interlocked.Increment(ref active);
            lock (releases) peak = Math.Max(peak, running);
            entered.Writer.TryWrite(context.JobId);
            try { await releases[context.JobId].Task; }
            finally { Interlocked.Decrement(ref active); }
        });
        await using var server = configured is { } n ? new JobServer(registry, store, workerCount: n) : new JobServer(registry, store);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => server.StartAsync()));
            var first = new List<string>();
            for (var i = 0; i < count; i++) first.Add(await entered.Reader.ReadAsync().AsTask().WaitAsync(Timeout));
            Assert.Equal(count, active); Assert.Equal(count, peak); Assert.Equal(3, store.Pending);
            releases[first[0]].SetResult();
            var next = await entered.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            Assert.DoesNotContain(next, first);
            Assert.All(first.Skip(1), id => Assert.False(releases[id].Task.IsCompleted));
            foreach (var release in releases.Values) release.TrySetResult();
            await Until(() => store.Completions.Count == jobs.Length);
            await server.StopAsync();
            Assert.Equal(count, peak); Assert.Equal(jobs.Length, invocations.Count);
            Assert.All(invocations.Values, calls => Assert.Equal(1, calls));
            Assert.Equal(1, store.Checks); Assert.Equal(1, store.Scans); Assert.Null(server.LastError);
        }
        finally { foreach (var release in releases.Values) release.TrySetResult(); }
    }

    [Fact]
    public async Task Contending_workers_invoke_only_the_winning_reservation_and_keep_draining()
    {
        var jobs = new[] { Candidate("a"), Candidate("b"), Candidate("c") };
        var store = new Store(jobs); var allRead = Signal();
        store.Peek = async call =>
        {
            if (call <= 4)
            {
                if (call == 4) allRead.SetResult();
                await allRead.Task;
                return jobs[0];
            }
            // The normal synchronized queue remains responsible for later candidates.
            store.Peek = null;
            return await store.PeekAsync();
        };
        var calls = new ConcurrentQueue<string>(); var registry = new JobRegistry();
        registry.Register(new JobDefinition<Args>("job"), (_, context) => { calls.Enqueue(context.JobId); return Task.CompletedTask; });
        await using var server = new JobServer(registry, store);
        try
        {
            await server.StartAsync(); await Until(() => store.Completions.Count == 3 && store.Losers >= 3);
            await server.StopAsync();
            Assert.Equal(new[] { "a", "b", "c" }, calls.Order());
            Assert.Equal(3, store.Completions.Count); Assert.Null(server.LastError);
        }
        finally { allRead.TrySetResult(); }
    }

    [Fact]
    public async Task Single_worker_obeys_fifo_and_waits_for_each_completion_acknowledgement()
    {
        var store = new Store(Candidate("A"), Candidate("B"), Candidate("C"));
        var confirming = Signal(); var confirmation = Signal(); var calls = new List<string>();
        store.Complete = async result => { if (result.Claim.Job.Id == "A") { confirming.SetResult(); await confirmation.Task; } };
        var registry = new JobRegistry();
        registry.Register(new JobDefinition<Args>("job"), (_, context) => { calls.Add(context.JobId); return Task.CompletedTask; });
        await using var server = new JobServer(registry, store, workerCount: 1);
        try
        {
            await server.StartAsync(); await confirming.Task.WaitAsync(Timeout);
            Assert.Equal(new[] { "A" }, calls); Assert.Equal(new[] { "A" }, store.Reservations);
            confirmation.SetResult(); await Until(() => store.Completions.Count == 3); await server.StopAsync();
            Assert.Equal(new[] { "A", "B", "C" }, calls);
        }
        finally { confirmation.TrySetResult(); }
    }

    [Theory]
    [InlineData("json")] [InlineData("validation")] [InlineData("sync")] [InlineData("async")]
    public async Task Attempt_errors_are_isolated_while_a_sibling_function_remains_active(string failure)
    {
        var store = new Store(Candidate("active"), Candidate("bad", failure == "json" ? "{}" : "{\"message\":\"bad\"}"), Candidate("healthy"));
        var active = Signal(); var release = Signal(); var healthy = Signal(); var registry = new JobRegistry();
        registry.Register(new JobDefinition<Args>("job"), (args, context) =>
        {
            if (context.JobId == "active") { active.SetResult(); return release.Task; }
            if (context.JobId == "bad") return failure == "sync" ? throw new Exception("sync") : Task.FromException(new Exception("async"));
            healthy.SetResult(); return Task.CompletedTask;
        }, args => { if (failure == "validation" && args.Message == "bad") throw new JobValidationException("invalid"); });
        await using var server = new JobServer(registry, store, workerCount: 2);
        try
        {
            await server.StartAsync(); await active.Task.WaitAsync(Timeout); await healthy.Task.WaitAsync(Timeout);
            Assert.False(release.Task.IsCompleted); Assert.True(server.IsAvailable); Assert.Null(server.LastError);
            release.SetResult(); await Until(() => store.Completions.Count == 3); await server.StopAsync();
            Assert.NotNull(store.Completions.Single(c => c.Claim.Job.Id == "bad").Error);
            Assert.All(store.Completions.Where(c => c.Claim.Job.Id != "bad"), c => Assert.Null(c.Error));
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("peek")] [InlineData("claim")] [InlineData("complete")]
    public async Task Fatal_worker_failure_closes_all_admission_and_drains_sibling_preserving_first_error(string stage)
    {
        var jobs = new[] { Candidate("active"), Candidate("fatal"), Candidate("pending") };
        var store = new Store(jobs); var active = Signal(); var release = Signal(); var fail = Signal();
        var error = new IOException("first infrastructure error"); var later = new IOException("second infrastructure error");
        var registry = new JobRegistry(); var calls = new ConcurrentQueue<string>();
        registry.Register(new JobDefinition<Args>("job"), (_, context) =>
        {
            calls.Enqueue(context.JobId);
            if (context.JobId == "active") { active.SetResult(); return release.Task; }
            return Task.CompletedTask;
        });
        if (stage == "peek") store.Peek = async call =>
        {
            if (call == 1) return jobs[0];
            await active.Task; await fail.Task; throw error;
        };
        store.BeforeClaim = async job => { if (stage == "claim" && job.Job.Id == "fatal") { await active.Task; await fail.Task; throw error; } };
        store.Complete = async result =>
        {
            if (result.Claim.Job.Id == "active") throw later;
            if (stage == "complete") { await active.Task; await fail.Task; throw error; }
        };
        await using var server = new JobServer(registry, store, workerCount: 2);
        try
        {
            await server.StartAsync(); await active.Task.WaitAsync(Timeout); fail.SetResult();
            await Until(() => server.LastError is not null);
            Assert.Same(error, server.LastError); Assert.False(server.IsAvailable);
            var reservations = store.Reservations.Count;
            var stop = server.StopAsync(); Assert.False(stop.IsCompleted);
            release.SetResult(); await stop.WaitAsync(Timeout);
            Assert.Same(error, server.LastError); Assert.Equal(reservations, store.Reservations.Count);
            Assert.DoesNotContain("pending", calls); Assert.Equal(stage == "complete" ? 2 : 1, calls.Count);
            Assert.Empty(store.Completions);
        }
        finally { fail.TrySetResult(); release.TrySetResult(); }
    }

    private sealed class IdleClock : TimeProvider
    {
        public readonly TaskCompletionSource Waiting = Signal();
        public readonly TaskCompletionSource Disposed = Signal();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(100), dueTime);
            Waiting.TrySetResult(); return new Timer(Disposed);
        }
        private sealed class Timer(TaskCompletionSource disposed) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => disposed.TrySetResult();
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task Fatal_failure_rejects_already_read_candidate_and_cancels_sibling_idle_wait()
    {
        var job = Candidate("stale"); var store = new Store(job); var clock = new IdleClock();
        var staleRead = Signal(); var staleRelease = Signal(); var fail = Signal(); var error = new IOException("read failure");
        store.Peek = async call =>
        {
            if (call == 1) { await fail.Task; throw error; }
            if (call == 2) { staleRead.SetResult(); await staleRelease.Task; return job; }
            return null;
        };
        var registry = new JobRegistry(); registry.Register(new JobDefinition<Args>("job"), (_, _) => throw new Exception("must not enter"));
        await using var server = new JobServer(registry, store, clock, workerCount: 3);
        try
        {
            await server.StartAsync(); await staleRead.Task.WaitAsync(Timeout); await clock.Waiting.Task.WaitAsync(Timeout);
            fail.SetResult(); await Until(() => server.LastError is not null); await clock.Disposed.Task.WaitAsync(Timeout);
            staleRelease.SetResult(); await server.StopAsync().WaitAsync(Timeout);
            Assert.Empty(store.Reservations); Assert.Empty(store.Completions); Assert.Same(error, server.LastError);
        }
        finally { fail.TrySetResult(); staleRelease.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Stop_or_fatal_failure_waits_for_every_admitted_reservation_and_confirmation(bool fatal)
    {
        var jobs = new[] { Candidate("a"), Candidate("b"), Candidate("pending") };
        var store = new Store(jobs); var acknowledgements = new[] { Signal(), Signal() };
        var reserved = Channel.CreateUnbounded<string>(); var entered = Channel.CreateUnbounded<string>();
        var confirming = Channel.CreateUnbounded<string>(); var releases = new[] { Signal(), Signal() }; var confirmations = new[] { Signal(), Signal() };
        var fail = Signal(); var error = new IOException("read failure"); var firstReserved = Signal();
        store.AcknowledgeClaim = async claim => { if (claim.Job.Id == "a") firstReserved.TrySetResult(); reserved.Writer.TryWrite(claim.Job.Id); await acknowledgements[claim.Job.Id == "a" ? 0 : 1].Task; };
        store.Complete = async result => { confirming.Writer.TryWrite(result.Claim.Job.Id); await confirmations[result.Claim.Job.Id == "a" ? 0 : 1].Task; };
        if (fatal) store.Peek = async call =>
        {
            if (call == 1) return jobs[0];
            if (call == 2) { await firstReserved.Task; return jobs[1]; }
            await fail.Task; throw error;
        };
        var registry = new JobRegistry();
        registry.Register(new JobDefinition<Args>("job"), (_, context) =>
        {
            entered.Writer.TryWrite(context.JobId); return releases[context.JobId == "a" ? 0 : 1].Task;
        });
        await using var server = new JobServer(registry, store, workerCount: fatal ? 3 : 2);
        try
        {
            await server.StartAsync();
            for (var i = 0; i < 2; i++) await reserved.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            if (fatal) { fail.SetResult(); await Until(() => server.LastError is not null); }
            var adapter = new JobServerHostedService(server);
            var stop = adapter.StopAsync(new CancellationToken(true));
            Assert.False(stop.IsCompleted); Assert.False(server.IsAvailable);
            for (var i = 0; i < 2; i++) acknowledgements[i].SetResult();
            for (var i = 0; i < 2; i++) await entered.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            Assert.False(stop.IsCompleted);
            for (var i = 0; i < 2; i++) releases[i].SetResult();
            for (var i = 0; i < 2; i++) await confirming.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            confirmations[0].SetResult(); await Until(() => store.Completions.Count == 1); Assert.False(stop.IsCompleted);
            confirmations[1].SetResult(); await stop.WaitAsync(Timeout);
            Assert.Equal(1, store.Pending); Assert.Equal(new[] { "a", "b" }, store.Reservations.Distinct().Order()); Assert.Equal(2, store.Completions.Count);
            Assert.Same(fatal ? error : null, server.LastError);
        }
        finally
        {
            fail.TrySetResult();
            foreach (var signal in acknowledgements.Concat(releases).Concat(confirmations)) signal.TrySetResult();
        }
    }

    [Theory]
    [InlineData("stop-before")] [InlineData("stop-during")] [InlineData("residual")] [InlineData("missing")]
    public async Task Initialization_never_releases_workers_after_stop_or_failed_checks(string scenario)
    {
        var store = new Store(Candidate("a")) { Initialization = Signal(), Residual = scenario == "residual" };
        if (scenario == "missing") store.References = [new("a", "missing", JobState.Succeeded)];
        var server = new JobServer(new(), store);
        try
        {
            if (scenario == "stop-before")
            {
                await server.StopAsync(); await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
                Assert.Equal(0, store.Checks);
            }
            else
            {
                var starts = Enumerable.Range(0, 8).Select(_ => server.StartAsync()).ToArray();
                await Until(() => store.Checks == 1);
                var stop = scenario == "stop-during" ? server.StopAsync() : null;
                if (stop is not null) Assert.False(stop.IsCompleted);
                store.Initialization.SetResult();
                if (scenario == "stop-during") { await Task.WhenAll(starts); await stop!.WaitAsync(Timeout); }
                else await Assert.ThrowsAsync<InvalidOperationException>(() => Task.WhenAll(starts));
                Assert.Equal(1, store.Checks); Assert.Equal(1, store.Scans);
            }
            Assert.False(server.IsAvailable); Assert.Equal(0, store.Reads); Assert.Empty(store.Reservations);
        }
        finally { store.Initialization.TrySetResult(); await server.DisposeAsync(); await server.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Repeated_start_shares_initialization_result_after_fatal_failure_without_restarting(bool duringInitialization)
    {
        var error = new IOException("read failure");
        var store = new Store { Residual = duringInitialization, Peek = _ => Task.FromException<JobCandidate?>(error) };
        await using var server = new JobServer(new(), store);
        var first = server.StartAsync();
        if (duringInitialization) await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        else { await first; await Until(() => server.LastError is not null); }
        var failure = server.LastError;
        var repeated = server.StartAsync();
        Assert.Same(first, repeated);
        if (duringInitialization) Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => repeated));
        else await repeated;
        await server.StopAsync();
        Assert.Equal(1, store.Checks); Assert.Equal(1, store.Scans); Assert.False(server.IsAvailable);
        Assert.Same(failure, server.LastError);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
    }

    [Fact]
    public async Task Independently_scheduled_workers_can_release_a_synchronously_blocked_handler()
    {
        var store = new Store(Candidate("a"), Candidate("b"));
        using var release = new ManualResetEventSlim(); var entered = Signal(); var registry = new JobRegistry();
        registry.Register(new JobDefinition<Args>("job"), (_, context) =>
        {
            if (context.JobId == "a")
            {
                entered.SetResult();
                if (!release.Wait(Timeout)) throw new TimeoutException("Sibling did not enter.");
            }
            else { entered.Task.GetAwaiter().GetResult(); release.Set(); }
            return Task.CompletedTask;
        });
        await using var server = new JobServer(registry, store, workerCount: 2);
        try
        {
            await server.StartAsync().WaitAsync(Timeout); await Until(() => store.Completions.Count == 2);
            await server.StopAsync(); Assert.All(store.Completions, c => Assert.Null(c.Error));
        }
        finally { release.Set(); }
    }
}
