namespace BackgroundJobs;

/// <summary>A stable contract name associated with its argument type.</summary>
public sealed record JobDefinition<TArgs>
{
    public JobDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
}
