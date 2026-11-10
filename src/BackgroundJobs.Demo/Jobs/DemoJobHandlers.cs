using BackgroundJobs.Demo.Events;

namespace BackgroundJobs.Demo.Jobs;

public sealed class DemoJobHandlers(DemoEvents events, TimeProvider? timeProvider = null)
{
    public void Register(JobRegistry registry)
    {
        registry.Register(DemoJobs.LogMessage, LogMessageAsync, DemoJobValidation.ValidateLogMessage);
        registry.Register(DemoJobs.SucceedAfter, SucceedAfterAsync, DemoJobValidation.ValidateSucceedAfter);
        registry.Register(DemoJobs.FailNTimes, FailNTimesAsync, DemoJobValidation.ValidateFailNTimes);
    }

    public Task LogMessageAsync(LogMessageArgs args, JobContext context) => Observe(context, () =>
    {
        events.Add("message", context, new { message = args.Message });
        return Task.CompletedTask;
    });

    public Task SucceedAfterAsync(SucceedAfterArgs args, JobContext context) => Observe(context,
        () => Task.Delay(TimeSpan.FromMilliseconds(args.DelayMs), timeProvider ?? TimeProvider.System));

    public Task FailNTimesAsync(FailNTimesArgs args, JobContext context) => Observe(context, () =>
    {
        if (context.Attempt <= args.FailTimes) throw new InvalidOperationException("demo failure");
        return Task.CompletedTask;
    });

    private async Task Observe(JobContext context, Func<Task> execute)
    {
        events.Add("started", context);
        try
        {
            await execute();
        }
        catch
        {
            events.Add("finished", context, new { outcome = "error" });
            throw;
        }
        events.Add("finished", context, new { outcome = "ok" });
    }
}
