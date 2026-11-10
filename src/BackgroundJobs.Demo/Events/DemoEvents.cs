namespace BackgroundJobs.Demo.Events;

public sealed record DemoEvent(string Type, string JobId, int Attempt, DateTimeOffset At, object? Detail);

public sealed class DemoEvents(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly List<DemoEvent> events = [];

    public void Add(string type, JobContext context, object? detail = null)
    {
        lock (gate) events.Add(new DemoEvent(type, context.JobId, context.Attempt, clock.GetUtcNow().ToUniversalTime(), detail));
    }

    public IReadOnlyList<DemoEvent> Snapshot() { lock (gate) return events.ToArray(); }
}
