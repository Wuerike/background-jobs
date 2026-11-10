using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs;

/// <summary>A single-worker executor. The caller owns the connection and configures the registry before starting.</summary>
public sealed class JobServer : IAsyncDisposable
{
    private readonly IProcessingStore store;
    private readonly JobHandlers handlers;
    private readonly TimeProvider clock;
    private readonly object admission = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? runner;
    private bool stopRequested;
    private bool available;
    private bool disposed;
    private Exception? lastError;

    public JobServer(JobRegistry registry, IConnectionMultiplexer connection,
        string prefix = "background-jobs", TimeProvider? timeProvider = null)
        : this(registry, new RedisJobStore(connection, prefix), timeProvider) { }

    internal JobServer(JobRegistry registry, IProcessingStore store, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(store);
        handlers = registry.Handlers;
        this.store = store;
        clock = timeProvider ?? TimeProvider.System;
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
            lock (admission) available = !stopRequested;
            initialized.TrySetResult();
            while (true)
            {
                lock (admission) { if (stopRequested) break; }
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
                    if (stopRequested) break;
                    if (!handlers.Contains(candidate.Job.Name))
                        throw new InvalidOperationException($"Handler ausente: '{candidate.Job.Name}' (job '{candidate.Job.Id}').");
                    // Calling the async operation under this lock admits even a reservation
                    // that has not yet received its Redis acknowledgement when StopAsync runs.
                    reservation = store.TryClaimAsync(candidate, clock.GetUtcNow(), Guid.NewGuid().ToString("N"));
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
            lock (admission) { lastError = failure; available = false; }
            initialized.TrySetException(failure);
        }
        finally { lock (admission) available = false; }
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
