using System.Text.Json;
using BackgroundJobs.Demo.Events;
using BackgroundJobs.Demo.Jobs;
using BackgroundJobs.Hosting;
using BackgroundJobs.Internal;

namespace BackgroundJobs.UnitTests;

public sealed class ExecutionTests
{
    private sealed record Args(string Message);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Persisted_invocation_reconstructs_validates_and_awaits_typed_handler()
    {
        var registry = new JobRegistry();
        var definition = new JobDefinition<Args>("typed");
        var entered = Signal();
        var release = Signal();
        var original = new Args("Olá 🦊");
        var context = new JobContext("id", 2);
        registry.Register(definition, async (args, actual) =>
        {
            Assert.NotSame(original, args);
            Assert.Equal(original, args);
            Assert.Same(context, actual);
            entered.SetResult();
            await release.Task;
        }, args => Assert.Equal(original.Message, args.Message));
        Assert.False(entered.Task.IsCompleted);
        Assert.True(registry.Handlers.Contains("typed"));
        Assert.False(registry.Handlers.Contains("Typed"));
        var invocation = registry.Handlers.InvokeAsync("typed", JsonSerializer.SerializeToElement(original, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), context);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(invocation.IsCompleted);
        release.SetResult();
        await invocation;
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.Handlers.InvokeAsync("Typed", default, context));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"message\":12}")]
    [InlineData("{\"message\":\"x\",\"unknown\":0}")]
    [InlineData("{\"message\":\"x\",\"message\":\"y\"}")]
    public async Task Invalid_persisted_json_never_enters_handler(string json)
    {
        var registry = new JobRegistry();
        var called = false;
        registry.Register(new JobDefinition<Args>("job"), (_, _) => { called = true; return Task.CompletedTask; });
        using var document = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<JsonException>(() => registry.Handlers.InvokeAsync("job", document.RootElement, new("id", 1)));
        Assert.False(called);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handler_exceptions_keep_identity(bool asynchronous)
    {
        var registry = new JobRegistry();
        var error = new InvalidOperationException("failure");
        registry.Register(new JobDefinition<Args>("job"), (_, _) => asynchronous ? Task.FromException(error) : throw error);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.Handlers.InvokeAsync("job", JsonSerializer.SerializeToElement(new { message = "x" }), new("id", 1))));
    }

    private sealed class Store : IProcessingStore
    {
        public readonly List<string> Calls = [];
        public readonly Queue<JobCandidate> Candidates = [];
        public readonly List<AttemptCompletion> Completions = [];
        public readonly List<JobReference> References = [];
        public bool RecoverySelectionError { get; set; }
        public Exception? PeekError;
        public Exception? ClaimError;
        public Exception? CompleteError;
        public TaskCompletionSource? ClaimRelease;
        public TaskCompletionSource? CompleteRelease;
        public TaskCompletionSource Claimed = Signal();
        public TaskCompletionSource Completing = Signal();
        public TaskCompletionSource Completed = Signal();
        public async IAsyncEnumerable<JobReference> ReadReferencesAsync()
        {
            await Task.CompletedTask;
            foreach (var reference in References) yield return reference;
        }
        public Task<bool> HasProcessingAsync() => Task.FromResult(false);
        public Task<IReadOnlyList<RecoveryCandidate>> ReadRecoveryAsync() => RecoverySelectionError
            ? throw new InvalidOperationException("Recovery selection failed.")
            : Task.FromResult<IReadOnlyList<RecoveryCandidate>>([]);
        public Task<RecoveryResult> TryRecoverAsync(RecoveryCandidate candidate, DateTimeOffset now) => throw new NotSupportedException();
        public Task<ScheduledBatch> ReadScheduledAsync(DateTimeOffset now, long offset) => Task.FromResult(new ScheduledBatch([]));
        public Task<PromotionResult> TryPromoteAsync(ScheduledCandidate candidate, DateTimeOffset now) => throw new NotSupportedException();
        public Task<JobCandidate?> PeekAsync()
        {
            lock (Calls) Calls.Add("peek");
            if (PeekError is not null) throw PeekError;
            return Task.FromResult(Candidates.TryPeek(out var c) ? c : null);
        }
        public async Task<ClaimedJob?> TryClaimAsync(JobCandidate candidate, DateTimeOffset at, string token)
        {
            lock (Calls) Calls.Add("claim");
            Claimed.TrySetResult();
            if (ClaimRelease is not null) await ClaimRelease.Task;
            if (ClaimError is not null) throw ClaimError;
            Candidates.Dequeue();
            return new(candidate.Job, 1, "0", token, at);
        }
        public async Task CompleteAsync(AttemptCompletion result)
        {
            lock (Calls) Calls.Add("complete");
            Completing.TrySetResult();
            if (CompleteRelease is not null) await CompleteRelease.Task;
            if (CompleteError is not null) throw CompleteError;
            Completions.Add(result);
            Completed.TrySetResult();
        }
    }

    private static JobCandidate Candidate(string id = "a", string name = "job", string json = "{\"message\":\"x\"}")
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(-1);
        return new(new JobDetails(id, name, JsonDocument.Parse(json).RootElement, JobState.Enqueued, 0, 1, 0, null, at, at,
            [new(JobState.Enqueued, at, "created")], []), "snapshot", "0", 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_waits_for_admitted_reservation_handler_and_confirmation_without_prefetch(bool stopDuringClaim)
    {
        var store = new Store { ClaimRelease = Signal(), CompleteRelease = Signal() };
        store.Candidates.Enqueue(Candidate());
        store.Candidates.Enqueue(Candidate("b"));
        var entered = Signal(); var release = Signal();
        var registry = new JobRegistry();
        registry.Register(new JobDefinition<Args>("job"), async (_, context) =>
        {
            Assert.Equal(new JobContext("a", 1), context);
            lock (store.Calls) store.Calls.Add("handler");
            entered.SetResult();
            await release.Task;
        });
        await using var server = new JobServer(registry, store, workerCount: 1);
        await Task.WhenAll(server.StartAsync(), server.StartAsync());
        await store.Claimed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task? stop = stopDuringClaim ? server.StopAsync() : null;
        store.ClaimRelease.SetResult();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop ??= server.StopAsync();
        Assert.False(server.IsAvailable);
        Assert.False(stop.IsCompleted);
        lock (store.Calls) Assert.Equal(new[] { "peek", "claim", "handler" }, store.Calls);
        release.SetResult();
        await store.Completing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(stop.IsCompleted);
        lock (store.Calls) Assert.Equal(new[] { "peek", "claim", "handler", "complete" }, store.Calls);
        store.CompleteRelease.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        await server.StopAsync();
        Assert.Single(store.Candidates);
        Assert.Single(store.Completions);
        Assert.Null(server.LastError);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("complete")]
    [InlineData("peek")]
    public async Task Infrastructure_failure_stops_the_worker_without_reinvocation_or_fake_handler_error(string stage)
    {
        var error = new IOException("infrastructure");
        var store = new Store { ClaimError = stage == "claim" ? error : null, CompleteError = stage == "complete" ? error : null,
            PeekError = stage == "peek" ? error : null };
        store.Candidates.Enqueue(Candidate()); store.Candidates.Enqueue(Candidate("b"));
        var registry = new JobRegistry(); var calls = 0;
        registry.Register(new JobDefinition<Args>("job"), (_, _) => { calls++; return Task.CompletedTask; });
        await using var server = new JobServer(registry, store, workerCount: 1);
        await server.StartAsync();
        await WaitUntil(() => server.LastError is not null);
        Assert.Same(error, server.LastError);
        Assert.False(server.IsAvailable);
        Assert.Equal(stage == "complete" ? 1 : 0, calls);
        Assert.Empty(store.Completions);
        lock (store.Calls) Assert.Equal(stage == "complete" ? new[] { "peek", "claim", "complete" } : stage == "claim" ? new[] { "peek", "claim" } : new[] { "peek" }, store.Calls);
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("json")]
    [InlineData("sync")]
    [InlineData("async")]
    public async Task Attempt_errors_are_closed_and_the_next_job_executes(string failure)
    {
        var store = new Store();
        store.Candidates.Enqueue(Candidate(json: failure == "json" ? "{}" : "{\"message\":\"bad\"}"));
        store.Candidates.Enqueue(Candidate("b"));
        var registry = new JobRegistry(); var called = new List<string>(); var second = Signal();
        registry.Register(new JobDefinition<Args>("job"), (args, context) =>
        {
            called.Add(context.JobId);
            if (context.JobId == "a") return failure == "sync" ? throw new Exception("sync") : Task.FromException(new Exception("async"));
            second.SetResult(); return Task.CompletedTask;
        }, args => { if (failure == "validation" && args.Message == "bad") throw new JobValidationException("invalid"); });
        await using var server = new JobServer(registry, store, workerCount: 1);
        await server.StartAsync();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await server.StopAsync();
        Assert.Equal(2, store.Completions.Count);
        Assert.NotNull(store.Completions[0].Error);
        Assert.Null(store.Completions[1].Error);
        Assert.Equal(failure is "json" or "validation" ? new[] { "b" } : new[] { "a", "b" }, called);
        Assert.Null(server.LastError);
    }

    [Fact]
    public async Task Missing_handler_after_start_is_fatal_before_reservation()
    {
        var store = new Store(); store.Candidates.Enqueue(Candidate(name: "missing"));
        await using var server = new JobServer(new JobRegistry(), store, workerCount: 1);
        await server.StartAsync();
        await WaitUntil(() => server.LastError is not null);
        Assert.Contains("missing", server.LastError!.Message);
        Assert.Contains("a", server.LastError.Message);
        lock (store.Calls) Assert.Equal(new[] { "peek" }, store.Calls);
        Assert.False(server.IsAvailable);
    }

    [Fact]
    public async Task Initialization_and_hosted_stop_are_idempotent_and_idle_wait_is_interrupted()
    {
        var store = new Store();
        await using var server = new JobServer(new JobRegistry(), store, workerCount: 1);
        Assert.False(server.IsAvailable);
        var adapter = new JobServerHostedService(server);
        await Task.WhenAll(adapter.StartAsync(default), adapter.StartAsync(default));
        Assert.True(server.IsAvailable);
        await adapter.StopAsync(new CancellationToken(true)).WaitAsync(TimeSpan.FromSeconds(2));
        await adapter.StopAsync(default);
        Assert.False(server.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly DateTimeOffset now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        public TimeSpan? Due { get; private set; }
        private readonly List<(TimerCallback Callback, object? State)> callbacks = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (callbacks) { callbacks.Add((callback, state)); Due = dueTime; }
            return new ManualTimer();
        }
        public void Fire() { lock (callbacks) { foreach (var (callback, state) in callbacks.ToArray()) callback(state); callbacks.Clear(); } }
        private sealed class ManualTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Succeed_after_awaits_the_configured_duration_and_only_then_finishes()
    {
        var clock = new ManualClock(); var events = new DemoEvents(clock);
        var handlers = new DemoJobHandlers(events, clock);
        var wait = handlers.SucceedAfterAsync(new(60000), new("id", 1));
        Assert.False(wait.IsCompleted);
        Assert.Equal(TimeSpan.FromMinutes(1), clock.Due);
        Assert.Equal("started", Assert.Single(events.Snapshot()).Type);
        clock.Fire(); await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "started", "finished" }, events.Snapshot().Select(e => e.Type));
        Assert.All(events.Snapshot(), e => Assert.Equal(clock.GetUtcNow(), e.At));
    }

    [Fact]
    public async Task Server_uses_injected_clock_for_attempt_and_idle_polling()
    {
        var clock = new ManualClock(); var store = new Store();
        store.Candidates.Enqueue(Candidate());
        var registry = new JobRegistry();
        registry.Register(new JobDefinition<Args>("job"), (_, _) => Task.CompletedTask);
        await using var server = new JobServer(registry, store, clock, workerCount: 1);
        await server.StartAsync();
        await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntil(() => clock.Due is not null);
        Assert.Equal(TimeSpan.FromMilliseconds(100), clock.Due);
        await server.StopAsync();
        var result = Assert.Single(store.Completions);
        Assert.Equal(clock.GetUtcNow(), result.Claim.StartedAt);
        Assert.Equal(clock.GetUtcNow(), result.FinishedAt);
    }

    [Theory]
    [InlineData(0)] [InlineData(60000)]
    public void Succeed_contract_accepts_boundaries(int value) => DemoJobValidation.ValidateSucceedAfter(new(value));
    [Theory]
    [InlineData(-1)] [InlineData(60001)]
    public void Succeed_contract_rejects_out_of_range(int value) => Assert.Throws<JobValidationException>(() => DemoJobValidation.ValidateSucceedAfter(new(value)));
    [Theory]
    [InlineData(0)] [InlineData(10)]
    public void Fail_contract_accepts_boundaries(int value) => DemoJobValidation.ValidateFailNTimes(new(value));
    [Theory]
    [InlineData(-1)] [InlineData(11)]
    public void Fail_contract_rejects_out_of_range(int value) => Assert.Throws<JobValidationException>(() => DemoJobValidation.ValidateFailNTimes(new(value)));

    [Fact]
    public async Task Demo_handlers_emit_independent_ordered_events_and_use_only_supplied_attempt()
    {
        var events = new DemoEvents(); var handlers = new DemoJobHandlers(events);
        var context = new JobContext("job", 2);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => handlers.FailNTimesAsync(new(2), context));
        Assert.Equal("demo failure", error.Message);
        await handlers.FailNTimesAsync(new(2), context with { Attempt = 3 });
        await handlers.FailNTimesAsync(new(2), context with { Attempt = 3 });
        await handlers.SucceedAfterAsync(new(0), context);
        await handlers.LogMessageAsync(new(" Olá 🦊 "), context);
        var snapshot = events.Snapshot();
        Assert.Equal(new[] { "started", "finished", "started", "finished", "started", "finished", "started", "finished", "started", "message", "finished" }, snapshot.Select(e => e.Type));
        Assert.All(snapshot, e => { Assert.Equal("job", e.JobId); Assert.Equal(TimeSpan.Zero, e.At.Offset); });
        Assert.Equal("error", JsonSerializer.SerializeToElement(snapshot[1].Detail).GetProperty("outcome").GetString());
        Assert.Equal("ok", JsonSerializer.SerializeToElement(snapshot[3].Detail).GetProperty("outcome").GetString());
        Assert.Equal(" Olá 🦊 ", JsonSerializer.SerializeToElement(snapshot[9].Detail).GetProperty("message").GetString());
        events.Add("started", context);
        Assert.Equal(11, snapshot.Count);
    }
}
