namespace BackgroundJobs;

public interface IJobManager
{
    /// <summary>Returns null only when the ID does not exist. Reading requires no registered contract.</summary>
    Task<JobDetails?> GetAsync(string jobId);
    /// <summary>Returns every indexed job, without ordering or a global snapshot guarantee.</summary>
    Task<IReadOnlyList<JobSummary>> ListAsync();
    Task<JobOperationResult> DeleteAsync(string jobId);
    Task<JobOperationResult> RequeueAsync(string jobId);
}
