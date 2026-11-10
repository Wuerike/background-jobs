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
    }

    private async Task RunAsync()
    {
        try
        {
            var missing = new List<string>();
            var residual = await store.HasProcessingAsync().ConfigureAwait(false);
            await foreach (var reference in store.ReadReferencesAsync().ConfigureAwait(false))
            {
                if (!handlers.Contains(reference.Name)) missing.Add($"'{reference.Name}' (job '{reference.Id}')");
                residual |= reference.State == JobState.Processing;
            }
            if (missing.Count > 0) throw new InvalidOperationException("Handlers ausentes: " + string.Join(", ", missing));
            if (residual) throw new InvalidOperationException("Há jobs em processamento que exigem recuperação; o servidor não pode iniciar.");
            await PromoteBatchAsync(0).ConfigureAwait(false);
            Task[] workers;
            lock (admission)
            {
                workers = stopping.IsCancellationRequested ? [] : Enumerable.Range(0, workerCount).Select(_ => Task.Run(WorkAsync)).Append(Task.Run(ScheduleAsync)).ToArray();
                available = !stopping.IsCancellationRequested;
                initialized.TrySetResult();
            }
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            Fail(failure);
            initialized.TrySetException(failure);
        }
        finally { lock (admission) available = false; }
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

    private async Task<(long Offset, bool Continue)> PromoteBatchAsync(long offset)
    {
        lock (admission) { if (stopping.IsCancellationRequested) return (0, false); }
        var now = clock.GetUtcNow();
        var candidates = await store.ReadScheduledAsync(now, offset).ConfigureAwait(false);
        var removed = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Job.ScheduledAt > now) continue;
            Task<PromotionResult> promotion;
            lock (admission)
            {
                if (stopping.IsCancellationRequested) return (0, false);
                try { promotion = store.TryPromoteAsync(candidate, now); }
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
                lock (admission) { if (stopping.IsCancellationRequested) break; }
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
                lock (admission) { if (stopping.IsCancellationRequested) break; }
                var candidate = await store.PeekAsync().ConfigureAwait(false);
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
                    try
                    {
                        if (!handlers.Contains(candidate.Job.Name))
                            throw new InvalidOperationException($"Handler ausente: '{candidate.Job.Name}' (job '{candidate.Job.Id}').");
                        // Calling the async operation under this lock admits even a reservation
                        // that has not yet received its Redis acknowledgement when StopAsync runs.
                        reservation = store.TryClaimAsync(candidate, clock.GetUtcNow(), Guid.NewGuid().ToString("N"));
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
                // No Redis operation is inside the handler catch. An uncertain write is fatal;
                // never run the function again or fetch another candidate on this instance.
                await store.CompleteAsync(new AttemptCompletion(claim, clock.GetUtcNow(), error)).ConfigureAwait(false);
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
