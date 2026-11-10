using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs;

/// <summary>Reads independent snapshots without a contract registry. The caller owns the connection.</summary>
public sealed class RedisJobManager : IJobManager
{
    private readonly IJobStore store;

    public RedisJobManager(IConnectionMultiplexer connection, string prefix = "background-jobs")
        : this(new RedisJobStore(connection, prefix)) { }

    internal RedisJobManager(IJobStore store) => this.store = store;

    public Task<JobDetails?> GetAsync(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return store.GetAsync(jobId);
    }
}
