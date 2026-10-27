namespace BackgroundJobs;

/// <summary>Configure contracts and handlers before using the registry; keep it stable during processing.</summary>
public sealed class JobRegistry
{
    public JobRegistry()
    {
        Contracts = new JobContracts();
        Handlers = new JobHandlers(Contracts);
    }

    public JobContracts Contracts { get; }
    public JobHandlers Handlers { get; }

    /// <summary>Adds a new contract and its handler together, without invoking either delegate.</summary>
    public void Register<TArgs>(JobDefinition<TArgs> job, Func<TArgs, JobContext, Task> handler,
        Action<TArgs>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(handler);
        Contracts.EnsureNotRegistered(job.Name);
        Handlers.EnsureNotRegistered(job.Name);

        Contracts.Add(job, validate);
        Handlers.Register(job, handler);
    }
}
