using System.Text.Json;

namespace BackgroundJobs;

public enum JobState
{
    Scheduled,
    Enqueued,
    Processing,
    Succeeded,
    Failed,
    Deleted
}

public enum JobAttemptOutcome
{
    Succeeded,
    Failed,
    Interrupted
}

public sealed record JobStateChange
{
    public JobStateChange(JobState state, DateTimeOffset at, string reason)
    {
        State = state;
        At = at.ToUniversalTime();
        Reason = reason;
    }

    public JobState State { get; }
    public DateTimeOffset At { get; }
    public string Reason { get; }
}

public sealed record JobAttempt
{
    public JobAttempt(int attempt, DateTimeOffset startedAt, DateTimeOffset? finishedAt,
        JobAttemptOutcome? outcome, string? error)
    {
        Attempt = attempt;
        StartedAt = startedAt.ToUniversalTime();
        FinishedAt = finishedAt?.ToUniversalTime();
        Outcome = outcome;
        Error = error;
    }

    public int Attempt { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? FinishedAt { get; }
    public JobAttemptOutcome? Outcome { get; }
    public string? Error { get; }
}

/// <summary>An independent, immutable snapshot of a job and its chronological histories.</summary>
public sealed class JobDetails
{
    public JobDetails(string id, string name, JsonElement args, JobState state, int attempts,
        int maxAttempts, int retryDelayMs, DateTimeOffset? scheduledAt, DateTimeOffset createdAt,
        DateTimeOffset updatedAt, IReadOnlyList<JobStateChange> history,
        IReadOnlyList<JobAttempt> attemptHistory)
    {
        Id = id;
        Name = name;
        Args = args.Clone();
        State = state;
        Attempts = attempts;
        MaxAttempts = maxAttempts;
        RetryDelayMs = retryDelayMs;
        ScheduledAt = scheduledAt?.ToUniversalTime();
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
        History = Array.AsReadOnly(history.ToArray());
        AttemptHistory = Array.AsReadOnly(attemptHistory.ToArray());
    }

    public string Id { get; }
    public string Name { get; }
    public JsonElement Args { get; }
    public JobState State { get; }
    public int Attempts { get; }
    public int MaxAttempts { get; }
    public int RetryDelayMs { get; }
    public DateTimeOffset? ScheduledAt { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public IReadOnlyList<JobStateChange> History { get; }
    public IReadOnlyList<JobAttempt> AttemptHistory { get; }
}
