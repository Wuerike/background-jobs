using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs;

/// <summary>Creates persisted jobs. The caller owns and shares the Redis connection.</summary>
public sealed class RedisJobClient : IJobClient
{
    private readonly JobPreparation preparation;
    private readonly IJobStore store;

    public RedisJobClient(JobContracts contracts, IConnectionMultiplexer connection,
        string prefix = "background-jobs", TimeProvider? timeProvider = null)
        : this(contracts, new RedisJobStore(connection, prefix), timeProvider) { }

    internal RedisJobClient(JobContracts contracts, IJobStore store, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        this.store = store;
        preparation = new JobPreparation(contracts, timeProvider ?? TimeProvider.System);
    }

    public async Task<string> EnqueueAsync<TArgs>(JobDefinition<TArgs> job, TArgs args, JobOptions? options = null)
    {
        var prepared = preparation.Prepare(job, args, options);
        var request = new NewJob(Guid.NewGuid().ToString("N"), prepared);
        await store.CreateAsync(request).ConfigureAwait(false);
        return request.Id;
    }
}
