using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;

namespace BackgroundJobs.Storage.Redis;

internal static class JobCodec
{
    internal static string DateText(DateTimeOffset date) => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static long ScheduledScore(DateTimeOffset date)
    {
        var ticks = date.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var milliseconds = Math.DivRem(ticks, TimeSpan.TicksPerMillisecond, out var remainder);
        return milliseconds + (remainder > 0 ? 1 : 0);
    }

    internal static string StateText(JobState state) => state switch
    {
        JobState.Scheduled => "scheduled", JobState.Enqueued => "enqueued", JobState.Processing => "processing",
        JobState.Succeeded => "succeeded", JobState.Failed => "failed", JobState.Deleted => "deleted",
        _ => throw new InvalidDataException("Invalid job state.")
    };

    private static JobState State(string value) => value switch
    {
        "scheduled" => JobState.Scheduled, "enqueued" => JobState.Enqueued, "processing" => JobState.Processing,
        "succeeded" => JobState.Succeeded, "failed" => JobState.Failed, "deleted" => JobState.Deleted,
        _ => throw new InvalidDataException("Invalid state.")
    };

    private static DateTimeOffset Date(string value)
    {
        if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Invalid UTC date.");
        return date;
    }

    private static int Number(string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
            throw new InvalidDataException("Invalid number.");
        return number;
    }

    internal static JobSummary ReadSummary(string id, RedisValue[] fields)
    {
        if (fields.Length != 4 || fields.Any(v => v.IsNull) || (string)fields[0]! != id ||
            string.IsNullOrWhiteSpace((string)fields[1]!))
            throw new InvalidDataException($"Invalid indexed job '{id}'.");
        return new JobSummary(id, (string)fields[1]!, State((string)fields[2]!), Number((string)fields[3]!, 0, 10));
    }

    internal static JobMonitorItem ReadMonitorItem(string id, RedisResult[] row)
    {
        try
        {
            var fields = ((RedisResult[])row[0]!).Select(value => (string?)value).ToArray();
            if (fields.Length != 9 || fields.Take(8).Any(value => value is null) ||
                fields[0] != id || string.IsNullOrWhiteSpace(fields[1])) throw new InvalidDataException();
            var state = State(fields[2]!);
            var max = Number(fields[4]!, 1, 10);
            var attempts = Number(fields[3]!, 0, max);
            var created = Date(fields[6]!);
            var updated = Date(fields[7]!);
            DateTimeOffset? due = fields[8] is { } date ? Date(date) : null;
            var change = ReadChange((string)row[2]!);
            var last = row[1].IsNull ? null : ReadAttempt((string)row[1]!);
            if (created > updated || (state == JobState.Scheduled) != due.HasValue ||
                change.State != state || change.At != updated ||
                attempts > 0 && (last is null || last.Attempt != attempts) ||
                state == JobState.Processing && (last is null || last.FinishedAt is not null || last.StartedAt != updated) ||
                state != JobState.Processing && last is { FinishedAt: null } ||
                last?.FinishedAt < last?.StartedAt) throw new InvalidDataException();
            return new(id, fields[1]!, state, attempts, max, Number(fields[5]!, 0, 60000), created, updated, due, last);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or
            ArgumentException or NullReferenceException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"Invalid monitoring projection for job '{id}'.");
        }
    }

    internal static long Cycle(string value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cycle) || cycle < 0 ||
            value != cycle.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("Invalid job cycle.");
        return cycle;
    }

    internal static JobDetails Read(string id, RedisResult[] snapshot)
    {
        string field = "snapshot";
        try
        {
            var hash = (RedisResult[])snapshot[0]!;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < hash.Length; i += 2) fields.Add((string)hash[i]!, (string)hash[i + 1]!);
            string Get(string key) { field = key; return fields[key]; }
            if (Get("id") != id) throw new InvalidDataException("ID mismatch.");
            var name = Get("name");
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Missing contract name.");
            using var args = JsonDocument.Parse(Get("args"));
            var state = State(Get("state"));
            var maxAttempts = Number(Get("maxAttempts"), 1, 10);
            var attempts = Number(Get("attempts"), 0, maxAttempts);
            var delay = Number(Get("retryDelayMs"), 0, 60000);
            var createdAt = Date(Get("createdAt"));
            var updatedAt = Date(Get("updatedAt"));
            field = "scheduledAt";
            DateTimeOffset? scheduledAt = fields.TryGetValue(field, out var due) ? Date(due) : null;
            if ((state == JobState.Scheduled) != scheduledAt.HasValue) throw new InvalidDataException("Invalid eligibility.");
            field = "history";
            var history = ((RedisResult[])snapshot[1]!).Select(r => ReadChange((string)r!)).ToArray();
            if (history.Length == 0) throw new InvalidDataException("Missing history.");
            field = "attemptHistory";
            var attemptHistory = ((RedisResult[])snapshot[2]!).Select(r => ReadAttempt((string)r!)).ToArray();
            return new JobDetails(id, name, args.RootElement, state, attempts, maxAttempts, delay,
                scheduledAt, createdAt, updatedAt, history, attemptHistory);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            // Do not include the persisted value or parser message: arguments can be sensitive.
            throw new InvalidDataException($"Invalid persisted job '{id}', field '{field}'.");
        }
    }

    private static string Text(JsonElement element, string key) => element.GetProperty(key).GetString()
        ?? throw new InvalidDataException("Required text is null.");

    private static JobStateChange ReadChange(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new JobStateChange(State(Text(root, "state")), Date(Text(root, "at")), Text(root, "reason"));
    }

    private static JobAttempt ReadAttempt(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var number = Number(root.GetProperty("attempt").GetRawText(), 1, 10);
        var start = Date(Text(root, "startedAt"));
        var finish = root.GetProperty("finishedAt");
        var outcome = root.GetProperty("outcome");
        var error = root.GetProperty("error");
        DateTimeOffset? finishedAt = finish.ValueKind == JsonValueKind.Null ? null : Date(finish.GetString()!);
        JobAttemptOutcome? result = outcome.ValueKind == JsonValueKind.Null ? null : outcome.GetString() switch
        {
            "succeeded" => JobAttemptOutcome.Succeeded, "failed" => JobAttemptOutcome.Failed,
            "interrupted" => JobAttemptOutcome.Interrupted, _ => throw new InvalidDataException("Invalid outcome.")
        };
        if (finishedAt.HasValue != result.HasValue) throw new InvalidDataException("Incomplete attempt.");
        return new JobAttempt(number, start, finishedAt, result, error.ValueKind == JsonValueKind.Null ? null : error.GetString());
    }
}
