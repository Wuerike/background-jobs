using BackgroundJobs.Internal;

namespace BackgroundJobs;

public sealed class JobContracts
{
    private readonly Dictionary<string, IJobContract> contracts = new(StringComparer.Ordinal);

    public void Add<TArgs>(JobDefinition<TArgs> job, Action<TArgs>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        EnsureNotRegistered(job.Name);
        contracts.Add(job.Name, new JobContract<TArgs>(job.Name, validate));
    }

    internal void EnsureNotRegistered(string name)
    {
        if (contracts.ContainsKey(name))
        {
            throw new InvalidOperationException($"O contrato '{name}' já está registrado.");
        }
    }

    internal IJobContract Get(string name) => contracts.TryGetValue(name, out var contract)
        ? contract
        : throw new InvalidOperationException($"O contrato '{name}' não está registrado.");

    internal JobContract<TArgs> Get<TArgs>(JobDefinition<TArgs> job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Get(job.Name) as JobContract<TArgs>
            ?? throw new InvalidOperationException($"O tipo dos argumentos de '{job.Name}' é incompatível com o contrato registrado.");
    }
}
