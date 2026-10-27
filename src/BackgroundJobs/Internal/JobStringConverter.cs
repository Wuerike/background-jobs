using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackgroundJobs.Internal;

internal sealed class JobStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        EnsureWellFormed(value);
        writer.WriteStringValue(value);
    }

    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => reader.GetString()!;

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        EnsureWellFormed(value);
        writer.WritePropertyName(value);
    }

    internal static void EnsureWellFormed(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;

            if (!char.IsHighSurrogate(value[i]) || i + 1 == value.Length || !char.IsLowSurrogate(value[i + 1]))
            {
                throw new JobValidationException("Os argumentos contêm texto UTF-16 malformado (surrogate sem par).");
            }

            i++;
        }
    }
}
