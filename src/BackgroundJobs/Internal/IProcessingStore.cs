namespace BackgroundJobs.Internal;

internal sealed record JobCandidate(JobDetails Job, string Snapshot, string Cycle);
internal sealed record ClaimedJob(JobDetails Job, int Attempt, string Cycle, string Token, DateTimeOffset StartedAt);
internal sealed record AttemptCompletion(ClaimedJob Claim, DateTimeOffset FinishedAt, string? Error);
internal sealed record JobReference(string Id, string Name, JobState State);

internal interface IProcessingStore
{
    IAsyncEnumerable<JobReference> ReadReferencesAsync();
    Task<bool> HasProcessingAsync();
    Task<JobCandidate?> PeekAsync();
    Task<ClaimedJob?> TryClaimAsync(JobCandidate candidate, DateTimeOffset startedAt, string token);
    Task CompleteAsync(AttemptCompletion completion);
}
