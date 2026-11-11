using System.Text.Json;
using BackgroundJobs.Dashboard;

namespace BackgroundJobs.UnitTests;

public sealed class MonitoringTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-11T00:00:00Z");
    private static JobMonitorItem Item(string id, JobState state = JobState.Enqueued) =>
        new(id, "email.v1", state, state == JobState.Scheduled ? 1 : 0, 3, 1000, At, At,
            state == JobState.Scheduled ? At.AddSeconds(1) : null, null);

    [Fact]
    public void Query_applies_creation_bounds_search_state_stable_sort_and_pagination_without_filtering_totals()
    {
        var observation = new JobObservation(At, At, [Item("b"), Item("a"), Item("c", JobState.Scheduled)]);
        var page = observation.Query(new(Search: "EMAIL", From: At, Until: At.AddTicks(1), PageSize: 1));
        Assert.Equal("a", Assert.Single(page.Items).Id);
        Assert.Equal("b", Assert.Single(observation.Query(new(Page: 2, PageSize: 1)).Items).Id);
        Assert.Equal(3, page.Total);
        Assert.Empty(observation.Query(new(Until: At)).Items);
        Assert.Single(observation.Query(new(State: JobState.Scheduled)).Items);
        Assert.Single(observation.Query(new(Search: "b")).Items);
        Assert.Equal(1, observation.Retries);
        Assert.Equal(2, observation.Counts[JobState.Enqueued]);
        Assert.Equal(1, observation.Query(new(State: JobState.Failed, Page: int.MaxValue)).Page);
        Assert.Throws<NotSupportedException>(() => ((IList<JobMonitorItem>)observation.Items).Clear());
    }

    [Theory]
    [InlineData(0, 25)] [InlineData(1, 0)] [InlineData(1, 101)]
    public void Invalid_page_bounds_are_rejected(int page, int size) =>
        Assert.Throws<ArgumentException>(() => new JobObservation(At, At, []).Query(new(Page: page, PageSize: size)));

    [Fact]
    public void Invalid_filters_are_rejected()
    {
        var observation = new JobObservation(At, At, []);
        Assert.Throws<ArgumentException>(() => observation.Query(new(State: (JobState)99)));
        Assert.Throws<ArgumentException>(() => observation.Query(new(Order: (JobMonitorOrder)99)));
        Assert.Throws<ArgumentException>(() => observation.Query(new(Search: new string('a', 201))));
        Assert.Throws<ArgumentException>(() => observation.Query(new(From: At, Until: At)));
    }

    [Fact]
    public void Attempt_cycles_use_history_order_even_with_equal_timestamps_and_cycles_without_execution()
    {
        using var args = JsonDocument.Parse("{}");
        var states = new[] { JobState.Enqueued, JobState.Deleted, JobState.Enqueued, JobState.Processing,
            JobState.Scheduled, JobState.Enqueued, JobState.Processing, JobState.Succeeded,
            JobState.Enqueued, JobState.Processing, JobState.Failed };
        var attempts = new[] { new JobAttempt(1, At, At, JobAttemptOutcome.Failed, "failure"),
            new JobAttempt(2, At, At, JobAttemptOutcome.Succeeded, null),
            new JobAttempt(1, At, At, JobAttemptOutcome.Interrupted, "interrupted") };
        var job = new JobDetails("a", "job", args.RootElement, JobState.Failed, 1, 2, 1000, null, At, At,
            states.Select(state => new JobStateChange(state, At, "reason")).ToArray(), attempts);
        Assert.Equal(new[] { 2, 2, 3 }, Presentation.AttemptCycles(job));
        Assert.Equal(At, Presentation.CurrentCycleStartedAt(job));
        Assert.False(Presentation.CanDelete(JobState.Processing));
        Assert.False(Presentation.CanRequeue(JobState.Scheduled));
        Assert.True(Presentation.CanRequeue(JobState.Deleted));
        Assert.True(Presentation.CanDelete(JobState.Enqueued));
    }
}
