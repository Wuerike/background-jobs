using System.Text.Json;
using System.Text.Json.Serialization;
using BackgroundJobs;
using BackgroundJobs.Demo.Endpoints;
using BackgroundJobs.Demo.Jobs;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration["PORT"] ?? "8080"}");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
});

var jobs = new JobRegistry();
jobs.Contracts.Add(DemoJobs.LogMessage, DemoJobValidation.ValidateLogMessage);
builder.Services.AddSingleton(jobs);
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration["REDIS_CONNECTION"] ?? "127.0.0.1:6379"));
builder.Services.AddSingleton<IJobClient>(services => new RedisJobClient(
    services.GetRequiredService<JobRegistry>().Contracts, services.GetRequiredService<IConnectionMultiplexer>()));
builder.Services.AddSingleton<IJobManager>(services => new RedisJobManager(services.GetRequiredService<IConnectionMultiplexer>()));

var app = builder.Build();
// Keep unexpected failures generic even in Development. Details stay in server logs.
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (Exception error) when (!context.Response.HasStarted)
    {
        app.Logger.LogError(error, "Falha ao atender a requisição HTTP.");
        context.Response.Clear();
        context.Response.StatusCode = error is BadHttpRequestException
            ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            error = error is BadHttpRequestException ? "Requisição inválida." : "Erro interno ao processar a requisição."
        });
    }
});
app.MapJobEndpoints();
app.Run();

public partial class Program;
