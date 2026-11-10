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
                var id = await client.EnqueueAsync(Jobs.DemoJobs.LogMessage, input.Args, input.Options);
                return Results.Json(new { jobId = id }, statusCode: StatusCodes.Status202Accepted);
            }
            catch (JobValidationException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });

        app.MapGet("/api/jobs/{id}", async (string id, IJobManager manager) =>
        {
            var detail = await manager.GetAsync(id);
            return detail is null
                ? Results.NotFound(new { error = "Job não encontrado." })
                : Results.Ok(detail);
        });
    }
}
