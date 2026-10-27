using BackgroundJobs.Demo.Jobs;
using BackgroundJobs.Internal;

namespace BackgroundJobs.UnitTests;

internal sealed class CountingClock : TimeProvider
{
    public static readonly DateTimeOffset Initial = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    public int Reads { get; private set; }
    public override DateTimeOffset GetUtcNow() => Initial.AddHours(Reads++);
}

public class PreparationTests
{
    private static (JobPreparation Preparation, CountingClock Clock) Setup()
    {
        var contracts = new JobContracts();
        contracts.Add(DemoJobs.LogMessage, DemoJobValidation.ValidateLogMessage);
        var clock = new CountingClock();
        return (new JobPreparation(contracts, clock), clock);
    }

    [Fact]
    public void Omitted_null_and_empty_options_use_spec_defaults()
    {
        var (preparation, _) = Setup();
        var args = new LogMessageArgs("hello");
        var prepared = new[]
        {
            preparation.Prepare(DemoJobs.LogMessage, args),
            preparation.Prepare(DemoJobs.LogMessage, args, null),
            preparation.Prepare(DemoJobs.LogMessage, args, new JobOptions())
        };
        Assert.All(prepared, job =>
        {
            Assert.Equal(3, job.MaxAttempts);
            Assert.Equal(1000, job.RetryDelayMs);
            Assert.Equal(JobState.Enqueued, job.State);
            Assert.Null(job.ScheduledAt);
            Assert.Equal(0, job.Attempts);
            Assert.Empty(job.AttemptHistory);
        });
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(10, 60000)]
    [InlineData(1, 60000)]
    [InlineData(10, 0)]
    public void Option_boundaries_are_accepted(int maxAttempts, int delay)
    {
        var (preparation, _) = Setup();
        var prepared = preparation.Prepare(DemoJobs.LogMessage, new LogMessageArgs("hello"),
            new JobOptions { MaxAttempts = maxAttempts, RetryDelayMs = delay });
        Assert.Equal(maxAttempts, prepared.MaxAttempts);
        Assert.Equal(delay, prepared.RetryDelayMs);
    }

    [Fact]
    public void Setting_one_option_preserves_other_defaults()
    {
        var (preparation, _) = Setup();
        var args = new LogMessageArgs("hello");
        var attempts = preparation.Prepare(DemoJobs.LogMessage, args, new JobOptions { MaxAttempts = 1 });
        var delay = preparation.Prepare(DemoJobs.LogMessage, args, new JobOptions { RetryDelayMs = 0 });
        var future = preparation.Prepare(DemoJobs.LogMessage, args,
            new JobOptions { RunAt = CountingClock.Initial.AddDays(1) });
        Assert.Equal(1000, attempts.RetryDelayMs);
        Assert.Equal(3, delay.MaxAttempts);
        Assert.Equal(3, future.MaxAttempts);
        Assert.Equal(1000, future.RetryDelayMs);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(int.MaxValue)]
    public void Invalid_attempt_limit_is_rejected(int value)
    {
        var (preparation, clock) = Setup();
        var error = Assert.Throws<JobValidationException>(() => preparation.Prepare(DemoJobs.LogMessage,
            new LogMessageArgs("hello"), new JobOptions { MaxAttempts = value }));
        Assert.Contains("maxAttempts", error.Message);
        Assert.Contains("1 e 10", error.Message);
        Assert.Equal(0, clock.Reads);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(60001)]
    [InlineData(int.MaxValue)]
    public void Invalid_delay_is_rejected_even_for_one_attempt(int value)
    {
        var (preparation, clock) = Setup();
        var error = Assert.Throws<JobValidationException>(() => preparation.Prepare(DemoJobs.LogMessage,
            new LogMessageArgs("hello"), new JobOptions { MaxAttempts = 1, RetryDelayMs = value }));
        Assert.Contains("retryDelayMs", error.Message);
        Assert.Contains("0 e 60000", error.Message);
        Assert.Equal(0, clock.Reads);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(864000000000)]
    public void Eligibility_and_all_creation_metadata_use_one_clock_read_with_tick_precision(long ticks)
    {
        var (preparation, clock) = Setup();
        var runAt = CountingClock.Initial.AddTicks(ticks).ToOffset(TimeSpan.FromHours(-3));
        var options = new JobOptions { RunAt = runAt };
        var job = preparation.Prepare(DemoJobs.LogMessage, new LogMessageArgs("hello"), options);

        Assert.Equal(1, clock.Reads);
        Assert.Equal(CountingClock.Initial, job.CreatedAt);
        Assert.Equal(job.CreatedAt, job.UpdatedAt);
        Assert.Equal(ticks > 0 ? JobState.Scheduled : JobState.Enqueued, job.State);
        Assert.Equal(ticks > 0 ? runAt.ToUniversalTime() : (DateTimeOffset?)null, job.ScheduledAt);
        if (job.ScheduledAt.HasValue) Assert.Equal(TimeSpan.Zero, job.ScheduledAt.Value.Offset);
        var history = Assert.Single(job.History);
        Assert.Equal(job.State, history.State);
        Assert.Equal(job.CreatedAt, history.At);
        Assert.False(string.IsNullOrWhiteSpace(history.Reason));
        Assert.Equal(runAt, options.RunAt);
        Assert.Equal(TimeSpan.FromHours(-3), options.RunAt!.Value.Offset);
    }

    [Fact]
    public void Reusing_options_keeps_each_prepared_creation_independent()
    {
        var (preparation, clock) = Setup();
        var options = new JobOptions { RunAt = CountingClock.Initial.AddMinutes(1), MaxAttempts = 5, RetryDelayMs = 0 };
        var first = preparation.Prepare(DemoJobs.LogMessage, new LogMessageArgs("first"), options);
        var second = preparation.Prepare(DemoJobs.LogMessage, new LogMessageArgs("second"), options);

        Assert.Equal(2, clock.Reads);
        Assert.Equal(JobState.Scheduled, first.State);
        Assert.Equal(JobState.Enqueued, second.State);
        Assert.Equal(CountingClock.Initial.AddHours(1), second.CreatedAt);
        Assert.Equal(CountingClock.Initial, first.CreatedAt);
        Assert.Equal(5, first.MaxAttempts);
        Assert.Equal(0, first.RetryDelayMs);
        Assert.Equal("first", first.Descriptor.Args.GetProperty("message").GetString());
    }
}
