namespace BackgroundJobs.Dashboard;

public sealed class DashboardOptions
{
    /// <summary>URL path owned by the dashboard in the consumer application.</summary>
    public string Path { get; set; } = "/dashboard";
    /// <summary>Display label only; never put credentials here.</summary>
    public string StorageLabel { get; set; } = "background-jobs";
    public int MaxJobs { get; set; } = 10_000;
    /// <summary>Optional ASP.NET Core authorization policy, checked before reads and mutations.</summary>
    public string? AuthorizationPolicy { get; set; }
}
