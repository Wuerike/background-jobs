using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackgroundJobs.Internal;
using StackExchange.Redis;

namespace BackgroundJobs.Storage.Redis;

internal sealed class RedisJobStore : IJobStore, IProcessingStore
{
    private static readonly string CreateScript = LoadScript("create");
    private static readonly string GetScript = LoadScript("get");
    private static readonly string ClaimScript = LoadScript("processing-common") + "\n" + LoadScript("claim");
    private static readonly string CompleteScript = LoadScript("processing-common") + "\n" + LoadScript("complete");
    private static readonly string PromoteScript = LoadScript("processing-common") + "\n" + LoadScript("promote");
    internal const int ScheduledBatchSize = 128;
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

    private RedisKey[] ProcessingKeys(string id) => [.. Keys(id), $"{prefix}:processing"];

    public async IAsyncEnumerable<JobReference> ReadReferencesAsync()
    {
        // SSCAN pages keep startup independent from a public list endpoint.
        await foreach (var value in database.SetScanAsync($"{prefix}:jobs", pageSize: 128).ConfigureAwait(false))
        {
            var id = (string)value!;
            var detail = await GetAsync(id).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Missing indexed job '{id}'.");
            yield return new JobReference(id, detail.Name, detail.State);
        }
    }

    public async Task<bool> HasProcessingAsync() =>
        await database.SetLengthAsync($"{prefix}:processing").ConfigureAwait(false) != 0;

    private async Task<(JobDetails Job, string Snapshot, Dictionary<string, string> Fields)> ReadProcessingSnapshot(string id)
    {
        var result = (RedisResult[])(await database.ScriptEvaluateAsync(GetScript, Keys(id)[..3]).ConfigureAwait(false))!;
        if (result.Length == 0) throw new InvalidDataException($"Missing processing candidate '{id}'.");
        var job = JobCodec.Read(id, result);
        var lists = result.Select(r => ((RedisResult[])r!).Select(v => (string)v!).ToArray()).ToArray();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < lists[0].Length; i += 2) fields.Add(lists[0][i], lists[0][i+1]);
        if (!fields.TryGetValue("creationIdentity", out var identity) || string.IsNullOrEmpty(identity) ||
            job.History[^1].State != job.State || job.History[^1].At != job.UpdatedAt ||
            job.AttemptHistory.Any(a => a.FinishedAt < a.StartedAt))
            throw new InvalidDataException($"Inconsistent processing metadata for '{id}'.");
        return (job, JsonSerializer.Serialize(lists), fields);
    }

    public async Task<IReadOnlyList<ScheduledCandidate>> ReadScheduledAsync(DateTimeOffset now, long offset)
    {
        var entries = await database.SortedSetRangeByScoreWithScoresAsync($"{prefix}:scheduled",
            double.NegativeInfinity, JobCodec.ScheduledScore(now), skip: offset, take: ScheduledBatchSize).ConfigureAwait(false);
        var candidates = new List<ScheduledCandidate>(entries.Length);
        foreach (var entry in entries)
        {
            var (job, snapshot, _) = await ReadProcessingSnapshot((string)entry.Element!).ConfigureAwait(false);
            if (job.State != JobState.Scheduled || job.ScheduledAt is not { } due ||
                JobCodec.ScheduledScore(due) != entry.Score || job.Attempts >= job.MaxAttempts ||
                job.AttemptHistory.Any(a => a.FinishedAt is null) ||
                (job.Attempts == 0 ? job.AttemptHistory.Count != 0 :
                    job.AttemptHistory.Count == 0 || job.AttemptHistory[^1].Attempt != job.Attempts ||
                    job.AttemptHistory[^1].Outcome != JobAttemptOutcome.Failed ||
                    job.AttemptHistory[^1].FinishedAt!.Value.AddMilliseconds(job.RetryDelayMs) != due))
                throw new InvalidDataException($"Invalid scheduled metadata for '{job.Id}'.");
            candidates.Add(new(job, snapshot, entry.Score));
        }
        return candidates;
    }

    public async Task<PromotionResult> TryPromoteAsync(ScheduledCandidate candidate, DateTimeOffset now)
    {
        var job = candidate.Job;
        now = now.ToUniversalTime();
        if (job.State != JobState.Scheduled || job.ScheduledAt is not { } due || due > now ||
            now < job.UpdatedAt || JobCodec.ScheduledScore(due) != candidate.Score)
            throw new InvalidDataException("Invalid promotion eligibility.");
        var history = JsonSerializer.Serialize(new { state = "enqueued", at = JobCodec.DateText(now),
            reason = job.Attempts == 0 ? "schedule eligible" : "retry eligible" });
        var result = await database.ScriptEvaluateAsync(PromoteScript, ProcessingKeys(job.Id),
            [job.Id, candidate.Snapshot, candidate.Score.ToString("R", CultureInfo.InvariantCulture),
             JobCodec.DateText(due), JobCodec.DateText(now), history, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Snapshot)))]).ConfigureAwait(false);
        return (long)result switch
        {
            1 => PromotionResult.Promoted,
            0 => PromotionResult.LostRace,
            _ => throw new InvalidDataException("Redis did not confirm promotion.")
        };
    }

    public async Task<JobCandidate?> PeekAsync()
    {
        var head = await database.SortedSetRangeByRankWithScoresAsync($"{prefix}:ready", 0, 0).ConfigureAwait(false);
        if (head.Length == 0) return null;
        var (job, snapshot, fields) = await ReadProcessingSnapshot((string)head[0].Element!).ConfigureAwait(false);
        if (await database.SortedSetScoreAsync($"{prefix}:ready", job.Id).ConfigureAwait(false) != head[0].Score) return null;
        // A concurrent winner can move this job between reading the head and snapshot.
        if (job.State != JobState.Enqueued)
        {
            if (await database.SortedSetScoreAsync($"{prefix}:ready", job.Id).ConfigureAwait(false) is null) return null;
            throw new InvalidDataException($"Non-enqueued ready job '{job.Id}'.");
        }
        var cycle = fields.GetValueOrDefault("cycle", "0");
        if (!long.TryParse(cycle, NumberStyles.None, CultureInfo.InvariantCulture, out var cycleNumber) || cycleNumber < 0 ||
            cycle != cycleNumber.ToString(CultureInfo.InvariantCulture) || job.Attempts >= job.MaxAttempts ||
            job.AttemptHistory.Any(a => a.FinishedAt is null) ||
            (job.Attempts == 0 ? cycle == "0" && job.AttemptHistory.Count != 0 :
                job.AttemptHistory.Count == 0 || job.AttemptHistory[^1].Attempt != job.Attempts))
            throw new InvalidDataException($"Invalid claim metadata for '{job.Id}'.");
        return new JobCandidate(job, snapshot, cycle, head[0].Score);
    }

    public async Task<ClaimedJob?> TryClaimAsync(JobCandidate candidate, DateTimeOffset startedAt, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var job = candidate.Job;
        startedAt = startedAt.ToUniversalTime();
        if (startedAt < job.UpdatedAt) throw new InvalidDataException("Attempt start precedes job update.");
        var number = job.Attempts + 1;
        var attempt = JsonSerializer.Serialize(new { attempt = number, startedAt = JobCodec.DateText(startedAt),
            finishedAt = (string?)null, outcome = (string?)null, error = (string?)null });
        var history = JsonSerializer.Serialize(new { state = "processing", at = JobCodec.DateText(startedAt), reason = "execution started" });
        var result = await database.ScriptEvaluateAsync(ClaimScript, ProcessingKeys(job.Id),
            [job.Id, candidate.Snapshot, candidate.Cycle, token, JobCodec.DateText(startedAt), attempt, history, candidate.Position.ToString("R", CultureInfo.InvariantCulture)]).ConfigureAwait(false);
        return (long)result == 0 ? null : new ClaimedJob(job, number, candidate.Cycle, token, startedAt);
    }

    public async Task CompleteAsync(AttemptCompletion completion)
    {
        var claim = completion.Claim;
        var job = claim.Job;
        var finish = completion.FinishedAt.ToUniversalTime();
        if (finish < claim.StartedAt) throw new InvalidDataException("Attempt finish precedes start.");
        var success = completion.Error is null;
        var state = success ? "succeeded" : claim.Attempt < job.MaxAttempts ? "scheduled" : "failed";
        // Compute representability before Redis can write anything. Overflow is infrastructure failure.
        DateTimeOffset? due = state == "scheduled" ? finish.AddMilliseconds(job.RetryDelayMs) : null;
        var attempt = JsonSerializer.Serialize(new { attempt = claim.Attempt, startedAt = JobCodec.DateText(claim.StartedAt),
            finishedAt = JobCodec.DateText(finish), outcome = success ? "succeeded" : "failed", error = completion.Error });
        var history = JsonSerializer.Serialize(new { state, at = JobCodec.DateText(finish),
            reason = success ? "execution succeeded" : state == "scheduled" ? "retry scheduled" : "attempts exhausted" });
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] {
            job.Id, claim.Cycle, claim.Token, attempt, history, due is { } d ? JobCodec.DateText(d) : "" }))));
        var (current, snapshot, fields) = await ReadProcessingSnapshot(job.Id).ConfigureAwait(false);
        if (fields.GetValueOrDefault("completionIdentity") != identity)
        {
            if (current.State != JobState.Processing || current.Attempts != claim.Attempt ||
                current.AttemptHistory.Count == 0 || current.AttemptHistory[^1].Attempt != claim.Attempt ||
                current.AttemptHistory[^1].StartedAt != claim.StartedAt ||
                current.AttemptHistory[^1].FinishedAt is not null || current.AttemptHistory[^1].Error is not null ||
                current.AttemptHistory.Take(current.AttemptHistory.Count-1).Any(a => a.FinishedAt is null) ||
                current.Name != job.Name || current.Args.GetRawText() != job.Args.GetRawText() ||
                current.MaxAttempts != job.MaxAttempts || current.RetryDelayMs != job.RetryDelayMs)
                throw new InvalidDataException($"Invalid open attempt for '{job.Id}'.");
        }
        var result = await database.ScriptEvaluateAsync(CompleteScript, ProcessingKeys(job.Id),
            [job.Id, snapshot, claim.Cycle, claim.Token, identity, JobCodec.DateText(finish), state,
             due is { } at ? JobCodec.DateText(at) : "", due is { } date ? JobCodec.ScheduledScore(date).ToString(CultureInfo.InvariantCulture) : "",
             attempt, history]).ConfigureAwait(false);
        if ((long)result != 1) throw new InvalidDataException("Redis did not confirm completion.");
    }

    private static string LoadScript(string name)
    {
        using var stream = typeof(RedisJobStore).Assembly.GetManifestResourceStream($"BackgroundJobs.Storage.Redis.{name}.lua")
            ?? throw new InvalidOperationException($"Missing Redis script: {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
