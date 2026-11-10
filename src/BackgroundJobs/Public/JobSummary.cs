namespace BackgroundJobs;

/// <summary>An immutable projection read atomically for one job.</summary>
public sealed record JobSummary(string Id, string Name, JobState State, int Attempts);

/// <summary>The confirmed outcome of a manual operation. Storage failures are exceptions.</summary>
public enum JobOperationResult { Applied, NotFound, Conflict }
