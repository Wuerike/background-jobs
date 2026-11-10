using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackgroundJobs.Internal;
using StackExchange.Redis;

namespace BackgroundJobs.Storage.Redis;

internal sealed class RedisJobStore : IJobStore
{
    private static readonly string CreateScript = LoadScript("create");
    private static readonly string GetScript = LoadScript("get");
    private readonly IDatabase database;
    private readonly string prefix;

    public RedisJobStore(IConnectionMultiplexer connection, string prefix)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrEmpty(prefix) || !prefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ArgumentException("Prefix must contain only ASCII letters, digits, '.', '_' or '-'.", nameof(prefix));
        this.prefix = prefix;
        database = connection.GetDatabase();
    }

    internal RedisKey[] Keys(string id) =>
        [$"{prefix}:job:{id}", $"{prefix}:job:{id}:history", $"{prefix}:job:{id}:attempts",
         $"{prefix}:jobs", $"{prefix}:ready", $"{prefix}:scheduled", $"{prefix}:sequence"];

    public async Task CreateAsync(NewJob job)
    {
        var p = job.Prepared;
        var values = new string[]
        {
            job.Id, p.Descriptor.Name, p.Descriptor.Args.GetRawText(), JobCodec.StateText(p.State),
            p.MaxAttempts.ToString(CultureInfo.InvariantCulture), p.RetryDelayMs.ToString(CultureInfo.InvariantCulture),
            JobCodec.DateText(p.CreatedAt), JobCodec.DateText(p.UpdatedAt),
            p.ScheduledAt is { } at ? JobCodec.DateText(at) : "",
            p.ScheduledAt is { } due ? JobCodec.ScheduledScore(due).ToString(CultureInfo.InvariantCulture) : "",
            JsonSerializer.Serialize(new { state = JobCodec.StateText(p.State), at = JobCodec.DateText(p.History[0].At), reason = p.History[0].Reason })
        };
        // Hash a length-delimited, deterministic representation of the immutable creation.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
        RedisValue[] args = [.. values.Select(v => (RedisValue)v), identity];
        var result = await database.ScriptEvaluateAsync(CreateScript, Keys(job.Id), args).ConfigureAwait(false);
        if ((long)result != 1) throw new InvalidDataException("Redis did not confirm job creation.");
    }

    public async Task<JobDetails?> GetAsync(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var result = (RedisResult[])(await database.ScriptEvaluateAsync(GetScript, Keys(id)[..3]).ConfigureAwait(false))!;
        return result.Length == 0 ? null : JobCodec.Read(id, result);
    }

    private static string LoadScript(string name)
    {
        using var stream = typeof(RedisJobStore).Assembly.GetManifestResourceStream($"BackgroundJobs.Storage.Redis.{name}.lua")
            ?? throw new InvalidOperationException($"Missing Redis script: {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
