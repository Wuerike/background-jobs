namespace BackgroundJobs;

public sealed class JobHandlers
{
    private readonly JobContracts contracts;
    private readonly Dictionary<string, Delegate> handlers = new(StringComparer.Ordinal);

    internal JobHandlers(JobContracts contracts) => this.contracts = contracts;

    public void Register<TArgs>(JobDefinition<TArgs> job, Func<TArgs, JobContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(handler);
        contracts.Get(job);
        EnsureNotRegistered(job.Name);
        handlers.Add(job.Name, handler);
    }

    internal void EnsureNotRegistered(string name)
    {
        if (handlers.ContainsKey(name))
        {
            throw new InvalidOperationException($"O handler de '{name}' já está registrado.");
        }
    }

    internal Func<TArgs, JobContext, Task> Get<TArgs>(JobDefinition<TArgs> job)
    {
        contracts.Get(job);
        return handlers.TryGetValue(job.Name, out var handler)
            ? (Func<TArgs, JobContext, Task>)handler
            : throw new InvalidOperationException($"O handler de '{job.Name}' não está registrado.");
    }
}
