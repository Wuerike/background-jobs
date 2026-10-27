using BackgroundJobs;
using BackgroundJobs.Demo.Jobs;

var builder = WebApplication.CreateBuilder(args);

var jobs = new JobRegistry();
jobs.Contracts.Add(DemoJobs.LogMessage, DemoJobValidation.ValidateLogMessage);
builder.Services.AddSingleton(jobs);

var app = builder.Build();

app.Run();
