namespace BackgroundJobs.Internal;

internal sealed record NewJob(string Id, PreparedJob Prepared);

internal interface IJobStore
{
    Task CreateAsync(NewJob job);
    Task<JobDetails?> GetAsync(string id);
}
