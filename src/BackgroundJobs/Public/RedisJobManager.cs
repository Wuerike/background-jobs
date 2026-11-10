using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs;

/// <summary>Manages persisted jobs without a contract registry. The caller owns the connection.</summary>
public sealed class RedisJobManager : IJobManager
{
    private readonly IJobStore store;
    private readonly TimeProvider clock;

    public RedisJobManager(IConnectionMultiplexer connection, string prefix = "background-jobs", TimeProvider? timeProvider = null)
        : this(new RedisJobStore(connection, prefix), timeProvider) { }

    internal RedisJobManager(IJobStore store, TimeProvider? timeProvider = null)
    {
        this.store = store;
        clock = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<JobSummary>> ListAsync() => store.ListAsync();

    public Task<JobOperationResult> DeleteAsync(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return store.DeleteAsync(jobId, clock);
    }

    public Task<JobOperationResult> RequeueAsync(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return store.RequeueAsync(jobId, clock);
    }

    public Task<JobDetails?> GetAsync(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return store.GetAsync(jobId);
    }
}
