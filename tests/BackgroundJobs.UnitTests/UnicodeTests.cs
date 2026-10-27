using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BackgroundJobs.Demo.Jobs;
using BackgroundJobs.Internal;

namespace BackgroundJobs.UnitTests;

public class UnicodeTests
{
    // Keep these values in the test body: attribute/test-case serialization can replace lone surrogates.
    private static readonly string[] MalformedStrings =
        ["\uD800", "\uDC00", "A\uD800B", "A\uDC00B", "\uDC00\uD800", "\uD800\uD800", "\uDC00\uDC00"];

    [Fact]
    public void Typed_message_rejects_malformed_utf16_before_validator_handler_or_clock()
    {
        var validations = 0;
        var executions = 0;
        var registry = new JobRegistry();
        registry.Register(DemoJobs.LogMessage,
            (_, _) => { executions++; return Task.CompletedTask; },
            args => { validations++; DemoJobValidation.ValidateLogMessage(args); });
        var clock = new CountingClock();
        var preparation = new JobPreparation(registry.Contracts, clock);

        foreach (var message in MalformedStrings)
        {
            var error = Assert.Throws<JobValidationException>(() =>
                preparation.Prepare(DemoJobs.LogMessage, new LogMessageArgs(message)));
            Assert.Contains("UTF-16", error.Message);
        }

        Assert.Equal(0, validations);
        Assert.Equal(0, executions);
        Assert.Equal(0, clock.Reads);
    }

    public sealed record NestedTextArgs(List<Dictionary<string, string?>> Items);
    public sealed record JsonArgs(JsonElement Data);
    public sealed record NodeArgs(JsonNode? Data);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Json_nodes_reject_malformed_text_and_keys_in_both_serializations(bool malformedKey, bool validatorAddsText)
    {
        var job = new JobDefinition<NodeArgs>("nodes");
        foreach (var malformed in MalformedStrings)
        {
            JsonObject CreateObject(string text) => new()
                { [malformedKey ? text : "text"] = malformedKey ? "value" : text };
            var validations = 0;
            var executions = 0;
            var registry = new JobRegistry();
            registry.Register(job, (_, _) => { executions++; return Task.CompletedTask; }, args =>
            {
                validations++;
                if (validatorAddsText) args.Data!.AsArray()[0] = CreateObject(malformed);
            });
            var original = new NodeArgs(new JsonArray(CreateObject(validatorAddsText ? "valid" : malformed)));
            var clock = new CountingClock();

            var error = Assert.Throws<JobValidationException>(() =>
                new JobPreparation(registry.Contracts, clock).Prepare(job, original));

            Assert.Contains("UTF-16", error.Message);
            Assert.Equal(validatorAddsText ? 1 : 0, validations);
            Assert.Equal(0, executions);
            Assert.Equal(0, clock.Reads);
            if (validatorAddsText) Assert.Equal("valid", malformedKey
                ? Assert.Single(original.Data![0]!.AsObject()).Key
                : original.Data![0]!["text"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Malformed_nodes_are_rejected_for_concrete_base_and_object_contracts()
    {
        foreach (var malformed in MalformedStrings)
        {
            VerifyNodeRejection<JsonObject>(new() { [malformed] = 1 });
            VerifyNodeRejection<JsonArray>(new(JsonValue.Create(malformed)));
            VerifyNodeRejection<JsonValue>(JsonValue.Create(malformed)!);
            VerifyNodeRejection<JsonNode>(JsonValue.Create(malformed)!);
            VerifyNodeRejection<object>(JsonValue.Create(malformed)!);
            VerifyNodeRejection(new Dictionary<string, object?> { ["node"] = new JsonObject { ["text"] = malformed } });
            VerifyNodeRejection(new ExtensionArgs<IDictionary<string, object?>>
                { Extra = new Dictionary<string, object?> { ["node"] = new JsonObject { [malformed] = 1 } } });
        }
    }

    private static void VerifyNodeRejection<TArgs>(TArgs args)
    {
        var job = new JobDefinition<TArgs>("node-root");
        var contracts = new JobContracts();
        var validations = 0;
        contracts.Add(job, _ => validations++);
        var clock = new CountingClock();

        Assert.Throws<JobValidationException>(() => new JobPreparation(contracts, clock).Prepare(job, args));
        Assert.Equal(0, validations);
        Assert.Equal(0, clock.Reads);
    }

    [Fact]
    public void Valid_nodes_preserve_text_types_nulls_and_independent_validated_data()
    {
        var texts = new[] { "", "  Olá  ", "😀", "e\u0301", "é", "\uFFFD", "\0", "\"\\\n" };
        var job = new JobDefinition<NodeArgs>("nodes");
        var contracts = new JobContracts();
        JsonArray? retained = null;
        contracts.Add(job, args =>
        {
            retained = args.Data!.AsArray();
            retained.Add(new JsonObject { ["validated"] = "😀\uFFFD" });
        });
        var original = new JsonArray(texts.Select(text => (JsonNode)new JsonObject { [text] = text }).ToArray());
        original.Add(null);
        original.Add(42);
        original.Add(true);
        var prepared = new JobPreparation(contracts, TimeProvider.System).Prepare(job, new NodeArgs(original));
        original.Clear();
        retained!.Clear();
        var data = prepared.Descriptor.Args.GetProperty("data");

        for (var i = 0; i < texts.Length; i++) Assert.Equal(texts[i], data[i].GetProperty(texts[i]).GetString());
        Assert.Equal(JsonValueKind.Null, data[texts.Length].ValueKind);
        Assert.Equal(42, data[texts.Length + 1].GetInt32());
        Assert.True(data[texts.Length + 2].GetBoolean());
        Assert.Equal("😀\uFFFD", data[texts.Length + 3].GetProperty("validated").GetString());

        var nullableContracts = new JobContracts();
        nullableContracts.Add(job);
        var empty = new JobPreparation(nullableContracts, TimeProvider.System).Prepare(job, new NodeArgs(null));
        Assert.Equal(JsonValueKind.Null, empty.Descriptor.Args.GetProperty("data").ValueKind);
    }

    [Theory]
    [InlineData("{\"data\":{\"key\":1,\"k\\u0065y\":2}}")]
    [InlineData("{\"data\":[{\"key\":1,\"key\":1}]}")]
    public void Node_deserialization_preserves_duplicate_rejection(string json)
    {
        using var document = JsonDocument.Parse(json);
        // The native JsonNode reader rejects duplicates with ArgumentException in this runtime.
        Assert.Throws<ArgumentException>(() => new JobContract<NodeArgs>("nodes", null)
            .DeserializeAndValidate(document.RootElement));
    }

    [Fact]
    public void Concrete_node_contracts_round_trip_their_native_shapes()
    {
        VerifyNodeRoundTrip<JsonObject>(new() { ["😀"] = "\uFFFD" });
        VerifyNodeRoundTrip<JsonArray>(new(1, "😀", null));
        VerifyNodeRoundTrip<JsonValue>(JsonValue.Create("😀\uFFFD")!);
        VerifyNodeRoundTrip<JsonValue>(JsonValue.Create(42)!);
        VerifyNodeRoundTrip<JsonValue>(JsonValue.Create(true)!);
        VerifyNodeRoundTrip<JsonNode>(new JsonObject { ["text"] = "e\u0301" });
    }

    private static void VerifyNodeRoundTrip<TNode>(TNode node) where TNode : JsonNode
    {
        var job = new JobDefinition<TNode>("node-root");
        var contracts = new JobContracts();
        contracts.Add(job);
        var prepared = new JobPreparation(contracts, TimeProvider.System).Prepare(job, node);
        var restored = contracts.Get(job).DeserializeAndValidate(prepared.Descriptor.Args);
        Assert.IsAssignableFrom<TNode>(restored);
        Assert.True(JsonNode.DeepEquals(node, restored));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public void Concrete_node_contracts_reject_incompatible_json_shapes(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => new JobContract<JsonObject>("object", null)
            .DeserializeAndValidate(document.RootElement));
    }

    [Theory]
    [InlineData(63, true)]
    [InlineData(64, false)]
    [InlineData(1000, false)]
    public void Node_validation_preserves_json_depth_boundaries(int depth, bool valid)
    {
        JsonNode node = JsonValue.Create("text")!;
        for (var i = 0; i < depth; i++) node = new JsonArray(node);
        var job = new JobDefinition<NodeArgs>("nodes");
        var contracts = new JobContracts();
        var validations = 0;
        contracts.Add(job, _ => validations++);
        var clock = new CountingClock();

        var error = Record.Exception(() => new JobPreparation(contracts, clock).Prepare(job, new NodeArgs(node)));
        if (valid) Assert.Null(error);
        else Assert.IsType<JsonException>(error);
        Assert.Equal(valid ? 1 : 0, validations);
        Assert.Equal(valid ? 1 : 0, clock.Reads);
    }

    [Fact]
    public void String_contracts_reject_malformed_utf16_without_requiring_an_object()
    {
        var job = new JobDefinition<string>("text");
        var contracts = new JobContracts();
        contracts.Add(job);
        var preparation = new JobPreparation(contracts, TimeProvider.System);

        foreach (var malformed in MalformedStrings)
            Assert.Throws<JobValidationException>(() => preparation.Prepare(job, malformed));

        var valid = preparation.Prepare(job, "😀\uFFFD");
        Assert.Equal("😀\uFFFD", valid.Descriptor.Args.GetString());
    }

    [Fact]
    public void Embedded_json_rejects_malformed_escaped_text_and_keys_before_validation()
    {
        var job = new JobDefinition<JsonArgs>("structured");
        var contracts = new JobContracts();
        var validations = 0;
        contracts.Add(job, _ => validations++);
        var clock = new CountingClock();
        var preparation = new JobPreparation(contracts, clock);

        foreach (var json in new[] { "\"\\uD800\"", "{\"\\uDC00\":1}", "[\"\\uDC00\"]" })
        {
            using var document = JsonDocument.Parse(json);
            Assert.Throws<JsonException>(() => preparation.Prepare(job,
                new JsonArgs(document.RootElement)));
        }

        Assert.Equal(0, validations);
        Assert.Equal(0, clock.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nested_dictionary_rejects_malformed_values_and_keys(bool malformedKey)
    {
        var job = new JobDefinition<NestedTextArgs>("nested-text");
        var contracts = new JobContracts();
        contracts.Add(job);
        var clock = new CountingClock();
        var preparation = new JobPreparation(contracts, clock);

        foreach (var malformed in MalformedStrings)
        {
            var args = new NestedTextArgs([new()
            {
                [malformedKey ? malformed : "key"] = malformedKey ? "value" : malformed
            }]);
            var error = Assert.Throws<JobValidationException>(() => preparation.Prepare(job, args));
            Assert.Contains("UTF-16", error.Message);
        }

        Assert.Equal(0, clock.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Final_serialization_rejects_malformed_values_and_keys_added_by_validator(bool malformedKey)
    {
        var job = new JobDefinition<NestedTextArgs>("nested-text");
        foreach (var malformed in MalformedStrings)
        {
            var validations = 0;
            var contracts = new JobContracts();
            contracts.Add(job, args =>
            {
                validations++;
                args.Items[0][malformedKey ? malformed : "key"] = malformedKey ? "value" : malformed;
            });
            var clock = new CountingClock();
            var original = new NestedTextArgs([new() { ["key"] = "original" }]);
            var error = Assert.Throws<JobValidationException>(() =>
                new JobPreparation(contracts, clock).Prepare(job, original));

            Assert.Contains("UTF-16", error.Message);
            Assert.Equal(1, validations);
            Assert.Equal(0, clock.Reads);
            Assert.Equal("original", Assert.Single(original.Items[0]).Value);
        }
    }

    public sealed class ExtensionArgs<TData>
    {
        [JsonExtensionData]
        public TData? Extra { get; set; }
    }

    public sealed record NestedExtensionArgs<TData>(List<ExtensionArgs<TData>> Items);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extension_data_rejects_malformed_keys_in_both_serializations(bool validatorAddsKey)
    {
        VerifyMalformedExtensionKeys(key => new Dictionary<string, JsonElement>
            { [key] = JsonSerializer.SerializeToElement(1) }, validatorAddsKey);
        VerifyMalformedExtensionKeys<IDictionary<string, object?>>(key => new Dictionary<string, object?>
            { [key] = 1 }, validatorAddsKey);
    }

    private static void VerifyMalformedExtensionKeys<TData>(Func<string, TData> create, bool validatorAddsKey)
    {
        var job = new JobDefinition<NestedExtensionArgs<TData>>("extensions");
        foreach (var malformed in MalformedStrings)
        {
            var validations = 0;
            var executions = 0;
            var registry = new JobRegistry();
            registry.Register(job, (_, _) => { executions++; return Task.CompletedTask; }, args =>
            {
                validations++;
                if (validatorAddsKey) args.Items[0].Extra = create(malformed);
            });
            var original = new NestedExtensionArgs<TData>([new()
                { Extra = create(validatorAddsKey ? "valid" : malformed) }]);
            var clock = new CountingClock();

            var error = Assert.Throws<JobValidationException>(() =>
                new JobPreparation(registry.Contracts, clock).Prepare(job, original));

            Assert.Contains("UTF-16", error.Message);
            Assert.Equal(validatorAddsKey ? 1 : 0, validations);
            Assert.Equal(0, executions);
            Assert.Equal(0, clock.Reads);
        }
    }

    [Fact]
    public void Extension_data_preserves_well_formed_keys_and_optional_null()
    {
        VerifyValidExtensionKeys(key => new Dictionary<string, JsonElement>
            { [key] = JsonSerializer.SerializeToElement(1) });
        VerifyValidExtensionKeys<IDictionary<string, object?>>(key => new Dictionary<string, object?>
            { [key] = 1 });
    }

    private static void VerifyValidExtensionKeys<TData>(Func<string, TData> create)
    {
        var job = new JobDefinition<ExtensionArgs<TData>>("extensions");
        var contracts = new JobContracts();
        contracts.Add(job);
        var preparation = new JobPreparation(contracts, TimeProvider.System);
        foreach (var key in new[] { "", "  Olá  ", "😀", "e\u0301", "é", "\uFFFD", "\0", "\"\\\n" })
        {
            var prepared = preparation.Prepare(job, new ExtensionArgs<TData> { Extra = create(key) });
            Assert.Equal(1, prepared.Descriptor.Args.GetProperty(key).GetInt32());
            Assert.Single(prepared.Descriptor.Args.EnumerateObject());
            var restored = contracts.Get(job).DeserializeAndValidate(prepared.Descriptor.Args);
            var repeated = preparation.Prepare(job, restored);
            Assert.Equal(1, repeated.Descriptor.Args.GetProperty(key).GetInt32());
        }

        var empty = preparation.Prepare(job, new ExtensionArgs<TData>());
        Assert.Empty(empty.Descriptor.Args.EnumerateObject());
        Assert.Null(contracts.Get(job).DeserializeAndValidate(empty.Descriptor.Args).Extra);
    }

    [Fact]
    public void Well_formed_text_and_keys_round_trip_without_normalization_or_null_restrictions()
    {
        var job = new JobDefinition<NestedTextArgs>("nested-text");
        var contracts = new JobContracts();
        contracts.Add(job);
        var texts = new[] { "", "  Olá  ", "😀", "e\u0301", "é", "\uFFFD", "\0", "\"\\\n" };
        var original = new NestedTextArgs(texts.Select(text =>
            new Dictionary<string, string?> { [text] = text, ["nullable"] = null }).ToList());

        var prepared = new JobPreparation(contracts, TimeProvider.System).Prepare(job, original);
        var restored = contracts.Get(job).DeserializeAndValidate(prepared.Descriptor.Args);

        for (var i = 0; i < texts.Length; i++)
        {
            Assert.Equal(texts[i], restored.Items[i][texts[i]]);
            Assert.Null(restored.Items[i]["nullable"]);
        }
    }
}
