using BackgroundJobs.Internal;

namespace BackgroundJobs.UnitTests;

public sealed partial class ConcurrencyTests
{
    private static RecoveryCandidate Recovery(string id) => new(Candidate(id).Job, "snapshot");

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Full_handler_verification_precedes_recovery_even_when_stopped_during_reference_scan(bool missing, bool stopping)
    {
        var entered = Signal(); var release = Signal(); var registry = new JobRegistry();
        registry.Register(new JobDefinition<Args>("job"), (_, _) => Task.CompletedTask);
        var store = new Store { References = [new("finalized", missing ? "absent" : "job", JobState.Succeeded)] };
        store.BeforeReferences = async () => { entered.SetResult(); await release.Task; };
        await using var server = new JobServer(registry, store);
        try
        {
            var start = server.StartAsync(); await entered.Task.WaitAsync(Timeout);
            Assert.False(start.IsCompleted); Assert.False(server.IsAvailable);
            Assert.Equal(0, store.RecoverySelections); Assert.Equal(0, store.Selections); Assert.Equal(0, store.Reads);
            var stop = stopping ? server.StopAsync() : null;
            if (stop is not null) Assert.False(stop.IsCompleted);
            release.SetResult();
            if (missing)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => start);
                Assert.Contains("absent", error.Message); Assert.Contains("finalized", error.Message);
            }
            else await start.WaitAsync(Timeout);
            await (stop ?? server.StopAsync()).WaitAsync(Timeout);
            Assert.Equal(missing || stopping ? 0 : 1, store.RecoverySelections);
            Assert.Equal(0, store.Recoveries); Assert.Empty(store.Reservations);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Stop_during_recovery_waits_for_admitted_selection_or_confirmation_and_never_publishes_readiness(bool confirming)
    {
        var entered = Signal(); var release = Signal(); var store = new Store(Candidate("pending"));
        store.SelectRecovery = async () =>
        {
            if (!confirming) { entered.TrySetResult(); await release.Task; }
            return [Recovery("first"), Recovery("second")];
        };
        store.Recover = async (_, _) => { entered.TrySetResult(); await release.Task; return RecoveryResult.Recovered; };
        await using var server = new JobServer(new(), store);
        try
        {
            var start = server.StartAsync(); Assert.Same(start, server.StartAsync());
            await entered.Task.WaitAsync(Timeout); Assert.False(start.IsCompleted); Assert.False(server.IsAvailable);
            var stop = server.StopAsync(); var repeated = server.StopAsync();
            Assert.False(stop.IsCompleted); Assert.False(repeated.IsCompleted);
            release.SetResult(); await Task.WhenAll(start, stop, repeated).WaitAsync(Timeout);
            Assert.Equal(1, store.RecoverySelections); Assert.Equal(confirming ? 1 : 0, store.Recoveries);
            Assert.Equal(0, store.Selections); Assert.Equal(0, store.Reads); Assert.Empty(store.Reservations);
            Assert.False(server.IsAvailable); Assert.Null(server.LastError);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("select-sync")] [InlineData("select-async")]
    [InlineData("confirm-sync")] [InlineData("confirm-async")]
    public async Task Recovery_failure_preserves_first_error_shares_startup_failure_and_never_invokes_handler(string phase)
    {
        var store = new Store(Candidate("pending")); var error = new IOException("recovery failure");
        store.SelectRecovery = () => phase switch
        {
            "select-sync" => throw error,
            "select-async" => Task.FromException<IReadOnlyList<RecoveryCandidate>>(error),
            _ => Task.FromResult<IReadOnlyList<RecoveryCandidate>>([Recovery("first"), Recovery("second")])
        };
        store.Recover = (_, _) => phase == "confirm-sync" ? throw error : Task.FromException<RecoveryResult>(error);
        await using var server = new JobServer(new(), store);
        var start = server.StartAsync();
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => start));
        Assert.Same(start, server.StartAsync()); Assert.Same(error, server.LastError);
        Assert.Equal(1, store.RecoverySelections); Assert.Equal(phase.StartsWith("select") ? 0 : 1, store.Recoveries);
        Assert.Equal(0, store.Selections); Assert.Equal(0, store.Reads); Assert.False(server.IsAvailable);
        await server.StopAsync();
    }

    [Fact]
    public async Task Recovery_drains_all_batches_including_duplicate_candidates_before_promoting_or_invoking()
    {
        var store = new Store(Candidate("pending")); var entered = Signal(); var release = Signal();
        var calls = new List<string>(); var registry = new JobRegistry();
        registry.Register(new JobDefinition<Args>("job"), (_, _) => { calls.Add("handler"); return Task.CompletedTask; });
        store.Recover = async (candidate, _) =>
        {
            calls.Add(candidate.Job.Id);
            if (store.Recoveries == 3) { entered.SetResult(); await release.Task; }
            return store.Recoveries == 2 ? RecoveryResult.AlreadyAdvanced : RecoveryResult.Recovered;
        };
        store.SelectRecovery = () => Task.FromResult<IReadOnlyList<RecoveryCandidate>>(store.RecoverySelections switch
        {
            1 => [Recovery("a"), Recovery("a")], 2 => [Recovery("b")], _ => []
        });
        store.SelectScheduled = (_, _) => { Assert.Equal(3, store.Recoveries); calls.Add("promotion selection"); return Task.FromResult<IReadOnlyList<ScheduledCandidate>>([]); };
        await using var server = new JobServer(registry, store);
        try
        {
            var start = server.StartAsync(); await entered.Task.WaitAsync(Timeout);
            Assert.Equal(new[] { "a", "a", "b" }, calls); Assert.False(server.IsAvailable);
            Assert.Equal(0, store.Reads); Assert.Equal(0, store.Selections);
            release.SetResult(); await start.WaitAsync(Timeout); await Until(() => store.Completions.Count == 1);
            await server.StopAsync(); Assert.Null(server.LastError);
            Assert.Equal(3, store.RecoverySelections); Assert.True(calls.IndexOf("promotion selection") < calls.IndexOf("handler"));
        }
        finally { release.TrySetResult(); }
    }
}
