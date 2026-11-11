using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using BackgroundJobs;
using Xunit.Abstractions;

namespace BackgroundJobs.HttpTests;

[Trait("Category", "ProcessLifecycle")]
public sealed class ProcessLifecycleTests(ITestOutputHelper output)
{
    private static string Json(JobDetails job) => JsonSerializer.Serialize(job);

    [Fact]
    public async Task Dedicated_directories_isolate_data_and_complete_aof_copy_restarts_without_modifying_source()
    {
        await using var source = new ProcessHost(); await using var empty = new ProcessHost();
        try
        {
            await source.Start();
            var id = await source.Create("log-message", new { message = "copied AOF 🦊" });
            var before = await source.State(id, JobState.Succeeded);
            await source.KillDemo(); await source.StopRedis(force: false);
            var directory = source.DataDirectory;
            string[] Fingerprints() => Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order()
                .Select(p => Path.GetRelativePath(directory, p) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
            var original = Fingerprints();
            Assert.Contains(original, f => f.Contains("manifest"));
            Assert.True(original.Length >= 3, "Multipart AOF requires manifest, base and incremental files.");
            var destination = Path.Combine(Path.GetDirectoryName(directory)!, "copied complete directory");
            Directory.CreateDirectory(destination);
            foreach (var path in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(directory, path)));
            foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(directory, path));
                File.Copy(path, target, overwrite: false);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, File.GetUnixFileMode(path));
            }
            await empty.Start(); Assert.Empty(await empty.Manager.ListAsync());
            await source.StartRedis(destination); await ProcessHost.Until(async () => source.Connection.IsConnected);
            await source.StartDemo(); Assert.Equal(Json(before), Json((await source.Manager.GetAsync(id))!));
            var control = await source.Create("log-message", new { message = "destination writable" });
            await source.State(control, JobState.Succeeded);
            Assert.Equal(original, Fingerprints()); Assert.Empty(await empty.Manager.ListAsync());
            output.WriteLine($"Native multipart AOF copied completely; ID={id}; source retained; independent directory empty. Compose volume transfer requires its own Docker validation.");
        }
        finally { output.WriteLine(source.Log); output.WriteLine(empty.Log); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Accepted_jobs_finals_histories_and_schedules_survive_demo_and_redis_process_restart(bool forceRedis)
    {
        await using var host = new ProcessHost();
        try
        {
            await host.Start();
            var successId = await host.Create("log-message", new { message = "persistência 🦊" });
            var success = await host.State(successId, JobState.Succeeded);
            var failedId = await host.Create("fail-n-times", new { failTimes = 10 }, new { maxAttempts = 1 });
            var failed = await host.State(failedId, JobState.Failed);
            var recycled = await host.Create("log-message", new { message = "cycles" });
            await host.State(recycled, JobState.Succeeded);
            using (var response = await host.Http.PostAsync($"/api/jobs/{recycled}/requeue", null)) response.EnsureSuccessStatusCode();
            await ProcessHost.Until(async () => (await host.Manager.GetAsync(recycled))!.AttemptHistory.Count == 2);
            var cycle = await host.State(recycled, JobState.Succeeded);
            var deletedId = await host.Create("log-message", new { message = "deleted" }, new { runAt = DateTimeOffset.UtcNow.AddHours(1) });
            using (var response = await host.Http.DeleteAsync($"/api/jobs/{deletedId}")) response.EnsureSuccessStatusCode();
            var deleted = await host.State(deletedId, JobState.Deleted);
            var dueAt = DateTimeOffset.UtcNow.AddSeconds(2);
            var dueId = await host.Create("log-message", new { message = "overdue" }, new { runAt = dueAt });
            var futureId = await host.Create("log-message", new { message = "future" }, new { runAt = DateTimeOffset.UtcNow.AddHours(1) });
            var future = await host.State(futureId, JobState.Scheduled);
            await host.KillDemo();
            await host.StopRedis(forceRedis);
            Assert.NotEmpty(Directory.GetFiles(host.DataDirectory, "*", SearchOption.AllDirectories));
            await Task.Delay(TimeSpan.FromSeconds(2)); // Cross eligibility while both processes are stopped.
            await host.StartRedis(host.DataDirectory);
            await ProcessHost.Until(async () => host.Connection.IsConnected);
            await host.StartDemo();
            var elapsed = Stopwatch.StartNew();
            var due = await host.State(dueId, JobState.Succeeded);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
            Assert.True(due.AttemptHistory[0].StartedAt >= dueAt);
            Assert.Equal(Json(success), Json((await host.Manager.GetAsync(successId))!));
            Assert.Equal(Json(failed), Json((await host.Manager.GetAsync(failedId))!));
            Assert.Equal(Json(deleted), Json((await host.Manager.GetAsync(deletedId))!));
            Assert.Equal(Json(cycle), Json((await host.Manager.GetAsync(recycled))!));
            Assert.Equal(Json(future), Json((await host.Manager.GetAsync(futureId))!));
            Assert.Equal(6, (await host.Manager.ListAsync()).Count);
            Assert.DoesNotContain(await host.Events(), e => e.GetProperty("jobId").GetString() == futureId);
            var healthy = await host.Create("log-message", new { message = "healthy" });
            await host.State(healthy, JobState.Succeeded);
            output.WriteLine($"Redis forced={forceRedis}; preserved IDs={successId},{failedId},{deletedId},{recycled},{futureId}; overdue={dueId}; post-readiness completion={elapsed.Elapsed}.");
        }
        finally { output.WriteLine(host.Log); }
    }

    [Theory]
    [InlineData(1)] [InlineData(3)]
    public async Task Killed_handler_is_interrupted_and_only_budgeted_attempts_run_after_restart(int max)
    {
        await using var host = new ProcessHost();
        try
        {
            await host.Start();
            var id = await host.Create("succeed-after", new { delayMs = 3000 }, new { maxAttempts = max, retryDelayMs = 60000 });
            var before = await host.State(id, JobState.Processing);
            await ProcessHost.Until(async () => (await host.Events()).Any(e => e.GetProperty("jobId").GetString() == id && e.GetProperty("type").GetString() == "started"));
            Assert.DoesNotContain(await host.Events(), e => e.GetProperty("jobId").GetString() == id && e.GetProperty("type").GetString() == "finished");
            await host.KillDemo(); await host.StartDemo();
            var elapsed = Stopwatch.StartNew();
            JobDetails after;
            if (max == 1) after = await host.State(id, JobState.Failed);
            else
            {
                JobDetails? running = null;
                await ProcessHost.Until(async () => (running = await host.Manager.GetAsync(id))!.AttemptHistory.Count == 2);
                after = running!;
            }
            var observation = elapsed.Elapsed;
            var interrupted = after.AttemptHistory[0];
            Assert.Equal(before.AttemptHistory[0].StartedAt, interrupted.StartedAt);
            Assert.Equal(JobAttemptOutcome.Interrupted, interrupted.Outcome); Assert.NotNull(interrupted.Error); Assert.NotNull(interrupted.FinishedAt);
            Assert.Equal(before.Args.GetRawText(), after.Args.GetRawText());
            Assert.Equal(before.History, after.History.Take(before.History.Count));
            Assert.DoesNotContain(after.History, h => h.State == JobState.Scheduled);
            if (max > 1)
            {
                await ProcessHost.Until(async () => (await host.Events()).Any(e => e.GetProperty("jobId").GetString() == id));
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
                var final = await host.State(id, JobState.Succeeded);
                Assert.Equal(new[] { 1, 2 }, final.AttemptHistory.Select(a => a.Attempt));
                Assert.Equal(JobAttemptOutcome.Succeeded, final.AttemptHistory[1].Outcome);
                Assert.Equal(new[] { JobState.Enqueued, JobState.Processing, JobState.Enqueued, JobState.Processing, JobState.Succeeded }, final.History.Select(h => h.State));
                Assert.All((await host.Events()).Where(e => e.GetProperty("jobId").GetString() == id), e => Assert.Equal(2, e.GetProperty("attempt").GetInt32()));
            }
            else
            {
                Assert.Single(after.AttemptHistory); Assert.Equal(1, after.Attempts);
                Assert.DoesNotContain(await host.Events(), e => e.GetProperty("jobId").GetString() == id);
                var healthy = await host.Create("log-message", new { message = "still running" }); await host.State(healthy, JobState.Succeeded);
                using var response = await host.Http.PostAsync($"/api/jobs/{id}/requeue", null); response.EnsureSuccessStatusCode();
                var final = await host.State(id, JobState.Succeeded);
                Assert.Equal(new[] { 1, 1 }, final.AttemptHistory.Select(a => a.Attempt));
                Assert.Equal(interrupted, final.AttemptHistory[0]);
            }
            // HTTP uses the same interrupted outcome and existing public fields.
            var http = await host.Http.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}");
            Assert.Equal("interrupted", http.GetProperty("attemptHistory")[0].GetProperty("outcome").GetString());
            Assert.Equal(new[] { "args", "attemptHistory", "attempts", "createdAt", "history", "id", "maxAttempts", "name", "retryDelayMs", "scheduledAt", "state", "updatedAt" }, http.EnumerateObject().Select(p => p.Name).Order());
            output.WriteLine($"Killed PID confirmed exited; ID={id}; maxAttempts={max}; recovery/restart succeeded; next-attempt observation={observation}.");
        }
        finally { output.WriteLine(host.Log); }
    }

    [Theory]
    [InlineData("TERM", 1, 35000)] [InlineData("INT", 1, 4000)] [InlineData("TERM", 2, 4000)]
    public async Task Graceful_signal_drains_every_active_function_and_leaves_pending_until_restart(string signal, int workers, int delayMs)
    {
        await using var host = new ProcessHost();
        try
        {
            await host.Start(workers);
            var active = new List<string>();
            for (var i = 0; i < workers; i++) active.Add(await host.Create("succeed-after", new { delayMs }));
            foreach (var id in active) await host.State(id, JobState.Processing);
            var pending = await host.Create("log-message", new { message = "after restart" });
            var queued = await host.State(pending, JobState.Enqueued); Assert.Equal(0, queued.Attempts);
            await ProcessHost.Until(async () => (await host.Events()).Count(e => e.GetProperty("type").GetString() == "started") == workers);
            var events = await host.Events();
            Assert.DoesNotContain(events, e => e.GetProperty("jobId").GetString() == pending);
            var elapsed = Stopwatch.StartNew(); await host.SignalDemo(signal);
            await Task.Delay(300); Assert.False(host.Demo.HasExited);
            foreach (var id in active)
            {
                var final = await host.State(id, JobState.Succeeded, TimeSpan.FromSeconds(45));
                Assert.Equal(JobAttemptOutcome.Succeeded, Assert.Single(final.AttemptHistory).Outcome);
            }
            await host.Demo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, host.Demo.ExitCode); Assert.True(elapsed.Elapsed > TimeSpan.FromSeconds(1));
            if (delayMs > 30000) Assert.True(elapsed.Elapsed > TimeSpan.FromSeconds(30), "The function must outlive the host's default shutdown window.");
            var drainDuration = elapsed.Elapsed;
            var untouched = await host.Manager.GetAsync(pending);
            Assert.Equal(Json(queued), Json(untouched!)); Assert.Empty(untouched!.AttemptHistory);
            await host.StartDemo(workers); var completed = await host.State(pending, JobState.Succeeded);
            Assert.Single(completed.AttemptHistory);
            var starts = (await host.Events()).Where(e => e.GetProperty("type").GetString() == "started").ToArray();
            Assert.Equal(pending, Assert.Single(starts).GetProperty("jobId").GetString());
            output.WriteLine($"SIG{signal}; workers={workers}; drain duration={drainDuration}; active={string.Join(',', active)}; pending={pending}; exit=0.");
        }
        finally { output.WriteLine(host.Log); }
    }
}
