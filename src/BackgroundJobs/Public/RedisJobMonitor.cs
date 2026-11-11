using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs;

/// <summary>Read-only bounded monitoring. The caller owns the Redis connection.</summary>
public sealed class RedisJobMonitor
{
    private readonly IDatabase database;
    private readonly string prefix;
    private readonly TimeProvider clock;
    private const string Projection = """
        local fields = redis.call('HMGET', KEYS[1], 'id', 'name', 'state', 'attempts',
            'maxAttempts', 'retryDelayMs', 'createdAt', 'updatedAt', 'scheduledAt')
        return {fields, redis.call('LINDEX', KEYS[2], -1), redis.call('LINDEX', KEYS[3], -1)}
        """;

    public RedisJobMonitor(IConnectionMultiplexer connection, string prefix = "background-jobs", TimeProvider? timeProvider = null)
    {
        // Apply the same prefix validation as the manager, without starting an executor.
        _ = new RedisJobManager(connection, prefix);
        database = connection.GetDatabase();
        this.prefix = prefix;
        clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<JobObservation> ReadAsync(int maxJobs = 10_000, CancellationToken cancellationToken = default)
    {
        if (maxJobs is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maxJobs));
        cancellationToken.ThrowIfCancellationRequested();
        var started = clock.GetUtcNow();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<JobMonitorItem>();
        var batch = new List<string>(128);
        await foreach (var id in database.SetScanAsync($"{prefix}:jobs", pageSize: 128).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ids.Add(id.ToString())) continue;
            if (ids.Count > maxJobs) throw new InvalidOperationException($"Monitoring limit of {maxJobs} jobs exceeded. No partial observation is available.");
            batch.Add(id.ToString());
            if (batch.Count == 128) { await ReadBatch(); batch.Clear(); }
        }
        if (batch.Count > 0) await ReadBatch();
        return new(started, clock.GetUtcNow(), items);

        async Task ReadBatch()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tasks = batch.Select(id => database.ScriptEvaluateAsync(Projection,
                [$"{prefix}:job:{id}", $"{prefix}:job:{id}:attempts", $"{prefix}:job:{id}:history"])).ToArray();
            var rows = await Task.WhenAll(tasks).ConfigureAwait(false);
            for (var i = 0; i < rows.Length; i++) items.Add(JobCodec.ReadMonitorItem(batch[i], (RedisResult[])rows[i]!));
        }
    }
}
