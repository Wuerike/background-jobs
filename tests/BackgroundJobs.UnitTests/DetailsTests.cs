using System.Text.Json;

namespace BackgroundJobs.UnitTests;

public class DetailsTests
{
    [Fact]
    public void Detail_owns_json_and_read_only_copies_of_histories_and_uses_utc()
    {
        var instant = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.FromHours(-3));
        var history = new List<JobStateChange> { new(JobState.Processing, instant, "started") };
        var attempts = new List<JobAttempt> { new(1, instant, null, null, null) };
        JobDetails detail;
        using (var document = JsonDocument.Parse("{\"message\":\"hello\"}"))
        {
            detail = new JobDetails("opaque:42", "unknown-contract", document.RootElement,
                JobState.Processing, 1, 3, 1000, null, instant, instant, history, attempts);
        }
        history.Clear();
        attempts.Clear();

        Assert.Equal("opaque:42", detail.Id);
        Assert.Equal("unknown-contract", detail.Name);
        Assert.Equal("hello", detail.Args.GetProperty("message").GetString());
        Assert.Single(detail.History);
        var attempt = Assert.Single(detail.AttemptHistory);
        Assert.Null(attempt.FinishedAt);
        Assert.Null(attempt.Outcome);
        Assert.Null(attempt.Error);
        Assert.Equal(TimeSpan.Zero, detail.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, detail.UpdatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, detail.History[0].At.Offset);
        Assert.Equal(TimeSpan.Zero, attempt.StartedAt.Offset);
        Assert.Throws<NotSupportedException>(() => ((IList<JobStateChange>)detail.History).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<JobAttempt>)detail.AttemptHistory).Clear());
        var json = JsonSerializer.SerializeToElement(detail, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal(JsonValueKind.Object, json.GetProperty("args").ValueKind);
        Assert.Equal(12, json.EnumerateObject().Count());
    }

    [Fact]
    public void Finished_attempt_preserves_result_error_and_utc_finish()
    {
        var instant = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.FromHours(-3));
        var attempt = new JobAttempt(2, instant, instant.AddSeconds(1), JobAttemptOutcome.Interrupted, "interrupted");
        Assert.Equal(2, attempt.Attempt);
        Assert.Equal(JobAttemptOutcome.Interrupted, attempt.Outcome);
        Assert.Equal("interrupted", attempt.Error);
        Assert.Equal(TimeSpan.Zero, attempt.FinishedAt!.Value.Offset);
    }

    [Fact]
    public void Public_creation_and_query_contracts_have_only_typed_creation_and_id_lookup()
    {
        var create = Assert.Single(typeof(IJobClient).GetMethods());
        Assert.Equal(typeof(Task<string>), create.ReturnType);
        Assert.True(create.IsGenericMethodDefinition);
        var parameters = create.GetParameters();
        Assert.Equal(typeof(JobDefinition<>), parameters[0].ParameterType.GetGenericTypeDefinition());
        Assert.Equal(create.GetGenericArguments()[0], parameters[1].ParameterType);
        Assert.Equal(typeof(JobOptions), parameters[2].ParameterType);
        Assert.True(parameters[2].HasDefaultValue);
        Assert.Null(parameters[2].DefaultValue);

        var query = Assert.Single(typeof(IJobManager).GetMethods());
        Assert.Equal(typeof(Task<JobDetails>), query.ReturnType);
        Assert.Equal(typeof(string), Assert.Single(query.GetParameters()).ParameterType);
        var nullability = new System.Reflection.NullabilityInfoContext().Create(query.ReturnParameter);
        Assert.Equal(System.Reflection.NullabilityState.Nullable, Assert.Single(nullability.GenericTypeArguments).ReadState);
    }
}
