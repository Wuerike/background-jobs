namespace BackgroundJobs;

/// <summary>An expected rejection whose message can be shown to the caller.</summary>
public sealed class JobValidationException(string message) : Exception(message);
