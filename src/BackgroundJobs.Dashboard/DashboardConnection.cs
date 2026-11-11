using Microsoft.AspNetCore.Components.Server.Circuits;

namespace BackgroundJobs.Dashboard;

/// <summary>Suspends dashboard polling while a retained Blazor circuit is disconnected.</summary>
public sealed class DashboardConnection : CircuitHandler
{
    private volatile bool connected;
    public bool IsConnected => connected;
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        connected = true;
        return Task.CompletedTask;
    }
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        connected = false;
        return Task.CompletedTask;
    }
}
