using BackgroundJobs.Internal;

namespace BackgroundJobs.UnitTests;

public class RegistryTests
{
    private static readonly JobDefinition<Args> Job = new("test");
    public sealed record Args(string Message);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    public void Definition_rejects_missing_names(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new JobDefinition<Args>(name!));
    }

    [Fact]
    public void Names_are_ordinal_and_are_not_trimmed()
    {
        var contracts = new JobContracts();
        contracts.Add(Job);
        contracts.Add(new JobDefinition<Args>("Test"));
        contracts.Add(new JobDefinition<Args>(" test "));

        Assert.NotSame(contracts.Get("test"), contracts.Get("Test"));
        Assert.NotSame(contracts.Get("test"), contracts.Get(" test "));
        Assert.Throws<InvalidOperationException>(() => contracts.Get("TEST"));
    }

    [Fact]
    public void Duplicate_contract_does_not_replace_type_or_validator()
    {
        var contracts = new JobContracts();
        var calls = 0;
        contracts.Add(Job, _ => calls++);

        Assert.Throws<InvalidOperationException>(() => contracts.Add(Job));
        Assert.Throws<InvalidOperationException>(() => contracts.Add(new JobDefinition<int>(Job.Name)));
        Assert.Equal(0, calls);
        contracts.Get(Job).Prepare(new Args("hello"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Preparation_requires_known_name_and_exact_registered_type()
    {
        var registry = new JobRegistry();
        var preparation = new JobPreparation(registry.Contracts, TimeProvider.System);
        Assert.Contains("não está registrado", Assert.Throws<InvalidOperationException>(
            () => preparation.Prepare(Job, new Args("hello"))).Message);

        registry.Contracts.Add(Job);
        Assert.Contains("incompatível", Assert.Throws<InvalidOperationException>(
            () => preparation.Prepare(new JobDefinition<int>(Job.Name), 42)).Message);
        var prepared = preparation.Prepare(new JobDefinition<Args>(Job.Name), new Args("hello"));
        Assert.Equal(Job.Name, prepared.Descriptor.Name);
    }

    [Fact]
    public void Contracts_can_be_used_without_handlers_or_validator()
    {
        var registry = new JobRegistry();
        registry.Contracts.Add(Job);

        var prepared = new JobPreparation(registry.Contracts, TimeProvider.System).Prepare(Job, new Args("hello"));

        Assert.Equal("hello", prepared.Descriptor.Args.GetProperty("message").GetString());
        Assert.Throws<InvalidOperationException>(() => registry.Handlers.Get(Job));
    }

    [Fact]
    public async Task Separate_handler_registration_requires_matching_contract_and_preserves_first_handler()
    {
        var registry = new JobRegistry();
        var calls = 0;
        Func<Args, JobContext, Task> handler = (_, _) => { calls++; return Task.CompletedTask; };

        Assert.Throws<InvalidOperationException>(() => registry.Handlers.Register(Job, handler));
        registry.Contracts.Add(Job);
        Assert.Throws<InvalidOperationException>(() => registry.Handlers.Register(
            new JobDefinition<int>(Job.Name), (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => registry.Handlers.Register(Job, null!));
        registry.Handlers.Register(Job, handler);
        Assert.Throws<InvalidOperationException>(() => registry.Handlers.Register(Job, (_, _) => Task.CompletedTask));
        Assert.Equal(0, calls);

        await registry.Handlers.Get(Job)(new Args("hello"), new JobContext("id", 1));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Combined_registration_shares_catalogs_and_does_not_invoke_delegates()
    {
        var registry = new JobRegistry();
        var validations = 0;
        var executions = 0;
        registry.Register(Job, (_, _) => { executions++; return Task.CompletedTask; }, _ => validations++);

        Assert.Equal(0, validations);
        Assert.Equal(0, executions);
        Assert.NotNull(registry.Handlers.Get(Job));
        new JobPreparation(registry.Contracts, TimeProvider.System).Prepare(Job, new Args("hello"));
        Assert.Equal(1, validations);
        Assert.Equal(0, executions);
        Assert.Throws<InvalidOperationException>(() => registry.Contracts.Add(Job));
        Assert.Throws<InvalidOperationException>(() => registry.Handlers.Register(Job, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public void Invalid_combined_registration_leaves_no_partial_registration()
    {
        var registry = new JobRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Register(Job, null!));
        Assert.Throws<InvalidOperationException>(() => registry.Contracts.Get(Job));
        registry.Register(Job, (_, _) => Task.CompletedTask);

        Assert.Throws<InvalidOperationException>(() => registry.Register(Job, (_, _) => Task.CompletedTask));
        Assert.NotNull(registry.Handlers.Get(Job));
    }

    [Fact]
    public void Combined_registration_does_not_add_handler_to_existing_contract()
    {
        var registry = new JobRegistry();
        registry.Contracts.Add(Job);
        Assert.Throws<InvalidOperationException>(() => registry.Register(Job, (_, _) => Task.CompletedTask));
        Assert.Throws<InvalidOperationException>(() => registry.Handlers.Get(Job));
        registry.Handlers.Register(Job, (_, _) => Task.CompletedTask);
    }
}
