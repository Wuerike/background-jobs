using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace BackgroundJobs.Internal;

internal interface IJobContract
{
    Type ArgsType { get; }
    object? DeserializeAndValidate(JsonElement args);
}

internal sealed class JobContract<TArgs>(string name, Action<TArgs>? validate) : IJobContract
{
    public Type ArgsType => typeof(TArgs);

    public TArgs DeserializeAndValidate(JsonElement args)
    {
        var reconstructed = args.Deserialize<TArgs>(JobJson.Options)!;
        // Do not catch validator exceptions: only the validator can declare an expected rejection.
        validate?.Invoke(reconstructed);
        return reconstructed;
    }

    object? IJobContract.DeserializeAndValidate(JsonElement args) => DeserializeAndValidate(args);

    public JobDescriptor Prepare(TArgs args)
    {
        var serialized = JsonSerializer.SerializeToElement(args, JobJson.Options);
        var reconstructed = DeserializeAndValidate(serialized);
        return new JobDescriptor(name, JsonSerializer.SerializeToElement(reconstructed, JobJson.Options));
    }
}

internal static class JobJson
{
    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(ValidateExtensionDataKeys);
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            NumberHandling = JsonNumberHandling.Strict,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JobStringConverter());
        options.Converters.Add(new JobJsonNodeConverter());
        options.MakeReadOnly();
        return options;
    }

    private static void ValidateExtensionDataKeys(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (!property.IsExtensionData || property.Get is not { } get) continue;

            // Extension data writes names without using the string key converter.
            property.Get = instance =>
            {
                var data = get(instance);
                IEnumerable<string> keys = data switch
                {
                    IDictionary<string, JsonElement> elements => elements.Keys,
                    IDictionary<string, object?> objects => objects.Keys,
                    _ => []
                };
                foreach (var key in keys) JobStringConverter.EnsureWellFormed(key);
                return data;
            };
        }
    }
}
