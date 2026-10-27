namespace BackgroundJobs;

public sealed record JobContext(string JobId, int Attempt);
