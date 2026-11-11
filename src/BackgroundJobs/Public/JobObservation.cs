namespace BackgroundJobs;

/// <summary>A compact per-job snapshot, without arguments or unbounded histories.</summary>
public sealed record JobMonitorItem(string Id, string Name, JobState State, int Attempts,
    int MaxAttempts, int RetryDelayMs, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? ScheduledAt, JobAttempt? LastAttempt)
{
    public bool IsRetry => State == JobState.Scheduled && Attempts > 0;
}

public enum JobMonitorOrder { Newest, Oldest, Updated, Name }

/// <summary>Creation interval is inclusive at From and exclusive at Until, in UTC.</summary>
public sealed record JobMonitorQuery(JobState? State = null, string? Search = null,
    DateTimeOffset? From = null, DateTimeOffset? Until = null,
    JobMonitorOrder Order = JobMonitorOrder.Newest, int Page = 1, int PageSize = 25)
{
    internal void Validate()
    {
        if (State is { } state && !Enum.IsDefined(state) || !Enum.IsDefined(Order) ||
            Page < 1 || PageSize is < 1 or > 100 || Search?.Length > 200 ||
            From is { } from && Until is { } until && from >= until)
            throw new ArgumentException("Invalid monitoring filters or page bounds.");
    }
}

public sealed record JobMonitorPage(IReadOnlyList<JobMonitorItem> Items, int Total, int Page, int PageSize);

/// <summary>Observations are atomic per job, not a global snapshot. Items may change during the interval.</summary>
public sealed class JobObservation
{
    public JobObservation(DateTimeOffset startedAt, DateTimeOffset finishedAt, IEnumerable<JobMonitorItem> items)
    {
        StartedAt = startedAt.ToUniversalTime();
        FinishedAt = finishedAt.ToUniversalTime();
        Items = Array.AsReadOnly(items.ToArray());
        Counts = new System.Collections.ObjectModel.ReadOnlyDictionary<JobState, int>(
            Enum.GetValues<JobState>().ToDictionary(state => state, state => Items.Count(item => item.State == state)));
    }

    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset FinishedAt { get; }
    public IReadOnlyList<JobMonitorItem> Items { get; }
    public IReadOnlyDictionary<JobState, int> Counts { get; }
    public int Retries => Items.Count(item => item.IsRetry);

    public JobMonitorPage Query(JobMonitorQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var filtered = Items.Where(item =>
            (query.State is null || item.State == query.State) &&
            (string.IsNullOrWhiteSpace(query.Search) || item.Id.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase)) &&
            (query.From is null || item.CreatedAt >= query.From) &&
            (query.Until is null || item.CreatedAt < query.Until));
        var ordered = query.Order switch
        {
            JobMonitorOrder.Oldest => filtered.OrderBy(item => item.CreatedAt),
            JobMonitorOrder.Updated => filtered.OrderByDescending(item => item.UpdatedAt),
            JobMonitorOrder.Name => filtered.OrderBy(item => item.Name, StringComparer.Ordinal),
            _ => filtered.OrderByDescending(item => item.CreatedAt)
        };
        var all = ordered.ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var page = Math.Min(query.Page, Math.Max(1, (all.Length + query.PageSize - 1) / query.PageSize));
        return new(Array.AsReadOnly(all.Skip((page - 1) * query.PageSize).Take(query.PageSize).ToArray()),
            all.Length, page, query.PageSize);
    }
}
