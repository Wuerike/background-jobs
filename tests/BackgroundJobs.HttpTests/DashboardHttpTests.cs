using System.Net;
using BackgroundJobs.Dashboard;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace BackgroundJobs.HttpTests;

public sealed class DashboardHttpTests : IAsyncLifetime
{
    private readonly string prefix = "dashboard-http-" + Guid.NewGuid().ToString("N");
    private ConnectionMultiplexer connection = null!;
    private DashboardTestHost factory = null!;
    public async Task InitializeAsync()
    {
        connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "127.0.0.1:6379");
        factory = await DashboardTestHost.StartAsync(services =>
        {
            services.AddAuthentication("operators").AddCookie("operators", options => options.LoginPath = "/login");
            services.AddAuthorization(options => options.AddPolicy("denied", policy => policy.RequireAssertion(_ => false)));
            services.RemoveAll<IConnectionMultiplexer>(); services.AddSingleton<IConnectionMultiplexer>(connection);
            services.RemoveAll<RedisJobManager>(); services.AddSingleton<RedisJobManager>(new RedisJobManager(connection, prefix));
            services.RemoveAll<RedisJobMonitor>(); services.AddSingleton(new RedisJobMonitor(connection, prefix));
        });
    }
    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        var keys = connection.GetServer(connection.GetEndPoints()[0]).Keys(pattern: prefix + ":*").ToArray();
        if (keys.Length > 0) await connection.GetDatabase().KeyDeleteAsync(keys);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Single_registration_serves_dashboard_without_executor_or_catalog()
    {
        using var http = factory.CreateClient();
        var html = await http.GetStringAsync("/dashboard/");
        Assert.Contains("Tudo pronto para o primeiro job", html);
        Assert.Empty(factory.Services.GetServices<IHostedService>().OfType<BackgroundJobs.Hosting.JobServerHostedService>());
        Assert.Null(factory.Services.GetService<JobServer>());
        Assert.Null(factory.Services.GetService<JobRegistry>());
        var script = System.Text.RegularExpressions.Regex.Match(html, "src=\"([^\"]*blazor.web[^\"]*)\"").Groups[1].Value;
        Assert.NotEmpty(script);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri(new Uri(http.BaseAddress!, "/dashboard/"), script))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/dashboard/_content/BackgroundJobs.Dashboard/dashboard.css")).StatusCode);
    }

    [Fact]
    public async Task Detail_renders_untrusted_arguments_as_text_and_preserves_direct_link_context()
    {
        var registry = new JobRegistry();
        var definition = new JobDefinition<Arguments>("unsafe-markup");
        registry.Contracts.Add(definition);
        var id = await new RedisJobClient(registry.Contracts, connection, prefix).EnqueueAsync(definition,
            new Arguments("<script>window.injected=true</script>"));
        using var http = factory.CreateClient();
        var html = await http.GetStringAsync("/dashboard/?state=enqueued&q=unsafe&job=" + id);
        Assert.Contains("unsafe-markup", html);
        Assert.Contains("Copiar JSON", html);
        Assert.DoesNotContain("<script>window.injected", html);
        Assert.Contains("state=enqueued", html);
        Assert.Contains("Deletar pend", html);
    }

    [Fact]
    public async Task Authorization_policy_is_checked_before_reads_and_storage_failure_is_not_shown_as_empty()
    {
        using var http = factory.CreateClient();
        var options = factory.Services.GetRequiredService<DashboardOptions>();
        options.AuthorizationPolicy = "denied";
        // Authorization runs before a storage read.
        using var denied = await http.GetAsync("/dashboard/");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Equal("/login", denied.Headers.Location!.AbsolutePath);
        options.AuthorizationPolicy = null;
        await connection.GetDatabase().SetAddAsync(prefix + ":jobs", "missing");
        var html = await http.GetStringAsync("/dashboard/");
        Assert.Contains("Não foi possível atualizar", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("Tudo pronto para o primeiro job", html);
    }
    public sealed record Arguments(string Value);
}

public sealed class DashboardAssetsTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Consumer_serves_styles_and_scripts_with_content_in_each_environment(string environment)
    {
        await using var factory = await DashboardTestHost.StartAsync(environment: environment);
        using var http = factory.CreateClient();
        var assets = new[]
        {
            (Path: "/dashboard/_content/BackgroundJobs.Dashboard/dashboard.css", Type: "text/css", Content: ".dashboard"),
            (Path: "/dashboard/_content/BackgroundJobs.Dashboard/dashboard.js", Type: "text/javascript", Content: "showDialog"),
            (Path: "/dashboard/_framework/blazor.web.js", Type: "text/javascript", Content: "Blazor")
        };
        foreach (var asset in assets)
        {
            using var response = await http.GetAsync(asset.Path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(asset.Type, response.Content.Headers.ContentType?.MediaType);
            Assert.Contains(asset.Content, await response.Content.ReadAsStringAsync());
        }
    }
    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/operations/jobs")]
    public async Task Registration_isolates_its_path_and_preserves_other_consumer_routes(string path)
    {
        await using var factory = await DashboardTestHost.StartAsync(configureDashboard: options => options.Path = path);
        using var http = factory.CreateClient();
        Assert.Equal("consumer endpoint", await http.GetStringAsync("/api/status"));
        Assert.Equal("consumer endpoint", await http.GetStringAsync("/"));
        using var redirect = await http.GetAsync(path + "?q=sample");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal(path + "/?q=sample", redirect.Headers.Location!.ToString());
        using var style = await http.GetAsync(path + "/_content/BackgroundJobs.Dashboard/dashboard.css");
        Assert.Equal("text/css", style.Content.Headers.ContentType?.MediaType);
        Assert.Contains(".dashboard", await style.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dashboard_respects_consumer_login_and_authorization_without_relocating_login(bool useFallback)
    {
        await using var factory = await DashboardTestHost.StartAsync(services =>
        {
            services.AddAuthentication("operators").AddCookie("operators", options => options.LoginPath = "/login");
            services.AddAuthorization(options =>
            {
                options.AddPolicy("operators", policy => policy.RequireAuthenticatedUser());
                if (useFallback) options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            });
        }, configureDashboard: options => options.AuthorizationPolicy = useFallback ? null : "operators");
        using var http = factory.CreateClient();
        using var result = await http.GetAsync("/dashboard/?q=sample");
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.Equal("/login", result.Headers.Location!.AbsolutePath);
        Assert.Contains("%2Fdashboard%2F", result.Headers.Location.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("consumer endpoint", await http.GetStringAsync("/login"));
        Assert.Equal("consumer endpoint", await http.GetStringAsync("/api/status"));
    }

}

internal sealed class DashboardTestHost(IHost host) : IAsyncDisposable
{
    public IServiceProvider Services => host.Services;
    public HttpClient CreateClient() => host.GetTestClient();

    public static async Task<DashboardTestHost> StartAsync(Action<IServiceCollection>? configure = null,
        string environment = "Development", Action<DashboardOptions>? configureDashboard = null)
    {
        var host = await new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .UseEnvironment(environment)
            .ConfigureServices((context, services) =>
            {
                context.HostingEnvironment.ApplicationName = typeof(BackgroundJobs.Demo.Jobs.DemoJobs).Assembly.GetName().Name!;
                services.AddBackgroundJobsDashboard(configure: configureDashboard);
                configure?.Invoke(services);
            })
            .Configure(app => app.Run(context => context.Response.WriteAsync("consumer endpoint"))))
            .StartAsync();
        return new DashboardTestHost(host);
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
    }
}
