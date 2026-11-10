using BackgroundJobs.Internal;

namespace BackgroundJobs.UnitTests;

public sealed partial class ConcurrencyTests
{
    private static ScheduledCandidate ScheduledCandidate(string id = "scheduled")
    {
        var candidate = Candidate(id);
        var job = candidate.Job;
        return new(new JobDetails(job.Id, job.Name, job.Args, JobState.Scheduled, 0, 3, 0, job.CreatedAt, job.CreatedAt, job.UpdatedAt, job.History, []), "snapshot", 1);
    }

    [Theory]
    [InlineData("missing")] [InlineData("residual")]
    public async Task Failed_startup_checks_prevent_scheduler_selection_and_promotion(string failure)
    {
        var store = new Store { Residual = failure == "residual" };
        if (failure == "missing") store.References = [new("a", "missing", JobState.Scheduled)];
        await using var server = new JobServer(new(), store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
        Assert.Equal(0, store.Selections); Assert.Equal(0, store.Promotions);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Stop_during_initial_selection_or_promotion_waits_without_publishing_readiness(bool promoting)
    {
        var entered = Signal(); var release = Signal(); var store = new Store();
        store.SelectScheduled = async (_, _) =>
        {
            if (!promoting) { entered.TrySetResult(); await release.Task; }
            return [ScheduledCandidate(), ScheduledCandidate("other")];
        };
        store.Promote = async (_, _) => { entered.TrySetResult(); await release.Task; return PromotionResult.Promoted; };
        await using var server = new JobServer(new(), store);
        try
        {
            var start = server.StartAsync(); await entered.Task.WaitAsync(Timeout);
            Assert.False(server.IsAvailable); Assert.False(start.IsCompleted);
            var stop = server.StopAsync(); Assert.False(stop.IsCompleted);
            release.SetResult(); await start.WaitAsync(Timeout); await stop.WaitAsync(Timeout);
            Assert.False(server.IsAvailable); Assert.Equal(promoting ? 1 : 0, store.Promotions);
            Assert.Equal(0, store.Reads); Assert.Null(server.LastError);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("selection-sync")] [InlineData("selection-async")]
    [InlineData("promotion-sync")] [InlineData("promotion-async")]
    public async Task Scheduler_failure_closes_worker_admission_and_drains_active_handler(string failure)
    {
        var entered = Signal(); var release = Signal(); var trigger = Signal(); var selecting = Signal();
        var error = new IOException("scheduler failure"); var store = new Store(Candidate("active"), Candidate("pending"));
        store.SelectScheduled = (_, _) =>
        {
            if (store.Selections == 1) return Task.FromResult<IReadOnlyList<ScheduledCandidate>>([]);
            selecting.TrySetResult();
            if (failure == "selection-sync") throw error;
            return Select();
        };
        async Task<IReadOnlyList<ScheduledCandidate>> Select()
        { await trigger.Task; if (failure == "selection-async") throw error; return [ScheduledCandidate()]; }
        store.Promote = (_, _) => failure == "promotion-sync" ? throw error : Task.FromException<PromotionResult>(error);
        // Keep scheduler's first runtime read behind the handler for synchronous errors too.
        var firstRead = store.SelectScheduled;
        store.SelectScheduled = async (at, offset) =>
        { if (store.Selections > 1) { await entered.Task; await trigger.Task; } return await firstRead(at, offset); };
        var registry = new JobRegistry();
        registry.Register(new JobDefinition<Args>("job"), async (_, _) => { entered.TrySetResult(); await release.Task; });
        await using var server = new JobServer(registry, store, workerCount: 1);
        try
        {
            await server.StartAsync(); await entered.Task.WaitAsync(Timeout); trigger.SetResult();
            await Until(() => server.LastError is not null);
            Assert.Same(error, server.LastError); Assert.False(server.IsAvailable);
            var stop = server.StopAsync(); Assert.False(stop.IsCompleted);
            release.SetResult(); await stop.WaitAsync(Timeout);
            Assert.Single(store.Reservations); Assert.Single(store.Completions); Assert.Equal(1, store.Pending);
        }
        finally { trigger.TrySetResult(); release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Stop_or_worker_failure_waits_for_pending_promotion_and_rejects_next_candidate(bool fatal)
    {
        var promoting = Signal(); var confirmation = Signal(); var fail = Signal(); var store = new Store();
        var error = new IOException("worker first"); var later = new IOException("promotion later");
        store.SelectScheduled = (_, _) => Task.FromResult<IReadOnlyList<ScheduledCandidate>>(
            store.Selections == 1 ? [] : [ScheduledCandidate(), ScheduledCandidate("next")]);
        store.Promote = async (_, _) =>
        { promoting.TrySetResult(); await confirmation.Task; if (fatal) throw later; return PromotionResult.Promoted; };
        store.Peek = async _ => { await fail.Task; if (fatal) throw error; return null; };
        await using var server = new JobServer(new(), store, workerCount: 1);
        try
        {
            await server.StartAsync(); await promoting.Task.WaitAsync(Timeout);
            if (fatal) { fail.SetResult(); await Until(() => server.LastError is not null); }
            var stop = server.StopAsync(); Assert.False(stop.IsCompleted); Assert.False(server.IsAvailable);
            fail.TrySetResult(); confirmation.SetResult(); await stop.WaitAsync(Timeout);
            Assert.Equal(1, store.Promotions); Assert.Empty(store.Reservations);
            Assert.Same(fatal ? error : null, server.LastError);
        }
        finally { fail.TrySetResult(); confirmation.TrySetResult(); }
    }

    [Fact]
    public async Task Runtime_selection_pending_at_stop_cannot_admit_promotion()
    {
        var selecting = Signal(); var release = Signal(); var store = new Store();
        store.SelectScheduled = async (_, _) =>
        { if (store.Selections == 1) return []; selecting.TrySetResult(); await release.Task; return [ScheduledCandidate()]; };
        await using var server = new JobServer(new(), store);
        try
        {
            await server.StartAsync(); await selecting.Task.WaitAsync(Timeout);
            var stop = server.StopAsync(); Assert.False(stop.IsCompleted);
            release.SetResult(); await stop.WaitAsync(Timeout);
            Assert.Equal(0, store.Promotions); Assert.Null(server.LastError);
        }
        finally { release.TrySetResult(); }
    }
}

public sealed partial class ConcurrencyTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Initial_promotion_finishes_before_workers_and_failed_confirmation_prevents_readiness(bool fail)
    {
        var entered = Signal(); var release = Signal(); var error = new IOException("unconfirmed promotion");
        var store = new Store();
        store.SelectScheduled = (_, _) => Task.FromResult<IReadOnlyList<ScheduledCandidate>>(store.Selections == 1 ? [ScheduledCandidate()] : []);
        store.Promote = async (_, _) => { entered.SetResult(); await release.Task; if (fail) throw error; return PromotionResult.Promoted; };
        await using var server = new JobServer(new(), store);
        try
        {
            var starts = Enumerable.Range(0,8).Select(_ => server.StartAsync()).ToArray();
            await entered.Task.WaitAsync(Timeout);
            Assert.False(server.IsAvailable); Assert.Equal(0, store.Reads); Assert.All(starts, t => Assert.False(t.IsCompleted));
            release.SetResult();
            if (fail)
            {
                Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => Task.WhenAll(starts)));
                Assert.Equal(0, store.Reads); Assert.Equal(1, store.Selections); Assert.Same(error, server.LastError);
            }
            else { await Task.WhenAll(starts); Assert.True(server.IsAvailable); }
            await server.StopAsync(); Assert.Equal(1, store.Promotions);
        }
        finally { release.TrySetResult(); }
    }
}
