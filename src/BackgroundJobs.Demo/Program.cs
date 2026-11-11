using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackgroundJobs;
using BackgroundJobs.Hosting;
using BackgroundJobs.Demo.Events;
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
var events = new DemoEvents();
new DemoJobHandlers(events).Register(jobs);
builder.Services.AddSingleton(events);
builder.Services.AddSingleton(jobs);
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var configuration = ConfigurationOptions.Parse(builder.Configuration["REDIS_CONNECTION"] ?? "127.0.0.1:6379");
    configuration.AbortOnConnectFail = false;
    configuration.BacklogPolicy = BacklogPolicy.FailFast;
    return ConnectionMultiplexer.Connect(configuration);
});
builder.Services.AddSingleton<IJobClient>(services => new RedisJobClient(
    services.GetRequiredService<JobRegistry>().Contracts, services.GetRequiredService<IConnectionMultiplexer>()));
builder.Services.AddSingleton<IJobManager>(services => new RedisJobManager(services.GetRequiredService<IConnectionMultiplexer>()));

builder.Services.AddSingleton(services =>
{
    var setting = services.GetRequiredService<IConfiguration>()["WORKER_COUNT"];
    var workerCount = 4;
    if (setting is not null &&
        (!int.TryParse(setting, NumberStyles.Integer, CultureInfo.InvariantCulture, out workerCount) || workerCount < 1))
        throw new InvalidOperationException("WORKER_COUNT deve ser um inteiro positivo de 32 bits.");
    return new JobServer(services.GetRequiredService<JobRegistry>(),
        services.GetRequiredService<IConnectionMultiplexer>(), workerCount: workerCount);
});
builder.Services.AddHostedService<JobServerHostedService>();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = Timeout.InfiniteTimeSpan);

var app = builder.Build();
// Construct and validate the executor before the host begins startup.
_ = app.Services.GetRequiredService<JobServer>();
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
