using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs;

/// <summary>A fixed-size executor. The caller owns the connection and configures the registry before starting.</summary>
public sealed class JobServer : IAsyncDisposable
{
    private readonly IProcessingStore store;
    private readonly JobHandlers handlers;
    private readonly TimeProvider clock;
    private readonly int workerCount;
    private readonly object admission = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? runner;
    private bool stopRequested;
    private bool suspended;
    private bool startupComplete;
    private int inFlight;
    private Task? supervision;
    private TaskCompletionSource resumed = Signal();
    private TaskCompletionSource round = Signal();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool available;
    private bool disposed;
    private Exception? lastError;

    public JobServer(JobRegistry registry, IConnectionMultiplexer connection,
        string prefix = "background-jobs", TimeProvider? timeProvider = null, int workerCount = 4)
        : this(registry, CreateStore(connection, prefix, workerCount), timeProvider, workerCount) { }

    internal JobServer(JobRegistry registry, IProcessingStore store, TimeProvider? timeProvider = null, int workerCount = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(store);
        handlers = registry.Handlers;
        this.store = store;
        clock = timeProvider ?? TimeProvider.System;
        this.workerCount = workerCount;
        if (store is RedisJobStore redis) redis.TransportFailure += Suspend;
    }

    private static RedisJobStore CreateStore(IConnectionMultiplexer connection, string prefix, int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        return new RedisJobStore(connection, prefix);
    }

    public bool IsAvailable { get { lock (admission) return available && runner is { IsCompleted: false }; } }
    public Exception? LastError { get { lock (admission) return lastError; } }

    /// <summary>Starts once; repeated calls share initialization. A stopped instance is not restarted.</summary>
    public Task StartAsync()
    {
        lock (admission)
        {
            if (stopRequested) throw new InvalidOperationException("This job server has stopped.");
            runner ??= Task.Run(RunAsync);
            return initialized.Task;
        }
    }

    /// <summary>Stops admitting reservations and waits for admitted work and its persistence, without cancelling handlers.</summary>
    public async Task StopAsync()
    {
        Task? task;
        lock (admission)
        {
            available = false;
            if (!stopRequested)
            {
                stopRequested = true;
                stopping.Cancel();
            }
            task = runner;
        }
        if (task is not null) await task.ConfigureAwait(false);
        Task? supervisor;
        lock (admission) supervisor = supervision;
        if (supervisor is not null) await supervisor.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        try
        {
            await AccessAsync(ValidateReferencesAsync).ConfigureAwait(false);
            if (!await WaitForAdmissionAsync().ConfigureAwait(false)) { initialized.TrySetResult(); return; }
            await AccessAsync(async () => { await store.ValidateProcessingIndexAsync().ConfigureAwait(false); return true; }).ConfigureAwait(false);
            Task<bool> session;
            lock (admission)
            {
                if (stopping.IsCancellationRequested) { initialized.TrySetResult(); return; }
                session = AccessAsync(async () => { await store.BeginSessionAsync().ConfigureAwait(false); return true; });
            }
            await session.ConfigureAwait(false);
            await RecoverAsync().ConfigureAwait(false);
            await PromoteBatchAsync(0).ConfigureAwait(false);
            Task[] workers;
            lock (admission)
            {
                workers = stopping.IsCancellationRequested ? [] : Enumerable.Range(0, workerCount).Select(_ => Task.Run(WorkAsync)).Append(Task.Run(ScheduleAsync)).ToArray();
                startupComplete = true;
                available = !stopping.IsCancellationRequested && !suspended;
                initialized.TrySetResult();
            }
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            initialized.TrySetResult();
        }
        catch (Exception failure) when (stopping.IsCancellationRequested && RedisTransport.IsTransient(failure))
        {
            initialized.TrySetResult();
        }
        catch (Exception failure)
        {
            Fail(failure);
            initialized.TrySetException(failure);
        }
        finally { lock (admission) available = false; }
    }

    private async Task<bool> ValidateReferencesAsync()
    {
        var missing = new List<string>();
        await store.HasProcessingAsync().ConfigureAwait(false);
        await foreach (var reference in store.ReadReferencesAsync().ConfigureAwait(false))
            if (!handlers.Contains(reference.Name)) missing.Add($"'{reference.Name}' (job '{reference.Id}')");
        if (missing.Count != 0) throw new InvalidOperationException("Handlers ausentes: " + string.Join(", ", missing));
        return true;
    }

    private async Task<bool> WaitForAdmissionAsync()
    {
        while (true)
        {
            Task wait;
            lock (admission)
            {
                if (stopping.IsCancellationRequested) return false;
                if (!suspended) return true;
                wait = resumed.Task;
            }
            try { await wait.WaitAsync(stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return false; }
        }
    }

    // The delegate retains its envelope (candidate/token/time/result) throughout
    // reconciliation. Only storage access is retried, never the user's function.
    private async Task<T> AccessAsync<T>(Func<Task<T>> operation)
    {
        lock (admission) inFlight++;
        try
        {
            while (true)
            {
                try { return await operation().ConfigureAwait(false); }
                catch (Exception error) when (RedisTransport.IsTransient(error))
                {
                    Task wait;
                    Suspend(error);
                    lock (admission)
                    {
                        if (stopping.IsCancellationRequested) throw;
                        wait = round.Task;
                    }
                    try { await wait.WaitAsync(stopping.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                    {
                        // One final bounded confirmation round; no idle reconnection loop survives stop.
                        return await operation().ConfigureAwait(false);
                    }
                }
            }
        }
        finally { lock (admission) inFlight--; }
    }

    private void Suspend(Exception error)
    {
        lock (admission)
        {
            lastError ??= error;
            available = false;
            if (stopping.IsCancellationRequested || suspended) return;
            suspended = true;
            resumed = Signal();
            supervision = Task.Run(SuperviseAsync);
        }
    }

    private async Task SuperviseAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), clock, stopping.Token).ConfigureAwait(false);
                bool validate;
                lock (admission)
                {
                    if (stopping.IsCancellationRequested) return;
                    validate = inFlight == 0;
                    if (!validate)
                    {
                        var pulse = round;
                        round = Signal();
                        pulse.TrySetResult();
                    }
                }
                if (!validate) continue;
                try
                {
                    await store.ValidateSessionAsync().ConfigureAwait(false);
                    await ValidateReferencesAsync().ConfigureAwait(false);
                    await store.ValidateProcessingIndexAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (RedisTransport.IsTransient(error)) { continue; }
                lock (admission)
                {
                    if (stopping.IsCancellationRequested) return;
                    if (inFlight != 0) continue;
                    suspended = false;
                    lastError = null;
                    available = startupComplete;
                    resumed.TrySetResult();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception failure)
    {
        lock (admission)
        {
            lastError ??= failure;
            available = false;
            stopping.Cancel();
        }
    }

    private async Task RecoverAsync()
    {
        while (await WaitForAdmissionAsync().ConfigureAwait(false))
        {
            Task<IReadOnlyList<RecoveryCandidate>> selection;
            lock (admission)
            {
                if (stopping.IsCancellationRequested) return;
                selection = AccessAsync(store.ReadRecoveryAsync);
            }
            var candidates = await selection.ConfigureAwait(false);
            if (candidates.Count == 0) return;
            foreach (var candidate in candidates)
            {
                if (!await WaitForAdmissionAsync().ConfigureAwait(false)) return;
                Task<RecoveryResult> recovery;
                lock (admission)
                {
                    if (stopping.IsCancellationRequested) return;
                    var now = clock.GetUtcNow();
                    var operation = Guid.NewGuid().ToString("N");
                    recovery = AccessAsync(() => store.TryRecoverAsync(candidate, now, operation));
                }
                await recovery.ConfigureAwait(false);
            }
        }
    }

    private async Task<(long Offset, bool Continue)> PromoteBatchAsync(long offset)
    {
        if (!await WaitForAdmissionAsync().ConfigureAwait(false)) return (0, false);
        var now = clock.GetUtcNow();
        var candidates = await AccessAsync(() => store.ReadScheduledAsync(now, offset)).ConfigureAwait(false);
        var removed = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Job.ScheduledAt > now) continue;
            if (!await WaitForAdmissionAsync().ConfigureAwait(false)) return (0, false);
            Task<PromotionResult> promotion;
            lock (admission)
            {
                if (stopping.IsCancellationRequested) return (0, false);
                if (suspended) continue;
                var operation = Guid.NewGuid().ToString("N");
                try { promotion = AccessAsync(() => store.TryPromoteAsync(candidate, now, operation)); }
                catch (Exception failure) { Fail(failure); throw; }
            }
            await promotion.ConfigureAwait(false);
            removed++;
        }
        return (candidates.ExaminedCount != candidates.Count ? 0 : offset + candidates.Count - removed,
            candidates.ExaminedCount == RedisJobStore.ScheduledBatchSize || removed > 0);
    }

    private async Task ScheduleAsync()
    {
        try
        {
            long offset = 0;
            while (true)
            {
                if (!await WaitForAdmissionAsync().ConfigureAwait(false)) break;
                var batch = await PromoteBatchAsync(offset).ConfigureAwait(false);
                if (batch.Continue) { offset = batch.Offset; continue; }
                offset = 0;
                try { await Task.Delay(TimeSpan.FromMilliseconds(100), clock, stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            }
        }
        catch (Exception failure) { Fail(failure); }
    }

    private async Task WorkAsync()
    {
        try
        {
            while (true)
            {
                if (!await WaitForAdmissionAsync().ConfigureAwait(false)) break;
                var candidate = await AccessAsync(store.PeekAsync).ConfigureAwait(false);
                if (candidate is null)
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(100), clock, stopping.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
                    continue;
                }
                Task<ClaimedJob?> reservation;
                lock (admission)
                {
                    if (stopping.IsCancellationRequested) break;
                    if (suspended) continue;
                    try
                    {
                        if (!handlers.Contains(candidate.Job.Name))
                            throw new InvalidOperationException($"Handler ausente: '{candidate.Job.Name}' (job '{candidate.Job.Id}').");
                        // Calling the async operation under this lock admits even a reservation
                        // that has not yet received its Redis acknowledgement when StopAsync runs.
                        var start = clock.GetUtcNow();
                        var token = Guid.NewGuid().ToString("N");
                        reservation = AccessAsync(() => store.TryClaimAsync(candidate, start, token));
                    }
                    catch (Exception failure)
                    {
                        Fail(failure);
                        return;
                    }
                }
                var claim = await reservation.ConfigureAwait(false);
                if (claim is null) continue;
                string? error = null;
                try
                {
                    await handlers.InvokeAsync(claim.Job.Name, claim.Job.Args,
                        new JobContext(claim.Job.Id, claim.Attempt)).ConfigureAwait(false);
                }
                catch (Exception failure) { error = failure.Message; }
                var completion = new AttemptCompletion(claim, clock.GetUtcNow(), error);
                await AccessAsync(async () => { await store.CompleteAsync(completion).ConfigureAwait(false); return true; }).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            // Close admission as soon as this worker observes the failure. The coordinator
            // still drains siblings, including reservations awaiting acknowledgement.
            Fail(failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (admission)
        {
            if (disposed) return;
            disposed = true;
            stopping.Dispose();
        }
    }
}
