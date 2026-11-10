using System.Text.Json;
using BackgroundJobs.Internal;
using BackgroundJobs.Storage.Redis;

namespace BackgroundJobs.UnitTests;

public class ClientTests
{
    private sealed class ControlledStore : IJobStore
    {
        public Task<IReadOnlyList<JobSummary>> ListAsync() => throw new NotSupportedException();
        public Task<JobOperationResult> DeleteAsync(string id, TimeProvider clock) => throw new NotSupportedException();
        public Task<JobOperationResult> RequeueAsync(string id, TimeProvider clock) => throw new NotSupportedException();
        public List<NewJob> Writes { get; } = [];
        public TaskCompletionSource Confirmation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CreateAsync(NewJob job) { Writes.Add(job); return Confirmation.Task; }
        public Task<JobDetails?> GetAsync(string id) => Task.FromException<JobDetails?>(new IOException("read failed"));
    }

    public sealed class MutableArgs { public List<string> Messages { get; set; } = []; }
    public sealed class CyclicArgs { public CyclicArgs? Next { get; set; } }

    [Fact]
    public async Task Creation_waits_for_confirmation_and_preserves_prepared_snapshot()
    {
        var definition = new JobDefinition<MutableArgs>("messages");
        var contracts = new JobContracts();
        contracts.Add(definition);
        var clock = new CountingClock();
        var store = new ControlledStore();
        IJobClient client = new RedisJobClient(contracts, store, clock);
        var args = new MutableArgs { Messages = ["original"] };
        var pending = client.EnqueueAsync(definition, args, new JobOptions { RunAt = CountingClock.Initial.AddTicks(1) });
        Assert.False(pending.IsCompleted);
        var write = Assert.Single(store.Writes);
        args.Messages[0] = "changed";
        Assert.Equal(1, clock.Reads);
        Assert.Equal(CountingClock.Initial, write.Prepared.CreatedAt);
        Assert.Equal(JobState.Scheduled, write.Prepared.State);
        Assert.Equal("original", write.Prepared.Descriptor.Args.GetProperty("messages")[0].GetString());
        store.Confirmation.SetResult();
        Assert.Equal(write.Id, await pending);
        Assert.NotEmpty(write.Id);
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("argument")]
    [InlineData("json")]
    [InlineData("unexpected")]
    public async Task Validator_errors_propagate_unchanged_without_storage(string kind)
    {
        Exception error = kind switch
        {
            "validation" => new JobValidationException("rejected"), "argument" => new ArgumentException("bug"),
            "json" => new JsonException("bug"), _ => new NullReferenceException("bug")
        };
        var definition = new JobDefinition<string>("test");
        var contracts = new JobContracts();
        contracts.Add(definition, _ => throw error);
        var store = new ControlledStore();
        var client = new RedisJobClient(contracts, store);
        Assert.Same(error, await Assert.ThrowsAsync(error.GetType(), () => client.EnqueueAsync(definition, "hello")));
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Invalid_options_unknown_contract_and_wrong_type_never_call_storage()
    {
        var definition = new JobDefinition<string>("test");
        var contracts = new JobContracts();
        contracts.Add(definition);
        var store = new ControlledStore();
        var client = new RedisJobClient(contracts, store);
        await Assert.ThrowsAsync<JobValidationException>(() => client.EnqueueAsync(definition, "ok", new JobOptions { MaxAttempts = 0 }));
        await Assert.ThrowsAsync<JobValidationException>(() => client.EnqueueAsync(definition, "\ud800"));
        await Assert.ThrowsAnyAsync<Exception>(() => client.EnqueueAsync(new JobDefinition<string>("unknown"), "ok"));
        await Assert.ThrowsAnyAsync<Exception>(() => client.EnqueueAsync(new JobDefinition<int>("test"), 42));
        var cyclicDefinition = new JobDefinition<CyclicArgs>("cycle");
        contracts.Add(cyclicDefinition);
        var cycle = new CyclicArgs();
        cycle.Next = cycle;
        await Assert.ThrowsAsync<JsonException>(() => client.EnqueueAsync(cyclicDefinition, cycle));
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Storage_failures_do_not_return_success_or_absence()
    {
        var contracts = new JobContracts();
        var definition = new JobDefinition<string>("test");
        contracts.Add(definition);
        var store = new ControlledStore();
        var client = new RedisJobClient(contracts, store);
        var pending = client.EnqueueAsync(definition, "ok");
        var error = new IOException("write failed");
        store.Confirmation.SetException(error);
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => pending));
        var manager = new RedisJobManager(store);
        await Assert.ThrowsAsync<IOException>(() => manager.GetAsync("opaque"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.GetAsync(null!));
    }

    [Fact]
    public void Score_rounds_up_without_losing_ticks_in_any_culture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("pt-BR");
            var dates = new[] { DateTimeOffset.MinValue, DateTimeOffset.UnixEpoch.AddTicks(-1),
                DateTimeOffset.UnixEpoch, CountingClock.Initial.AddTicks(1), CountingClock.Initial.AddTicks(9999), DateTimeOffset.MaxValue };
            foreach (var date in dates)
            {
                var exactTicks = date.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
                var roundedTicks = JobCodec.ScheduledScore(date) * TimeSpan.TicksPerMillisecond;
                Assert.InRange(roundedTicks - exactTicks, 0, TimeSpan.TicksPerMillisecond - 1);
                Assert.Equal(date, DateTimeOffset.ParseExact(JobCodec.DateText(date), "O", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }
}
