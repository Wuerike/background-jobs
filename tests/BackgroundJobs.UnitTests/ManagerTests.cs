using BackgroundJobs.Internal;

namespace BackgroundJobs.UnitTests;

public sealed class ManagerTests
{
    private sealed class Clock : TimeProvider;
    private sealed class Store : IJobStore
    {
        public readonly TaskCompletionSource<JobOperationResult> Mutation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<IReadOnlyList<JobSummary>> Listing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Id;
        public TimeProvider? Clock;
        public bool Requeue;
        public int Calls;
        public Task CreateAsync(NewJob job) => throw new NotSupportedException();
        public Task<JobDetails?> GetAsync(string id) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobSummary>> ListAsync() { Calls++; return Listing.Task; }
        public Task<JobOperationResult> DeleteAsync(string id, TimeProvider clock) => Mutate(id, clock, false);
        public Task<JobOperationResult> RequeueAsync(string id, TimeProvider clock) => Mutate(id, clock, true);
        private Task<JobOperationResult> Mutate(string id, TimeProvider clock, bool requeue)
        { Calls++; Id = id; Clock = clock; Requeue = requeue; return Mutation.Task; }
    }

    [Theory]
    [InlineData(false, JobOperationResult.Applied)] [InlineData(true, JobOperationResult.Applied)]
    [InlineData(false, JobOperationResult.NotFound)] [InlineData(true, JobOperationResult.NotFound)]
    [InlineData(false, JobOperationResult.Conflict)] [InlineData(true, JobOperationResult.Conflict)]
    public async Task Mutations_preserve_opaque_id_clock_and_confirmed_result(bool requeue, JobOperationResult result)
    {
        var store = new Store(); var clock = new Clock(); var manager = new RedisJobManager(store, clock);
        var pending = requeue ? manager.RequeueAsync(" opaque :🦊 ") : manager.DeleteAsync(" opaque :🦊 ");
        Assert.False(pending.IsCompleted); Assert.Equal(" opaque :🦊 ", store.Id); Assert.Same(clock, store.Clock);
        Assert.Equal(requeue, store.Requeue); store.Mutation.SetResult(result); Assert.Equal(result, await pending); Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task Listing_waits_for_storage_and_preserves_immutable_projection()
    {
        var store = new Store(); var manager = new RedisJobManager(store); var pending = manager.ListAsync();
        Assert.False(pending.IsCompleted); IReadOnlyList<JobSummary> result = Array.AsReadOnly(new[] { new JobSummary("opaque", "unregistered.v1", JobState.Deleted, 2) });
        store.Listing.SetResult(result); Assert.Same(result, await pending); Assert.Equal(1, store.Calls);
    }

    [Theory]
    [InlineData("list")] [InlineData("delete")] [InlineData("requeue")]
    public async Task Unexpected_storage_error_propagates_without_retry(string operation)
    {
        var store = new Store(); var manager = new RedisJobManager(store); var error = new IOException("unconfirmed");
        Task pending = operation switch { "list" => manager.ListAsync(), "delete" => manager.DeleteAsync(""), _ => manager.RequeueAsync("") };
        store.Listing.TrySetException(error); store.Mutation.TrySetException(error);
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => pending)); Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task Null_ids_fail_before_storage()
    {
        var store = new Store(); var manager = new RedisJobManager(store);
        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.DeleteAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.RequeueAsync(null!)); Assert.Equal(0, store.Calls);
    }
}
