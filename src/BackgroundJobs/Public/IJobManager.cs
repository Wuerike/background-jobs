namespace BackgroundJobs;

public interface IJobManager
{
    /// <summary>Returns null only when the ID does not exist. Reading requires no registered contract.</summary>
    Task<JobDetails?> GetAsync(string jobId);
}
