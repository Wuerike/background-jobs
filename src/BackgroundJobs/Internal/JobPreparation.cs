using System.Text.Json;

namespace BackgroundJobs.Internal;

internal sealed record JobDescriptor(string Name, JsonElement Args);

internal sealed record PreparedJob(
    JobDescriptor Descriptor,
    int MaxAttempts,
    int RetryDelayMs,
    JobState State,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<JobStateChange> History)
{
    public int Attempts => 0;
    public IReadOnlyList<JobAttempt> AttemptHistory => Array.Empty<JobAttempt>();
}

internal sealed class JobPreparation(JobContracts contracts, TimeProvider timeProvider)
{
    public PreparedJob Prepare<TArgs>(JobDefinition<TArgs> job, TArgs args, JobOptions? options = null)
    {
        options ??= new JobOptions();
        if (options.MaxAttempts is < 1 or > 10)
        {
            throw new JobValidationException("maxAttempts deve ser um inteiro entre 1 e 10.");
        }

        if (options.RetryDelayMs is < 0 or > 60_000)
        {
            throw new JobValidationException("retryDelayMs deve ser um inteiro entre 0 e 60000.");
        }

        var descriptor = contracts.Get(job).Prepare(args);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var runAt = options.RunAt?.ToUniversalTime();
        var scheduledAt = runAt > now ? runAt : null;
        var state = scheduledAt.HasValue ? JobState.Scheduled : JobState.Enqueued;

        return new PreparedJob(descriptor, options.MaxAttempts, options.RetryDelayMs, state,
            scheduledAt, now, now,
            Array.AsReadOnly(new[] { new JobStateChange(state, now, "Job criado.") }));
    }
}
