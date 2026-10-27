using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BackgroundJobs.Internal;

internal sealed class JobJsonNodeConverter : JsonConverter<JsonNode>
{
    private static readonly JsonConverter<JsonNode> NativeConverter =
        (JsonConverter<JsonNode>)JsonSerializerOptions.Default.GetConverter(typeof(JsonNode));

    public override bool CanConvert(Type typeToConvert) => typeof(JsonNode).IsAssignableFrom(typeToConvert);

    public override JsonNode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = NativeConverter.Read(ref reader, typeToConvert, options);
        if (node is not null && !typeToConvert.IsInstanceOfType(node))
            throw new JsonException($"O valor JSON é incompatível com {typeToConvert.Name}.");
        return node;
    }

    public override void Write(Utf8JsonWriter writer, JsonNode value, JsonSerializerOptions options)
    {
        // JsonNode.WriteTo bypasses string converters, so check its subtree before writing it.
        EnsureWellFormed(value, (options.MaxDepth == 0 ? 64 : options.MaxDepth) - writer.CurrentDepth);
        value.WriteTo(writer, options);
    }

    private static void EnsureWellFormed(JsonNode? node, int remainingDepth)
    {
        if (remainingDepth < 0) throw new JsonException("Os argumentos excedem a profundidade JSON permitida.");

        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    JobStringConverter.EnsureWellFormed(property.Key);
                    EnsureWellFormed(property.Value, remainingDepth - 1);
                }
                break;
            case JsonArray array:
                foreach (var item in array) EnsureWellFormed(item, remainingDepth - 1);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                JobStringConverter.EnsureWellFormed(text);
                break;
        }
    }
}
