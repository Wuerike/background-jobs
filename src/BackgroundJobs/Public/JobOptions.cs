namespace BackgroundJobs;

public sealed record JobOptions
{
    public DateTimeOffset? RunAt { get; init; }
    public int MaxAttempts { get; init; } = 3;
    public int RetryDelayMs { get; init; } = 1000;
}
