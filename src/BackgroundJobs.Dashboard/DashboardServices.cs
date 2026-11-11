using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using StackExchange.Redis;

namespace BackgroundJobs.Dashboard;

public static class DashboardServices
{
    /// <summary>Registers UI and read-only monitoring; never registers JobServer or job handlers.</summary>
    public static IServiceCollection AddBackgroundJobsDashboard(this IServiceCollection services,
        string prefix = "background-jobs", Action<DashboardOptions>? configure = null)
    {
        var options = new DashboardOptions { StorageLabel = prefix };
        configure?.Invoke(options);
        if (options.MaxJobs is < 1 or > 100_000 || string.IsNullOrWhiteSpace(options.StorageLabel) ||
            string.IsNullOrEmpty(options.Path) || !options.Path.StartsWith('/') || options.Path == "/" ||
            options.Path.Contains('?') || options.Path.Contains('#') || options.Path.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Invalid dashboard options.");
        options.Path = options.Path.TrimEnd('/');
        services.AddSingleton(options);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, DashboardStartupFilter>());
        services.AddSingleton<RedisJobManager>(provider => new RedisJobManager(provider.GetRequiredService<IConnectionMultiplexer>(), prefix));
        services.AddSingleton(provider => new RedisJobMonitor(provider.GetRequiredService<IConnectionMultiplexer>(), prefix));
        services.AddSingleton<DashboardObservationCache>();
        services.AddScoped<DashboardConnection>();
        services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler>(
            provider => provider.GetRequiredService<DashboardConnection>());
        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        services.AddRazorComponents().AddInteractiveServerComponents();
        return services;
    }
}

public sealed class DashboardObservationCache(RedisJobMonitor monitor, DashboardOptions options) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private JobObservation? latest;
    private long version;
    private long cachedVersion;
    public async Task<JobObservation> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (latest is not null && cachedVersion == Volatile.Read(ref version) && DateTimeOffset.UtcNow - latest.FinishedAt < TimeSpan.FromSeconds(1)) return latest;
            var readVersion = Volatile.Read(ref version);
            latest = await monitor.ReadAsync(options.MaxJobs, cancellationToken);
            cachedVersion = readVersion;
            return latest;
        }
        finally { gate.Release(); }
    }
    public void Invalidate() => Interlocked.Increment(ref version);
    public void Dispose() => gate.Dispose();
}
