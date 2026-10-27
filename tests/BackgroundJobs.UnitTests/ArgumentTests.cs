using System.Runtime.CompilerServices;
using System.Text.Json;
using BackgroundJobs.Demo.Jobs;
using BackgroundJobs.Internal;

namespace BackgroundJobs.UnitTests;

public class ArgumentTests
{
    private static JobContracts LogContracts()
    {
        var contracts = new JobContracts();
        contracts.Add(DemoJobs.LogMessage, DemoJobValidation.ValidateLogMessage);
        return contracts;
    }

    public static TheoryData<string> ValidMessages => new()
    {
        "", "   ", "  Olá  ", "Aspas: \"A\"\nOutra linha\\", new string('a', 200),
        new string('á', 200), string.Concat(Enumerable.Repeat("😀", 100)),
        new string('a', 198) + "😀", string.Concat(Enumerable.Repeat("e\u0301", 100))
    };

    [Theory]
    [MemberData(nameof(ValidMessages))]
    public void Typed_preparation_and_revalidation_preserve_message(string message)
    {
        var contracts = LogContracts();
        var prepared = new JobPreparation(contracts, TimeProvider.System)
            .Prepare(DemoJobs.LogMessage, new LogMessageArgs(message));

        Assert.Equal("log-message", prepared.Descriptor.Name);
        Assert.Equal(message, prepared.Descriptor.Args.GetProperty("message").GetString());
        var restored = contracts.Get(DemoJobs.LogMessage).DeserializeAndValidate(prepared.Descriptor.Args);
        Assert.Equal(message, restored.Message);
        Assert.Single(prepared.Descriptor.Args.EnumerateObject());
    }

    public static TheoryData<string?> InvalidMessages => new()
    {
        null, new string('a', 201), string.Concat(Enumerable.Repeat("😀", 101)),
        new string('a', 199) + "😀", string.Concat(Enumerable.Repeat("e\u0301", 100)) + "a"
    };

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public void Invalid_typed_message_is_an_expected_rejection(string? message)
    {
        var error = Assert.Throws<JobValidationException>(() =>
            new JobPreparation(LogContracts(), TimeProvider.System)
                .Prepare(DemoJobs.LogMessage, new LogMessageArgs(message!)));
        Assert.Contains("message", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"message\":null}")]
    [InlineData("{\"message\":1}")]
    [InlineData("{\"message\":true}")]
    [InlineData("{\"message\":[]}")]
    [InlineData("{\"message\":{}}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"Message\":\"hello\"}")]
    [InlineData("{\"message\":\"hello\",\"color\":\"red\"}")]
    [InlineData("{\"message\":\"hello\",\"message\":\"hello\"}")]
    [InlineData("{\"message\":\"hello\",\"message\":\"other\"}")]
    [InlineData("{\"message\":\"hello\",\"mess\\u0061ge\":\"hello\"}")]
    public void Invalid_log_json_is_rejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        var error = Record.Exception(() => LogContracts().Get(DemoJobs.LogMessage)
            .DeserializeAndValidate(document.RootElement));
        Assert.True(error is JsonException or JobValidationException, error?.ToString());
    }

    [Fact]
    public void Escapes_are_decoded_without_changing_meaning()
    {
        using var document = JsonDocument.Parse("{\"message\":\"\\u0041\\nOlá\"}");
        var contract = LogContracts().Get(DemoJobs.LogMessage);
        var args = contract.DeserializeAndValidate(document.RootElement);
        Assert.Equal("A\nOlá", args.Message);
        Assert.Equal(args.Message, contract.Prepare(args).Args.GetProperty("message").GetString());
    }

    public sealed record NumberArgs(int Count);

    [Theory]
    [InlineData("3", true)]
    [InlineData("-3", true)]
    [InlineData("3.0", false)]
    [InlineData("3e0", false)]
    [InlineData("3.5", false)]
    [InlineData("\"3\"", false)]
    [InlineData("2147483648", false)]
    [InlineData("true", false)]
    [InlineData("null", false)]
    public void Integer_arguments_require_integer_tokens_within_type_range(string number, bool valid)
    {
        var job = new JobDefinition<NumberArgs>("number");
        var contracts = new JobContracts();
        contracts.Add(job);
        using var document = JsonDocument.Parse("{\"count\":" + number + "}");
        var error = Record.Exception(() => contracts.Get(job).DeserializeAndValidate(document.RootElement));
        if (valid) Assert.Null(error);
        else Assert.IsType<JsonException>(error);
    }

    public sealed record OptionalArgs(string? Note = null, int Count = 7);
    public sealed record NestedArgs(NumberArgs Child, Dictionary<string, int> Data);

    [Fact]
    public void Optional_fields_keep_contract_defaults_and_nullable_values()
    {
        var contract = new JobContract<OptionalArgs>("optional", null);
        using var document = JsonDocument.Parse("{}");
        Assert.Equal(new OptionalArgs(null, 7), contract.DeserializeAndValidate(document.RootElement));
        Assert.Equal(new OptionalArgs(null, 7), contract.DeserializeAndValidate(contract.Prepare(new OptionalArgs()).Args));
    }

    [Theory]
    [InlineData("{\"child\":{\"count\":1,\"count\":1},\"data\":{}}")]
    [InlineData("{\"child\":{\"count\":1,\"Count\":1},\"data\":{}}")]
    [InlineData("{\"child\":{\"count\":1},\"data\":{\"key\":1,\"key\":2}}")]
    public void Nested_unknown_fields_and_duplicates_are_rejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => new JobContract<NestedArgs>("nested", null)
            .DeserializeAndValidate(document.RootElement));
    }

    [Fact]
    public void Dictionary_keys_are_data_and_not_unknown_dto_fields()
    {
        using var document = JsonDocument.Parse("{\"child\":{\"count\":1},\"data\":{\"arbitrary key\":2}}");
        var args = new JobContract<NestedArgs>("nested", null).DeserializeAndValidate(document.RootElement);
        Assert.Equal(2, args.Data["arbitrary key"]);
    }

    public sealed record StructuredArgs(JsonElement Data);

    [Theory]
    [InlineData("{\"data\":{\"key\":1,\"key\":2}}")]
    [InlineData("{\"data\":[{\"key\":1,\"k\\u0065y\":1}]}")]
    public void Duplicate_rejection_also_applies_to_embedded_json(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => new JobContract<StructuredArgs>("structured", null)
            .DeserializeAndValidate(document.RootElement));
    }

    [Fact]
    public void Preparation_rejects_duplicates_inside_caller_supplied_json()
    {
        var job = new JobDefinition<StructuredArgs>("structured");
        var contracts = new JobContracts();
        contracts.Add(job);
        using var document = JsonDocument.Parse("{\"key\":1,\"key\":2}");
        Assert.Throws<JsonException>(() => new JobPreparation(contracts, TimeProvider.System)
            .Prepare(job, new StructuredArgs(document.RootElement)));
    }

    public sealed class MutableArgs
    {
        public List<string> Messages { get; set; } = [];
    }

    [Fact]
    public void Validator_receives_reconstructed_data_and_descriptor_uses_validated_representation()
    {
        var job = new JobDefinition<MutableArgs>("mutable");
        var original = new MutableArgs { Messages = ["before"] };
        MutableArgs? reconstructed = null;
        var contracts = new JobContracts();
        contracts.Add(job, args => { reconstructed = args; args.Messages.Add("validated"); });
        var prepared = new JobPreparation(contracts, TimeProvider.System).Prepare(job, original);

        Assert.NotSame(original, reconstructed);
        Assert.NotSame(original.Messages, reconstructed!.Messages);
        Assert.Single(original.Messages);
        original.Messages[0] = "caller changed";
        reconstructed.Messages.Clear();
        Assert.Equal(new[] { "before", "validated" }, prepared.Descriptor.Args.GetProperty("messages")
            .EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void Expected_rejection_preserves_exception_and_readable_message()
    {
        Assert.Equal(typeof(Exception), typeof(JobValidationException).BaseType);
        var rejection = new JobValidationException("Regra específica rejeitou o pedido.");
        var error = RunFailingValidator(rejection);
        Assert.Same(rejection, error);
        Assert.Equal("Regra específica rejeitou o pedido.", error.Message);
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("json")]
    [InlineData("null")]
    public void Unexpected_validator_exceptions_propagate_same_instance_with_original_stack(string kind)
    {
        Exception unexpected = kind switch
        {
            "argument" => new ArgumentException("validator defect"),
            "json" => new JsonException("validator defect"),
            _ => new NullReferenceException("validator defect")
        };
        var error = RunFailingValidator(unexpected);
        Assert.Same(unexpected, error);
        Assert.Equal("validator defect", error.Message);
        Assert.Contains(nameof(ThrowFromValidator), error.StackTrace);
    }

    private static Exception RunFailingValidator(Exception error)
    {
        var contracts = new JobContracts();
        var clock = new CountingClock();
        contracts.Add(DemoJobs.LogMessage, _ => ThrowFromValidator(error));
        var observed = Assert.ThrowsAny<Exception>(() => new JobPreparation(contracts, clock)
            .Prepare(DemoJobs.LogMessage, new LogMessageArgs("valid")));
        Assert.Equal(0, clock.Reads);
        return observed;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromValidator(Exception error) => throw error;
}
