using Microsoft.Extensions.Hosting;

namespace BackgroundJobs.Hosting;

/// <summary>Hosting adapter; shutdown waits for the active attempt even if the host's token expires.</summary>
public sealed class JobServerHostedService(JobServer server) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => server.StartAsync();
    public Task StopAsync(CancellationToken cancellationToken) => server.StopAsync();
}
