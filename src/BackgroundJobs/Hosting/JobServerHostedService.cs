using Microsoft.Extensions.Hosting;

namespace BackgroundJobs.Hosting;

/// <summary>Hosting adapter; shutdown waits for all admitted attempts even if the host's token expires.</summary>
public sealed class JobServerHostedService(JobServer server) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var registration = cancellationToken.Register(() => _ = server.StopAsync());
        await server.StartAsync().ConfigureAwait(false);
    }
    public Task StopAsync(CancellationToken cancellationToken) => server.StopAsync();
}
