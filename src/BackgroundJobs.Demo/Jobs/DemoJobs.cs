namespace BackgroundJobs.Demo.Jobs;

public static class DemoJobs
{
    public static readonly JobDefinition<SucceedAfterArgs> SucceedAfter = new("succeed-after");
    public static readonly JobDefinition<FailNTimesArgs> FailNTimes = new("fail-n-times");
    public static readonly JobDefinition<LogMessageArgs> LogMessage = new("log-message");
}
