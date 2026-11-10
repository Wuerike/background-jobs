using System.Globalization;
using System.Text.Json;
using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;
using StackExchange.Redis;

namespace BackgroundJobs.IntegrationTests;

public sealed class RedisTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    public sealed class Payload { public string Message { get; set; } = ""; public List<string> Values { get; set; } = []; }
    public sealed class CyclicArgs { public CyclicArgs? Next { get; set; } }
    private readonly string prefix = "redis-store-" + Guid.NewGuid().ToString("N");
    private readonly JobDefinition<Payload> definition = new("message.v1");
    private readonly JobContracts contracts = new();
    private ConnectionMultiplexer connection = null!;
    private IDatabase db = null!;
    private RedisJobStore store = null!;
    private IJobClient client = null!;
    private IJobManager manager = null!;
    private string Configuration => Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "127.0.0.1:6379";

    public async Task InitializeAsync()
    {
        connection = await ConnectionMultiplexer.ConnectAsync(Configuration);
        db = connection.GetDatabase();
        store = new RedisJobStore(connection, prefix);
        contracts.Add(definition);
        client = new RedisJobClient(contracts, connection, prefix, new Clock());
        manager = new RedisJobManager(connection, prefix);
    }

    private async Task<RedisKey[]> AllKeys(string? scope = null) =>
        await Task.Run(() => connection.GetServer(connection.GetEndPoints()[0]).Keys(db.Database, (scope ?? prefix) + ":*").ToArray());

    public async Task DisposeAsync()
    {
        if (connection is null) return;
        try
        {
            var keys = await AllKeys();
            if (keys.Length > 0) await db.KeyDeleteAsync(keys);
        }
        finally { await connection.DisposeAsync(); }
    }

    private NewJob Request(string id, JobOptions? options = null, string message = "Olá 🦊\n\"teste\"") =>
        new(id, new JobPreparation(contracts, new Clock()).Prepare(definition,
            new Payload { Message = message, Values = ["á", "漢字"] }, options));

    private async Task<JobDetails> Detail(string id) => Assert.IsType<JobDetails>(await manager.GetAsync(id));

    [Theory]
    [InlineData(3, 1000)]
    [InlineData(1, 0)]
    [InlineData(10, 60000)]
    public async Task Public_creation_and_detail_match_the_contract(int maxAttempts, int delay)
    {
        var args = new Payload { Message = "Olá 🦊\n\"", Values = ["a", "b"] };
        var id = maxAttempts == 3 ? await client.EnqueueAsync(definition, args) :
            await client.EnqueueAsync(definition, args, new JobOptions { MaxAttempts = maxAttempts, RetryDelayMs = delay });
        args.Message = "changed";
        args.Values.Clear();
        var detail = await Detail(id);
        Assert.Equal(id, detail.Id);
        Assert.Equal(definition.Name, detail.Name);
        Assert.Equal("Olá 🦊\n\"", detail.Args.GetProperty("message").GetString());
        Assert.Equal(2, detail.Args.GetProperty("values").GetArrayLength());
        Assert.Equal(JobState.Enqueued, detail.State);
        Assert.Equal(0, detail.Attempts);
        Assert.Equal(maxAttempts, detail.MaxAttempts);
        Assert.Equal(delay, detail.RetryDelayMs);
        Assert.Null(detail.ScheduledAt);
        Assert.Equal(Now, detail.CreatedAt);
        Assert.Equal(Now, detail.UpdatedAt);
        var change = Assert.Single(detail.History);
        Assert.Equal(detail.State, change.State);
        Assert.Equal(Now, change.At);
        Assert.NotEmpty(change.Reason);
        Assert.Empty(detail.AttemptHistory);
        Assert.True(await db.SetContainsAsync($"{prefix}:jobs", id));
        Assert.NotNull(await db.SortedSetScoreAsync($"{prefix}:ready", id));
        Assert.Null(await db.SortedSetScoreAsync($"{prefix}:scheduled", id));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9999)]
    [InlineData(10001)]
    [InlineData(long.MaxValue)]
    public async Task Exact_dates_and_non_anticipating_scores(long ticks)
    {
        var at = ticks == long.MaxValue ? DateTimeOffset.MaxValue : Now.AddTicks(ticks).ToOffset(TimeSpan.FromHours(-3));
        var id = await client.EnqueueAsync(definition, new Payload(), new JobOptions { RunAt = at });
        var detail = await Detail(id);
        Assert.Equal(ticks > 0 ? JobState.Scheduled : JobState.Enqueued, detail.State);
        if (ticks > 0)
        {
            Assert.Equal(at.UtcTicks, detail.ScheduledAt!.Value.UtcTicks);
            Assert.Equal(TimeSpan.Zero, detail.ScheduledAt.Value.Offset);
            Assert.Equal((double)JobCodec.ScheduledScore(at), await db.SortedSetScoreAsync($"{prefix}:scheduled", id));
            Assert.Null(await db.SortedSetScoreAsync($"{prefix}:ready", id));
            Assert.False(await db.KeyExistsAsync($"{prefix}:sequence"));
        }
        else Assert.Null(await db.SortedSetScoreAsync($"{prefix}:scheduled", id));
    }

    [Fact]
    public async Task Invalid_preparation_leaves_no_keys()
    {
        await Assert.ThrowsAsync<JobValidationException>(() => client.EnqueueAsync(definition, new Payload(), new JobOptions { RetryDelayMs = -1 }));
        await Assert.ThrowsAsync<JobValidationException>(() => client.EnqueueAsync(definition, new Payload { Message = "\ud800" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnqueueAsync(new JobDefinition<int>("unknown"), 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnqueueAsync(new JobDefinition<int>(definition.Name), 1));
        var cyclicDefinition = new JobDefinition<CyclicArgs>("cycle");
        contracts.Add(cyclicDefinition);
        var cycle = new CyclicArgs();
        cycle.Next = cycle;
        await Assert.ThrowsAsync<JsonException>(() => client.EnqueueAsync(cyclicDefinition, cycle));
        foreach (var error in new Exception[] { new JobValidationException("reject"), new ArgumentException("bug"), new JsonException("bug"), new NullReferenceException("bug") })
        {
            var localContracts = new JobContracts();
            localContracts.Add(definition, _ => throw error);
            var localClient = new RedisJobClient(localContracts, connection, prefix);
            Assert.Same(error, await Assert.ThrowsAsync(error.GetType(), () => localClient.EnqueueAsync(definition, new Payload())));
        }
        Assert.Empty(await AllKeys());
    }

    [Fact]
    public async Task New_connection_reads_without_contracts_and_args_survive_disposal()
    {
        var id = await client.EnqueueAsync(definition, new Payload { Message = "persisted" });
        JobDetails detail;
        await using (var other = await ConnectionMultiplexer.ConnectAsync(Configuration))
        {
            IJobManager reader = new RedisJobManager(other, prefix);
            detail = Assert.IsType<JobDetails>(await reader.GetAsync(id));
            Assert.Null(await reader.GetAsync(" opaque ID / with spaces "));
            Assert.Null(await reader.GetAsync(""));
        }
        Assert.Equal("persisted", detail.Args.GetProperty("message").GetString());
        Assert.Single(detail.History);
        Assert.Empty(detail.AttemptHistory);
        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.GetAsync(null!));
    }

    [Fact]
    public async Task Fifo_sequence_ignores_scheduled_jobs_and_no_keys_expire()
    {
        var a = await client.EnqueueAsync(definition, new Payload());
        var s = await client.EnqueueAsync(definition, new Payload(), new JobOptions { RunAt = Now.AddHours(1) });
        var b = await client.EnqueueAsync(definition, new Payload());
        var c = await client.EnqueueAsync(definition, new Payload());
        Assert.Equal(new RedisValue[] { a, b, c }, await db.SortedSetRangeByRankAsync($"{prefix}:ready"));
        Assert.Equal(1, await db.SortedSetScoreAsync($"{prefix}:ready", a));
        Assert.Equal(2, await db.SortedSetScoreAsync($"{prefix}:ready", b));
        Assert.Equal(3, await db.SortedSetScoreAsync($"{prefix}:ready", c));
        Assert.Null(await db.SortedSetScoreAsync($"{prefix}:ready", s));
        foreach (var key in await AllKeys()) Assert.Null(await db.KeyTimeToLiveAsync(key));
    }

    [Fact]
    public async Task Last_exact_fifo_sequence_is_preserved_without_rounding()
    {
        await db.StringSetAsync($"{prefix}:sequence", "9007199254740990");
        var id = await client.EnqueueAsync(definition, new Payload());
        Assert.Equal(9007199254740991d, await db.SortedSetScoreAsync($"{prefix}:ready", id));
        Assert.Equal("9007199254740991", (string?)await db.StringGetAsync($"{prefix}:sequence"));
        var before = await Snapshot();
        await Assert.ThrowsAsync<RedisServerException>(() => client.EnqueueAsync(definition, new Payload()));
        Assert.Equal(before, await Snapshot());
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("[1,\"á\",true]")]
    public async Task Structured_json_is_preserved_for_non_object_arguments(string json)
    {
        var jsonDefinition = new JobDefinition<JsonElement>("json");
        contracts.Add(jsonDefinition);
        string id;
        using (var document = JsonDocument.Parse(json))
            id = await client.EnqueueAsync(jsonDefinition, document.RootElement);
        var detail = await Detail(id);
        using var expected = JsonDocument.Parse(json);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, detail.Args));
    }

    [Fact]
    public async Task Opaque_ids_are_not_normalized()
    {
        const string id = " opaque / ID with spaces ";
        await store.CreateAsync(Request(id));
        Assert.Equal(id, (await Detail(id)).Id);
        Assert.Null(await manager.GetAsync(id.Trim()));
    }

    [Fact]
    public async Task Parallel_creations_have_exclusive_indexes_and_complete_snapshots()
    {
        var ids = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => client.EnqueueAsync(definition,
            new Payload { Message = i.ToString() }, new JobOptions { RunAt = i % 2 == 0 ? Now.AddHours(1) : null })));
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(ids.Length, await db.SetLengthAsync($"{prefix}:jobs"));
        var scores = new HashSet<double>();
        foreach (var id in ids)
        {
            var detail = await Detail(id);
            Assert.Single(detail.History);
            Assert.Empty(detail.AttemptHistory);
            var ready = await db.SortedSetScoreAsync($"{prefix}:ready", id);
            var scheduled = await db.SortedSetScoreAsync($"{prefix}:scheduled", id);
            Assert.True(ready.HasValue ^ scheduled.HasValue);
            if (ready.HasValue) Assert.True(scores.Add(ready.Value));
        }
    }

    [Fact]
    public async Task Concurrent_creation_reads_are_absent_or_complete()
    {
        await using var other = await ConnectionMultiplexer.ConnectAsync(Configuration);
        var writer = new RedisJobStore(other, prefix);
        var requests = Enumerable.Range(0, 30).Select(i => Request("opaque-" + i)).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = Task.Run(async () => { await start.Task; await Task.WhenAll(requests.Select(writer.CreateAsync)); });
        var reading = Task.Run(async () =>
        {
            await start.Task;
            for (var round = 0; round < 4; round++)
                foreach (var request in requests)
                {
                    var detail = await manager.GetAsync(request.Id);
                    if (detail is not null) { Assert.Single(detail.History); Assert.Empty(detail.AttemptHistory); Assert.Equal(JobState.Enqueued, detail.State); }
                }
        });
        start.SetResult();
        await Task.WhenAll(writing, reading);
        foreach (var request in requests) Assert.NotNull(await manager.GetAsync(request.Id));
    }

    [Fact]
    public async Task Equivalent_retransmissions_are_no_ops_even_after_state_changes()
    {
        var request = Request("opaque-repeated");
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.CreateAsync(request)));
        var keys = store.Keys(request.Id);
        Assert.Equal(1, await db.ListLengthAsync(keys[1]));
        Assert.Equal("1", (string?)await db.StringGetAsync(keys[6]));
        await db.HashSetAsync(keys[0], "state", "succeeded");
        await store.CreateAsync(request);
        Assert.Equal("succeeded", (string?)await db.HashGetAsync(keys[0], "state"));
        Assert.Equal(1, await db.ListLengthAsync(keys[1]));
        var before = await Snapshot();
        await Assert.ThrowsAsync<RedisServerException>(() => store.CreateAsync(Request(request.Id, message: "different")));
        Assert.Equal(before, await Snapshot());
    }

    // DUMP preserves exact values/types; compare all keys to detect partial writes.
    private async Task<string[]> Snapshot()
    {
        var keys = (await AllKeys()).OrderBy(k => (string?)k, StringComparer.Ordinal).ToArray();
        var entries = new List<string>();
        foreach (var key in keys) entries.Add($"{key}={Convert.ToBase64String((byte[])(await db.ExecuteAsync("DUMP", key))!)}");
        return entries.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Wrong_key_types_are_rejected_before_any_write(int position)
    {
        var request = Request("bad-types");
        var key = store.Keys(request.Id)[position];
        if (position == 6) await db.ListRightPushAsync(key, "wrong");
        else await db.StringSetAsync(key, "wrong");
        var before = await Snapshot();
        await Assert.ThrowsAsync<RedisServerException>(() => store.CreateAsync(request));
        Assert.Equal(before, await Snapshot());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("01")]
    [InlineData("9007199254740991")]
    [InlineData("9007199254740992")]
    [InlineData("9223372036854775808")]
    public async Task Invalid_or_exhausted_sequence_leaves_no_partial_write(string value)
    {
        await db.StringSetAsync($"{prefix}:sequence", value);
        var before = await Snapshot();
        await Assert.ThrowsAsync<RedisServerException>(() => store.CreateAsync(Request("sequence")));
        Assert.Equal(before, await Snapshot());
    }

    [Theory]
    [InlineData("name", null)]
    [InlineData("args", "{broken")]
    [InlineData("state", "unknown")]
    [InlineData("createdAt", "not-a-date")]
    [InlineData("attempts", "NaN")]
    [InlineData("maxAttempts", "0")]
    [InlineData("retryDelayMs", "-1")]
    [InlineData("scheduledAt", "")]
    [InlineData("id", "different")]
    public async Task Corrupt_hash_fails_instead_of_returning_partial_data(string field, string? value)
    {
        var id = await client.EnqueueAsync(definition, new Payload());
        if (value is null) await db.HashDeleteAsync(store.Keys(id)[0], field);
        else await db.HashSetAsync(store.Keys(id)[0], field, value);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => manager.GetAsync(id));
        Assert.Contains(field, error.Message);
    }

    [Fact]
    public async Task Missing_history_and_orphans_are_explicit_errors()
    {
        var request = Request("orphan");
        var keys = store.Keys(request.Id);
        await db.ListRightPushAsync(keys[1], "{}");
        await Assert.ThrowsAsync<RedisServerException>(() => manager.GetAsync(request.Id));
        var before = await Snapshot();
        await Assert.ThrowsAsync<RedisServerException>(() => store.CreateAsync(request));
        Assert.Equal(before, await Snapshot());
        await db.KeyDeleteAsync(keys[1]);
        await store.CreateAsync(request);
        await db.KeyDeleteAsync(keys[1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.GetAsync(request.Id));
        await db.ListRightPushAsync(keys[1], "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.GetAsync(request.Id));
    }

    [Theory]
    [InlineData("scheduled")]
    [InlineData("enqueued")]
    [InlineData("processing")]
    [InlineData("succeeded")]
    [InlineData("failed")]
    [InlineData("deleted")]
    public async Task Reader_preserves_all_states_outcomes_order_and_repeated_attempt_numbers(string state)
    {
        var id = await client.EnqueueAsync(definition, new Payload());
        await WriteSnapshot(store.Keys(id), state, 1);
        var detail = await Detail(id);
        Assert.Equal(state, JobCodec.StateText(detail.State));
        Assert.Equal(new[] { JobState.Enqueued, detail.State }, detail.History.Select(h => h.State));
        Assert.Equal(new[] { 1, 2, 3, 1 }, detail.AttemptHistory.Select(a => a.Attempt));
        Assert.Equal(new JobAttemptOutcome?[] { JobAttemptOutcome.Failed, JobAttemptOutcome.Interrupted, JobAttemptOutcome.Succeeded, null }, detail.AttemptHistory.Select(a => a.Outcome));
        Assert.Equal("demo failure", detail.AttemptHistory[0].Error);
        Assert.Null(detail.AttemptHistory[3].FinishedAt);
    }

    private async Task WriteSnapshot(RedisKey[] keys, string state, int attempts)
    {
        var date = JobCodec.DateText(Now);
        var history = new[] { "enqueued", state }.Select(s => JsonSerializer.Serialize(new { state = s, at = date, reason = s })).ToArray();
        var outcomes = new string?[] { "failed", "interrupted", "succeeded", null };
        var records = outcomes.Select((o, i) => JsonSerializer.Serialize(new
        {
            attempt = i % 3 + 1, startedAt = date, finishedAt = o is null ? null : date,
            outcome = o, error = o == "failed" ? (attempts == 1 ? "demo failure" : "snapshot two") : null
        })).ToArray();
        const string script = """
            redis.call('HSET', KEYS[1], 'state', ARGV[1], 'attempts', ARGV[2])
            if ARGV[1] == 'scheduled' then redis.call('HSET', KEYS[1], 'scheduledAt', ARGV[3])
            else redis.call('HDEL', KEYS[1], 'scheduledAt') end
            redis.call('DEL', KEYS[2], KEYS[3])
            redis.call('RPUSH', KEYS[2], ARGV[4], ARGV[5])
            redis.call('RPUSH', KEYS[3], ARGV[6], ARGV[7], ARGV[8], ARGV[9])
            return 1
            """;
        await db.ScriptEvaluateAsync(script, keys[..3], [state, attempts, date, .. history.Select(h => (RedisValue)h), .. records.Select(r => (RedisValue)r)]);
    }

    [Fact]
    public async Task Concurrent_detail_never_combines_different_snapshots()
    {
        var id = await client.EnqueueAsync(definition, new Payload());
        var keys = store.Keys(id);
        await WriteSnapshot(keys, "processing", 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = Task.Run(async () =>
        {
            await gate.Task;
            for (var i = 0; i < 100; i++) await WriteSnapshot(keys, i % 2 == 0 ? "succeeded" : "processing", i % 2 == 0 ? 2 : 1);
        });
        var reading = Task.Run(async () =>
        {
            await gate.Task;
            for (var i = 0; i < 100; i++)
            {
                var detail = await Detail(id);
                Assert.Equal(detail.State == JobState.Processing ? 1 : 2, detail.Attempts);
                Assert.Equal(detail.State, detail.History[^1].State);
                Assert.Equal(4, detail.AttemptHistory.Count);
                Assert.Equal(detail.Attempts == 1 ? "demo failure" : "snapshot two", detail.AttemptHistory[0].Error);
            }
        });
        gate.SetResult();
        await Task.WhenAll(writing, reading);
    }

    [Fact]
    public async Task Prefixes_isolate_data_and_invalid_configuration_is_rejected()
    {
        var otherPrefix = prefix + "-other";
        try
        {
            IJobClient otherClient = new RedisJobClient(contracts, connection, otherPrefix);
            IJobManager otherManager = new RedisJobManager(connection, otherPrefix);
            var id = await client.EnqueueAsync(definition, new Payload());
            var otherId = await otherClient.EnqueueAsync(definition, new Payload());
            Assert.Null(await manager.GetAsync(otherId));
            Assert.Null(await otherManager.GetAsync(id));
            await db.KeyDeleteAsync(await AllKeys());
            Assert.NotNull(await otherManager.GetAsync(otherId));
        }
        finally { await db.KeyDeleteAsync(await AllKeys(otherPrefix)); }
        foreach (var invalid in new[] { "", " ", "bad:*", "bad:prefix", "á" })
        {
            Assert.Throws<ArgumentException>(() => new RedisJobClient(contracts, connection, invalid));
            Assert.Throws<ArgumentException>(() => new RedisJobManager(connection, invalid));
        }
    }

    [Fact]
    public async Task Infrastructure_errors_propagate_without_modifying_shared_Redis()
    {
        // A non-Redis listener avoids stopping or reconfiguring the shared server.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var configuration = ConfigurationOptions.Parse($"127.0.0.1:{port},abortConnect=false,connectTimeout=150,asyncTimeout=200,connectRetry=0");
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        await using var unavailable = await ConnectionMultiplexer.ConnectAsync(configuration);
        var failingClient = new RedisJobClient(contracts, unavailable, prefix);
        var failingManager = new RedisJobManager(unavailable, prefix);
        await Assert.ThrowsAnyAsync<RedisException>(() => failingClient.EnqueueAsync(definition, new Payload()));
        await Assert.ThrowsAnyAsync<RedisException>(() => failingManager.GetAsync("absent"));
        Assert.Empty(await AllKeys());
    }
}
