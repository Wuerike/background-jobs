using System.Text.Json;
using BackgroundJobs.Demo.Jobs;

namespace BackgroundJobs.Demo.Endpoints;

internal sealed record CreateJobRequest(object Args, JobOptions Options)
{
    // Inspect the original JSON before mapping: deserialization would hide duplicate
    // properties and collapse absent fields and explicit null into the same value.
    public static async Task<CreateJobRequest> ReadAsync(HttpRequest request)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            var root = Object(document.RootElement, "pedido", "job", "params", "options");
            var name = Text(Required(root, "job"), "job");
            var parameters = Required(root, "params");
            object args = name switch
            {
                "log-message" => new LogMessageArgs(Text(Required(Object(parameters, "params", "message"), "message"), "message")),
                "succeed-after" => new SucceedAfterArgs(Integer(Required(Object(parameters, "params", "delayMs"), "delayMs"), "delayMs")),
                "fail-n-times" => new FailNTimesArgs(Integer(Required(Object(parameters, "params", "failTimes"), "failTimes"), "failTimes")),
                _ => throw new JobValidationException("job desconhecido.")
            };
            var options = new JobOptions();
            if (root.TryGetProperty("options", out var value))
            {
                var obj = Object(value, "options", "runAt", "maxAttempts", "retryDelayMs");
                if (obj.TryGetProperty("maxAttempts", out value))
                    options = options with { MaxAttempts = Integer(value, "maxAttempts") };
                if (obj.TryGetProperty("retryDelayMs", out value))
                    options = options with { RetryDelayMs = Integer(value, "retryDelayMs") };
                if (obj.TryGetProperty("runAt", out value))
                    options = options with { RunAt = Instant(value) };
            }
            return new(args, options);
        }
        // These catches surround parsing only. Exceptions from library validators
        // must retain their classification as unexpected failures at the HTTP boundary.
        catch (JsonException)
        {
            throw new JobValidationException("JSON inválido.");
        }
        catch (InvalidOperationException)
        {
            throw new JobValidationException("Texto JSON inválido.");
        }
    }

    private static JsonElement Object(JsonElement value, string field, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new JobValidationException($"{field} deve ser um objeto.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new JobValidationException($"Campo duplicado em {field}.");
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new JobValidationException($"Campo desconhecido em {field}.");
        }
        return value;
    }

    private static JsonElement Required(JsonElement value, string field) =>
        value.TryGetProperty(field, out var result) ? result :
            throw new JobValidationException($"{field} é obrigatório.");

    private static string Text(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! :
            throw new JobValidationException($"{field} deve ser textual e não nulo.");

    private static int Integer(JsonElement value, string field)
    {
        if (value.ValueKind == JsonValueKind.Number &&
            !value.GetRawText().Any(c => c is '.' or 'e' or 'E') && value.TryGetInt32(out var number))
            return number;
        throw new JobValidationException($"{field} deve ser um inteiro de 32 bits.");
    }

    private static DateTimeOffset Instant(JsonElement value)
    {
        var text = Text(value, "runAt");
        var hasZone = text.EndsWith('Z') ||
            (text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':');
        if (!text.Contains('T') || !hasZone || !value.TryGetDateTimeOffset(out var at))
            throw new JobValidationException("runAt deve ser uma data ISO-8601 com Z ou offset explícito.");
        return at.ToUniversalTime();
    }
}
