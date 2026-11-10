using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BackgroundJobs.Demo.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BackgroundJobs.HttpTests;

public sealed partial class JobHttpTests
{
    [Fact]
    public async Task Management_routes_list_delete_requeue_and_report_conflict_without_body()
    {
        using var empty = await http.GetAsync("/api/jobs"); Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Empty((await Body(empty)).GetProperty("items").EnumerateArray());
        var id = await client.EnqueueAsync(DemoJobs.LogMessage, new LogMessageArgs("original"));
        var initial = (await manager.GetAsync(id))!;
        using var conflict = await http.PostAsync($"/api/jobs/{id}/requeue", null); await AssertError(conflict, HttpStatusCode.Conflict);
        using var deletion = await http.DeleteAsync($"/api/jobs/{id}"); Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
        var deletedBody = await Body(deletion); Assert.Equal(2, deletedBody.EnumerateObject().Count());
        Assert.Equal(id, deletedBody.GetProperty("id").GetString()); Assert.Equal("deleted", deletedBody.GetProperty("state").GetString());
        using var twice = await http.DeleteAsync($"/api/jobs/{id}"); await AssertError(twice, HttpStatusCode.Conflict);
        using var listing = await http.GetAsync("/api/jobs"); var summary = Assert.Single((await Body(listing)).GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "attempts", "id", "name", "state" }, summary.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("deleted", summary.GetProperty("state").GetString()); Assert.Equal(0, summary.GetProperty("attempts").GetInt32());
        using var requeue = await http.PostAsync($"/api/jobs/{id}/requeue", null); Assert.Equal(HttpStatusCode.Accepted, requeue.StatusCode);
        var requeueBody = await Body(requeue); Assert.Single(requeueBody.EnumerateObject()); Assert.Equal(id, requeueBody.GetProperty("jobId").GetString());
        var job = (await manager.GetAsync(id))!; Assert.Equal(JobState.Enqueued, job.State); Assert.Equal(0, job.Attempts);
        Assert.Equal(initial.Args.GetRawText(), job.Args.GetRawText()); Assert.Equal(initial.CreatedAt, job.CreatedAt);
        Assert.Equal(new[] { JobState.Enqueued, JobState.Deleted, JobState.Enqueued }, job.History.Select(h => h.State));
        Assert.Empty(job.AttemptHistory); Assert.Equal(0, executions);
        var future = await client.EnqueueAsync(DemoJobs.LogMessage, new LogMessageArgs("future"), new() { RunAt = Now.AddDays(1) });
        using var futureConflict = await http.PostAsync($"/api/jobs/{future}/requeue", null); await AssertError(futureConflict, HttpStatusCode.Conflict);
        using var futureDelete = await http.DeleteAsync($"/api/jobs/{future}"); Assert.Equal(HttpStatusCode.OK, futureDelete.StatusCode);
        foreach (var unknown in new[] { "unknown", "opaque%20id" })
        {
            using var missingDelete = await http.DeleteAsync($"/api/jobs/{unknown}"); await AssertError(missingDelete, HttpStatusCode.NotFound);
            using var missingRequeue = await http.PostAsync($"/api/jobs/{unknown}/requeue", null); await AssertError(missingRequeue, HttpStatusCode.NotFound);
        }
    }

    private sealed class ConfirmingManager : IJobManager
    {
        public readonly TaskCompletionSource<JobOperationResult> Confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<JobDetails?> GetAsync(string id) => throw new InvalidOperationException("Must not read after a mutation.");
        public Task<IReadOnlyList<JobSummary>> ListAsync() => throw new NotSupportedException();
        public Task<JobOperationResult> DeleteAsync(string id) { Entered.TrySetResult(); return Confirmation.Task; }
        public Task<JobOperationResult> RequeueAsync(string id) { Entered.TrySetResult(); return Confirmation.Task; }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Mutation_response_waits_for_confirmation_and_handles_unknown_write_result(bool requeue, bool fail)
    {
        var controlled = new ConfirmingManager();
        await using var host = new Factory(services => { services.RemoveAll<IJobManager>(); services.AddSingleton<IJobManager>(controlled); });
        using var requestHttp = host.CreateClient();
        var pending = requeue ? requestHttp.PostAsync("/api/jobs/opaque%20id/requeue", null) : requestHttp.DeleteAsync("/api/jobs/opaque%20id");
        await controlled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
        if (fail) controlled.Confirmation.SetException(new IOException("private infrastructure details"));
        else controlled.Confirmation.SetResult(JobOperationResult.Applied);
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(fail ? HttpStatusCode.InternalServerError : requeue ? HttpStatusCode.Accepted : HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("private", await response.Content.ReadAsStringAsync());
    }
}

public sealed partial class ExecutionHttpTests
{
    [Fact]
    public async Task Acceptance_manual_cycle_preserves_retry_history_events_and_deleted_schedule_never_runs()
    {
        await using var host = new Host(); await host.Start();
        var due = DateTimeOffset.UtcNow.AddSeconds(1);
        var deleted = await host.Create("log-message", new { message = "deleted" }, new { runAt = due });
        using var delete = await host.Http.DeleteAsync($"/api/jobs/{deleted}"); Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var id = await host.Create("fail-n-times", new { failTimes = 1 }, new { maxAttempts = 2, retryDelayMs = 500 });
        var first = await host.WaitState(id, "succeeded");
        using var requeue = await host.Http.PostAsync($"/api/jobs/{id}/requeue", null); Assert.Equal(HttpStatusCode.Accepted, requeue.StatusCode);
        Assert.Equal(id, (await Body(requeue)).GetProperty("jobId").GetString());
        var final = await host.WaitState(id, "succeeded");
        // WaitState may read the old terminal only if the operation did not persist; inspect history explicitly.
        var attempts = final.GetProperty("attemptHistory").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1, 2, 1, 2 }, attempts.Select(a => a.GetProperty("attempt").GetInt32()));
        Assert.Equal(first.GetProperty("attemptHistory").EnumerateArray().Select(a => a.GetRawText()), attempts.Take(2).Select(a => a.GetRawText()));
        Assert.Equal(first.GetProperty("history").EnumerateArray().Select(a => a.GetRawText()), final.GetProperty("history").EnumerateArray().Take(first.GetProperty("history").GetArrayLength()).Select(a => a.GetRawText()));
        foreach (var offset in new[] { 0, 2 })
        {
            Assert.Equal("demo failure", attempts[offset].GetProperty("error").GetString());
            Assert.True(attempts[offset + 1].GetProperty("startedAt").GetDateTimeOffset() - attempts[offset].GetProperty("finishedAt").GetDateTimeOffset() >= TimeSpan.FromMilliseconds(500));
        }
        using var eventsResponse = await host.Http.GetAsync("/demo/events"); var events = (await Body(eventsResponse)).GetProperty("items").EnumerateArray().ToArray();
        Assert.True(DateTimeOffset.UtcNow > due); Assert.DoesNotContain(events, e => e.GetProperty("jobId").GetString() == deleted);
        Assert.Equal(new[] { 1, 2, 1, 2 }, events.Where(e => e.GetProperty("jobId").GetString() == id && e.GetProperty("type").GetString() == "started").Select(e => e.GetProperty("attempt").GetInt32()));
        using var twice = await host.Http.DeleteAsync($"/api/jobs/{deleted}"); Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        using var revive = await host.Http.PostAsync($"/api/jobs/{deleted}/requeue", null); Assert.Equal(HttpStatusCode.Accepted, revive.StatusCode);
        await host.WaitState(deleted, "succeeded");
        var failed = await host.Create("fail-n-times", new { failTimes = 10 }, new { maxAttempts = 1 }); await host.WaitState(failed, "failed");
        using var failedDelete = await host.Http.DeleteAsync($"/api/jobs/{failed}"); Assert.Equal(HttpStatusCode.Conflict, failedDelete.StatusCode);
        using var failedRequeue = await host.Http.PostAsync($"/api/jobs/{failed}/requeue", null); Assert.Equal(HttpStatusCode.Accepted, failedRequeue.StatusCode);
        var failedAgain = await host.WaitState(failed, "failed"); Assert.Equal(2, failedAgain.GetProperty("attemptHistory").GetArrayLength());
        using var listResponse = await host.Http.GetAsync("/api/jobs"); var summaries = (await Body(listResponse)).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, summaries.Length);
        foreach (var item in summaries)
        {
            var detail = await host.WaitState(item.GetProperty("id").GetString()!, item.GetProperty("state").GetString()!);
            Assert.Equal(detail.GetProperty("attempts").GetInt32(), item.GetProperty("attempts").GetInt32());
        }
        using var health = await host.Http.GetAsync("/health"); Assert.Equal(HttpStatusCode.OK, health.StatusCode); Assert.Null(host.Server.LastError);
    }

    [Fact]
    public async Task Processing_routes_report_conflicts_while_handler_is_blocked()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Host();
        await host.Start((registry, _) => registry.Register(DemoJobs.SucceedAfter, async (_, _) => { entered.SetResult(); await release.Task; }));
        try
        {
            var id = await host.Create("succeed-after", new { delayMs = 0 }); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var delete = await host.Http.DeleteAsync($"/api/jobs/{id}"); Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
            using var requeue = await host.Http.PostAsync($"/api/jobs/{id}/requeue", null); Assert.Equal(HttpStatusCode.Conflict, requeue.StatusCode);
            using var list = await host.Http.GetAsync("/api/jobs"); var item = Assert.Single((await Body(list)).GetProperty("items").EnumerateArray());
            Assert.Equal("processing", item.GetProperty("state").GetString()); Assert.Equal(1, item.GetProperty("attempts").GetInt32());
            release.SetResult(); await host.WaitState(id, "succeeded"); Assert.Null(host.Server.LastError);
        }
        finally { release.TrySetResult(); }
    }
}

public sealed partial class JobHttpTests
{
    [Fact]
    public async Task Orphaned_index_is_generic_server_error_for_list_and_both_mutations()
    {
        await connection.GetDatabase().SetAddAsync(prefix + ":jobs", "orphan");
        using var listing = await http.GetAsync("/api/jobs"); await AssertError(listing, HttpStatusCode.InternalServerError);
        using var deletion = await http.DeleteAsync("/api/jobs/orphan"); await AssertError(deletion, HttpStatusCode.InternalServerError);
        using var requeue = await http.PostAsync("/api/jobs/orphan/requeue", null); await AssertError(requeue, HttpStatusCode.InternalServerError);
        Assert.DoesNotContain(prefix, await listing.Content.ReadAsStringAsync());
    }
}
