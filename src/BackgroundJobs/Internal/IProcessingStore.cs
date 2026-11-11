namespace BackgroundJobs.Internal;

internal sealed record JobCandidate(JobDetails Job, string Snapshot, string Cycle, double Position);
internal sealed record ClaimedJob(JobDetails Job, int Attempt, string Cycle, string Token, DateTimeOffset StartedAt);
internal sealed record AttemptCompletion(ClaimedJob Claim, DateTimeOffset FinishedAt, string? Error);
internal sealed record ScheduledCandidate(JobDetails Job, string Snapshot, double Score);
internal sealed class ScheduledBatch(IList<ScheduledCandidate> candidates, int? examinedCount = null)
    : System.Collections.ObjectModel.ReadOnlyCollection<ScheduledCandidate>(candidates)
{
    public int ExaminedCount { get; } = examinedCount ?? candidates.Count;
}
internal enum PromotionResult { Promoted, LostRace }
internal sealed record RecoveryCandidate(JobDetails Job, string Snapshot);
internal enum RecoveryResult { Recovered, AlreadyAdvanced }
internal sealed record JobReference(string Id, string Name, JobState State);

internal interface IProcessingStore
{
    Task BeginSessionAsync() => Task.CompletedTask;
    Task ValidateSessionAsync() => Task.CompletedTask;
    Task ValidateProcessingIndexAsync() => Task.CompletedTask;
    IAsyncEnumerable<JobReference> ReadReferencesAsync();
    Task<bool> HasProcessingAsync();
    Task<IReadOnlyList<RecoveryCandidate>> ReadRecoveryAsync();
    Task<RecoveryResult> TryRecoverAsync(RecoveryCandidate candidate, DateTimeOffset now);
    Task<RecoveryResult> TryRecoverAsync(RecoveryCandidate candidate, DateTimeOffset now, string operation) => TryRecoverAsync(candidate, now);
    Task<PromotionResult> TryPromoteAsync(ScheduledCandidate candidate, DateTimeOffset now, string operation) => TryPromoteAsync(candidate, now);
    Task<ScheduledBatch> ReadScheduledAsync(DateTimeOffset now, long offset);
    Task<PromotionResult> TryPromoteAsync(ScheduledCandidate candidate, DateTimeOffset now);
    Task<JobCandidate?> PeekAsync();
    Task<ClaimedJob?> TryClaimAsync(JobCandidate candidate, DateTimeOffset startedAt, string token);
    Task CompleteAsync(AttemptCompletion completion);
}
