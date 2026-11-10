namespace BackgroundJobs.Internal;

internal sealed record NewJob(string Id, PreparedJob Prepared);

internal interface IJobStore
{
    Task CreateAsync(NewJob job);
    Task<JobDetails?> GetAsync(string id);
    Task<IReadOnlyList<JobSummary>> ListAsync();
    Task<JobOperationResult> DeleteAsync(string id, TimeProvider clock);
    Task<JobOperationResult> RequeueAsync(string id, TimeProvider clock);
}
