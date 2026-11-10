using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BackgroundJobs.Demo.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace BackgroundJobs.HttpTests;

public sealed class JobHttpTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Factory(Action<IServiceCollection> configure) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Development").ConfigureServices(configure);
    }

    private readonly string prefix = "e13-" + Guid.NewGuid().ToString("N");
    private ConnectionMultiplexer connection = null!;
    private Factory factory = null!;
    private HttpClient http = null!;
    private Exception? validatorError;
    private int executions;
    private IJobClient client = null!;
    private IJobManager manager = null!;

    public async Task InitializeAsync()
    {
        connection = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "127.0.0.1:6379");
        var registry = new JobRegistry();
        registry.Register(DemoJobs.LogMessage, (_, _) => { executions++; return Task.CompletedTask; }, args =>
        {
            if (validatorError is not null) throw validatorError;
            DemoJobValidation.ValidateLogMessage(args);
        });
        client = new RedisJobClient(registry.Contracts, connection, prefix, new Clock());
        manager = new RedisJobManager(connection, prefix);
        factory = new Factory(services =>
        {
            services.RemoveAll<JobRegistry>();
            services.AddSingleton(registry);
            services.RemoveAll<IJobClient>();
            services.AddSingleton(client);
            services.RemoveAll<IJobManager>();
            services.AddSingleton(manager);
        });
        http = factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        http?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        if (connection is null) return;
        try
        {
            var keys = connection.GetServer(connection.GetEndPoints()[0])
                .Keys(connection.GetDatabase().Database, prefix + ":*").ToArray();
            if (keys.Length > 0) await connection.GetDatabase().KeyDeleteAsync(keys);
        }
        finally { await connection.DisposeAsync(); }
    }

    private Task<HttpResponseMessage> Post(string json) => http.PostAsync("/demo/jobs", new StringContent(json, Encoding.UTF8, "application/json"));
    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    private async Task AssertNoJobs() => Assert.Equal(0, await connection.GetDatabase().SetLengthAsync(prefix + ":jobs"));

    [Theory]
    [InlineData("")]
    [InlineData(",\"options\":{}")]
    [InlineData(",\"options\":{\"runAt\":\"2020-01-01T00:00:00Z\"}")]
    [InlineData(",\"options\":{\"runAt\":\"2026-11-10T00:00:00Z\"}")]
    [InlineData(",\"options\":{\"maxAttempts\":1,\"retryDelayMs\":0}")]
    [InlineData(",\"options\":{\"maxAttempts\":10,\"retryDelayMs\":60000}")]
    [InlineData(",\"options\":{\"runAt\":\"2030-01-01T03:00:00.0000001+03:00\",\"maxAttempts\":2,\"retryDelayMs\":500}")]
    public async Task Creation_and_detail_match_direct_typed_use(string options)
    {
        const string message = "  Olá 🦊\n\"teste\"  ";
        using var response = await Post("{\"job\":\"log-message\",\"params\":{\"message\":" + JsonSerializer.Serialize(message) + "}" + options + "}");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await Body(response);
        Assert.Single(created.EnumerateObject());
        var id = created.GetProperty("jobId").GetString()!;
        Assert.NotEmpty(id);
        // Persistence is observable as soon as the HTTP acknowledgement arrives.
        var stored = Assert.IsType<JobDetails>(await manager.GetAsync(id));
        using var read = await http.GetAsync("/api/jobs/" + id);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("application/json", read.Content.Headers.ContentType!.MediaType);
        var detail = await Body(read);
        Assert.Equal(new[] { "args", "attemptHistory", "attempts", "createdAt", "history", "id", "maxAttempts", "name", "retryDelayMs", "scheduledAt", "state", "updatedAt" },
            detail.EnumerateObject().Select(p => p.Name).Order().ToArray());
        using var optionDocument = JsonDocument.Parse("{" + options.TrimStart(',') + "}");
        var typedOptions = optionDocument.RootElement.TryGetProperty("options", out var rawOptions)
            ? JsonSerializer.Deserialize<JobOptions>(rawOptions, new JsonSerializerOptions(JsonSerializerDefaults.Web))! : new JobOptions();
        Assert.Equal(typedOptions.MaxAttempts, stored.MaxAttempts);
        Assert.Equal(typedOptions.RetryDelayMs, stored.RetryDelayMs);
        Assert.Equal(stored.MaxAttempts, detail.GetProperty("maxAttempts").GetInt32());
        Assert.Equal(stored.RetryDelayMs, detail.GetProperty("retryDelayMs").GetInt32());
        Assert.Equal(id, detail.GetProperty("id").GetString());
        Assert.Equal(message, detail.GetProperty("args").GetProperty("message").GetString());
        Assert.Equal(0, detail.GetProperty("attempts").GetInt32());
        Assert.Equal(Now, detail.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(Now, detail.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(0, detail.GetProperty("attemptHistory").GetArrayLength());
        var history = Assert.Single(detail.GetProperty("history").EnumerateArray());
        Assert.Equal(detail.GetProperty("state").GetString(), history.GetProperty("state").GetString());
        Assert.Equal(Now, history.GetProperty("at").GetDateTimeOffset());
        Assert.False(string.IsNullOrEmpty(history.GetProperty("reason").GetString()));
        if (stored.ScheduledAt is { } at)
        {
            Assert.Equal("scheduled", detail.GetProperty("state").GetString());
            Assert.Equal(DateTimeOffset.Parse("2030-01-01T00:00:00.0000001Z"), at);
            Assert.Equal(TimeSpan.Zero, detail.GetProperty("scheduledAt").GetDateTimeOffset().Offset);
        }
        else
        {
            Assert.Equal("enqueued", detail.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, detail.GetProperty("scheduledAt").ValueKind);
        }
        var directId = await client.EnqueueAsync(DemoJobs.LogMessage, new LogMessageArgs(message), typedOptions);
        var direct = Assert.IsType<JobDetails>(await manager.GetAsync(directId));
        Assert.Equal(stored.State, direct.State);
        Assert.Equal(stored.Args.GetRawText(), direct.Args.GetRawText());
        Assert.Equal(stored.History, direct.History);
        Assert.Equal(0, executions);
    }

    public static TheoryData<string> InvalidBodies => new()
    {
        "", "{", "null", "[]", "42", "{}",
        "{\"params\":{\"message\":\"x\"}}",
        "{\"job\":\"log-message\"}",
        "{\"job\":null,\"params\":{}}",
        "{\"job\":1,\"params\":{}}",
        "{\"job\":\"unknown\",\"params\":{\"message\":\"x\"}}",
        "{\"job\":\"LOG-MESSAGE\",\"params\":{\"message\":\"x\"}}",
        "{\"job\":\" log-message\",\"params\":{\"message\":\"x\"}}",
        "{\"Job\":\"log-message\",\"params\":{}}",
        "{\"job\":\"log-message\",\"params\":null}",
        "{\"job\":\"log-message\",\"params\":[]}",
        "{\"job\":\"log-message\",\"params\":{}}",
        "{\"job\":\"log-message\",\"params\":{\"Message\":\"x\"}}",
        "{\"job\":\"log-message\",\"params\":{\"message\":null}}",
        "{\"job\":\"log-message\",\"params\":{\"message\":42}}",
        "{\"job\":\"log-message\",\"params\":{\"message\":{\"nested\":1}}}",
        "{\"job\":\"log-message\",\"params\":{\"message\":\"x\",\"extra\":true}}",
        "{\"job\":\"log-message\",\"params\":{\"message\":\"x\",\"message\":\"y\"}}",
        "{\"job\":\"log-message\",\"params\":{\"message\":\"x\",\"mess\\u0061ge\":\"y\"}}",
        "{\"job\":\"log-message\",\"job\":\"log-message\",\"params\":{\"message\":\"x\"}}",
        "{\"job\":\"log-message\",\"j\\u006fb\":\"log-message\",\"params\":{\"message\":\"x\"}}",
        "{\"job\":\"log-message\",\"params\":{\"message\":\"\\ud800\"}}",
        "{\"job\":\"log-message\",\"params\":{\"message\":\"x\"},\"extra\":1}",
        "{\"job\":\"log-message\",\"params\":{\"message\":\"x\"},}",
        "{/*comment*/\"job\":\"log-message\",\"params\":{\"message\":\"x\"}}"
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_input_is_rejected_before_persistence(string json)
    {
        using var response = await Post(json);
        await AssertError(response, HttpStatusCode.BadRequest);
        await AssertNoJobs();
    }

    public static TheoryData<string> InvalidOptions => new()
    {
        "null", "[]", "1", "{\"extra\":1}", "{\"MaxAttempts\":3}",
        "{\"maxAttempts\":null}", "{\"retryDelayMs\":null}", "{\"runAt\":null}",
        "{\"maxAttempts\":3,\"maxAttempts\":4}",
        "{\"maxAttempts\":3,\"max\\u0041ttempts\":4}",
        "{\"maxAttempts\":0}", "{\"maxAttempts\":11}",
        "{\"maxAttempts\":\"3\"}", "{\"maxAttempts\":3.0}", "{\"maxAttempts\":3e0}",
        "{\"maxAttempts\":true}", "{\"maxAttempts\":2147483648}",
        "{\"retryDelayMs\":-1}", "{\"retryDelayMs\":60001}",
        "{\"retryDelayMs\":\"1\"}", "{\"retryDelayMs\":1.5}", "{\"retryDelayMs\":1e3}",
        "{\"retryDelayMs\":1.0}", "{\"retryDelayMs\":2147483648}",
        "{\"maxAttempts\":1,\"retryDelayMs\":-1}",
        "{\"runAt\":\"2030-01-01T00:00:00\"}", "{\"runAt\":\"2030-01-01\"}",
        "{\"runAt\":\"\"}", "{\"runAt\":42}", "{\"runAt\":\"tomorrow\"}",
        "{\"runAt\":\"2030-02-30T00:00:00Z\"}", "{\"runAt\":\"2030-01-01T00:00:00+15:00\"}"
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task Options_preserve_types_duplicates_and_absence_vs_null(string options)
    {
        using var response = await Post("{\"job\":\"log-message\",\"params\":{\"message\":\"x\"},\"options\":" + options + "}");
        await AssertError(response, HttpStatusCode.BadRequest);
        await AssertNoJobs();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(201)]
    public async Task Message_length_uses_library_validation(int length)
    {
        using var response = await Post(JsonSerializer.Serialize(new { job = "log-message", @params = new { message = new string(' ', length) } }));
        Assert.Equal(length <= 200 ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest, response.StatusCode);
        if (length > 200) await AssertNoJobs();
    }

    [Theory]
    [InlineData("expected")]
    [InlineData("argument")]
    [InlineData("json")]
    [InlineData("unexpected")]
    public async Task Validator_bugs_remain_internal_errors(string kind)
    {
        validatorError = kind switch
        {
            "expected" => new JobValidationException("Rejeição declarada."),
            "argument" => new ArgumentException("sensitive argument details"),
            "json" => new JsonException("sensitive json details"),
            _ => new InvalidOperationException("sensitive internal details")
        };
        using var response = await Post("{\"job\":\"log-message\",\"params\":{\"message\":\"x\"}}");
        var body = await AssertError(response, kind == "expected" ? HttpStatusCode.BadRequest : HttpStatusCode.InternalServerError);
        Assert.Equal(kind == "expected" ? "Rejeição declarada." : "Erro interno ao processar a requisição.", body.GetProperty("error").GetString());
        await AssertNoJobs();
    }

    [Fact]
    public async Task Opaque_unknown_id_returns_json_404()
    {
        using var response = await http.GetAsync("/api/jobs/opaque-not-a-guid");
        await AssertError(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lookup_needs_no_demo_contract_and_preserves_structured_args()
    {
        var contracts = new JobContracts();
        var definition = new JobDefinition<int[]>("other-job.v1");
        contracts.Add(definition);
        var id = await new RedisJobClient(contracts, connection, prefix).EnqueueAsync(definition, [1, 2]);
        using var response = await http.GetAsync("/api/jobs/" + id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Body(response);
        Assert.Equal("other-job.v1", body.GetProperty("name").GetString());
        Assert.Equal("[1,2]", body.GetProperty("args").GetRawText());
    }

    [Fact]
    public async Task Corrupt_storage_returns_generic_500_instead_of_not_found()
    {
        var id = await client.EnqueueAsync(DemoJobs.LogMessage, new LogMessageArgs("secret"));
        await connection.GetDatabase().HashSetAsync($"{prefix}:job:{id}", "state", "invalid");
        using var response = await http.GetAsync("/api/jobs/" + id);
        await AssertError(response, HttpStatusCode.InternalServerError);
        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync());
    }

    private sealed class SnapshotManager(JobDetails snapshot) : IJobManager
    {
        public Task<JobDetails?> GetAsync(string id) => Task.FromResult<JobDetails?>(snapshot);
    }

    [Theory]
    [InlineData(JobState.Scheduled, "scheduled")]
    [InlineData(JobState.Enqueued, "enqueued")]
    [InlineData(JobState.Processing, "processing")]
    [InlineData(JobState.Succeeded, "succeeded")]
    [InlineData(JobState.Failed, "failed")]
    [InlineData(JobState.Deleted, "deleted")]
    public async Task Detail_serializes_states_outcomes_nulls_and_utc_dates(JobState state, string text)
    {
        var offsetTime = Now.ToOffset(TimeSpan.FromHours(-3));
        var snapshot = new JobDetails("opaque id", "unknown-contract", JsonSerializer.SerializeToElement(new { message = "x" }),
            state, 1, 3, 1000, state == JobState.Scheduled ? offsetTime : null, offsetTime, offsetTime,
            [new JobStateChange(state, offsetTime, "reason")],
            [new JobAttempt(1, offsetTime, offsetTime, JobAttemptOutcome.Succeeded, null),
             new JobAttempt(2, offsetTime, offsetTime, JobAttemptOutcome.Failed, "failure"),
             new JobAttempt(3, offsetTime, offsetTime, JobAttemptOutcome.Interrupted, "interruption"),
             new JobAttempt(1, offsetTime, null, null, null)]);
        await using var snapshotFactory = new Factory(services =>
        {
            services.RemoveAll<IJobManager>();
            services.AddSingleton<IJobManager>(new SnapshotManager(snapshot));
        });
        using var snapshotHttp = snapshotFactory.CreateClient();
        using var response = await snapshotHttp.GetAsync("/api/jobs/opaque%20id");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Body(response);
        Assert.Equal(text, body.GetProperty("state").GetString());
        Assert.Equal(text, body.GetProperty("history")[0].GetProperty("state").GetString());
        Assert.Equal("opaque id", body.GetProperty("id").GetString());
        foreach (var field in new[] { "createdAt", "updatedAt" })
        {
            var time = body.GetProperty(field);
            Assert.Equal(Now, time.GetDateTimeOffset());
            Assert.Equal(TimeSpan.Zero, time.GetDateTimeOffset().Offset);
        }
        var attempts = body.GetProperty("attemptHistory").EnumerateArray().ToArray();
        Assert.Equal(new string?[] { "succeeded", "failed", "interrupted", null },
            attempts.Select(a => a.GetProperty("outcome").GetString()));
        Assert.Null(attempts[0].GetProperty("error").GetString());
        Assert.Equal("failure", attempts[1].GetProperty("error").GetString());
        Assert.Equal("interruption", attempts[2].GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, attempts[3].GetProperty("finishedAt").ValueKind);
        Assert.Equal(Now, attempts[0].GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(Now, attempts[0].GetProperty("finishedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Redis_failures_return_generic_500_for_creation_and_lookup()
    {
        var configuration = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "127.0.0.1:6379";
        var closed = await ConnectionMultiplexer.ConnectAsync(configuration);
        await closed.DisposeAsync();
        var contracts = new JobContracts();
        contracts.Add(DemoJobs.LogMessage, DemoJobValidation.ValidateLogMessage);
        await using var failingFactory = new Factory(services =>
        {
            services.RemoveAll<IJobClient>();
            services.AddSingleton<IJobClient>(new RedisJobClient(contracts, closed, prefix));
            services.RemoveAll<IJobManager>();
            services.AddSingleton<IJobManager>(new RedisJobManager(closed, prefix));
        });
        using var failingHttp = failingFactory.CreateClient();
        using var create = await failingHttp.PostAsJsonAsync("/demo/jobs", new { job = "log-message", @params = new { message = "x" } });
        await AssertError(create, HttpStatusCode.InternalServerError);
        using var read = await failingHttp.GetAsync("/api/jobs/unknown");
        await AssertError(read, HttpStatusCode.InternalServerError);
        await AssertNoJobs();
    }

    private static async Task<JsonElement> AssertError(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var body = await Body(response);
        Assert.Single(body.EnumerateObject());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        return body;
    }
}
