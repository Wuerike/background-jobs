namespace BackgroundJobs.Demo.Endpoints;

internal static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        app.MapPost("/demo/jobs", async (HttpRequest request, IJobClient client) =>
        {
            try
            {
                var input = await CreateJobRequest.ReadAsync(request);
                var id = input.Args switch
                {
                    Jobs.LogMessageArgs args => await client.EnqueueAsync(Jobs.DemoJobs.LogMessage, args, input.Options),
                    Jobs.SucceedAfterArgs args => await client.EnqueueAsync(Jobs.DemoJobs.SucceedAfter, args, input.Options),
                    Jobs.FailNTimesArgs args => await client.EnqueueAsync(Jobs.DemoJobs.FailNTimes, args, input.Options),
                    _ => throw new InvalidOperationException("Contrato desconhecido.")
                };
                return Results.Json(new { jobId = id }, statusCode: StatusCodes.Status202Accepted);
            }
            catch (JobValidationException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        app.MapGet("/demo/events", (Events.DemoEvents events) => Results.Ok(new { items = events.Snapshot() }));
        app.MapGet("/health", (JobServer server) => server.IsAvailable
            ? Results.Ok(new { status = "ok" })
            : Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

        app.MapGet("/api/jobs/{id}", async (string id, IJobManager manager) =>
        {
            var detail = await manager.GetAsync(id);
            return detail is null
                ? Results.NotFound(new { error = "Job não encontrado." })
                : Results.Ok(detail);
        });
    }
}
