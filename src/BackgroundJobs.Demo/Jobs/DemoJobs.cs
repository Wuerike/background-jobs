namespace BackgroundJobs.Demo.Jobs;

public static class DemoJobs
{
    public static readonly JobDefinition<LogMessageArgs> LogMessage = new("log-message");
}
