namespace BackgroundJobs;

public interface IJobClient
{
    /// <summary>Returns the generated ID only after persistence is confirmed.</summary>
    Task<string> EnqueueAsync<TArgs>(
        JobDefinition<TArgs> job,
        TArgs args,
        JobOptions? options = null);
}
