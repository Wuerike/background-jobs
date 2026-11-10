using System.Text.Json;

namespace BackgroundJobs;

public sealed class JobHandlers
{
    private readonly JobContracts contracts;
    private readonly Dictionary<string, Delegate> handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement, JobContext, Task>> invokers = new(StringComparer.Ordinal);

    internal JobHandlers(JobContracts contracts) => this.contracts = contracts;

    public void Register<TArgs>(JobDefinition<TArgs> job, Func<TArgs, JobContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(handler);
        var contract = contracts.Get(job);
        EnsureNotRegistered(job.Name);
        handlers.Add(job.Name, handler);
        invokers.Add(job.Name, (args, context) => handler(contract.DeserializeAndValidate(args), context));
    }

    internal bool Contains(string name) => invokers.ContainsKey(name);

    internal Task InvokeAsync(string name, JsonElement args, JobContext context) =>
        invokers.TryGetValue(name, out var invoke) ? invoke(args, context) :
            throw new InvalidOperationException($"O handler de '{name}' não está registrado.");

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
