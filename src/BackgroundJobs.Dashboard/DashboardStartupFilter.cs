using BackgroundJobs.Dashboard.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BackgroundJobs.Dashboard;

internal sealed class DashboardStartupFilter(
    DashboardOptions options, IWebHostEnvironment environment, IConfiguration configuration) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // A build manifest resolves packaged assets; published applications use wwwroot.
        StaticWebAssetsLoader.UseStaticWebAssets(environment, configuration);

        // A separate builder keeps dashboard endpoints and middleware out of the consumer's routing table.
        var dashboard = new ApplicationBuilder(app.ApplicationServices, app.ServerFeatures);
        dashboard.Use(async (context, nextMiddleware) =>
        {
            if (!context.Request.Path.HasValue && HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.Redirect($"{context.Request.PathBase}/{context.Request.QueryString}");
                return;
            }
            await nextMiddleware();
        });
        dashboard.UseRouting();
        dashboard.UseAntiforgery();
        var manifest = Path.Combine(AppContext.BaseDirectory,
            $"{environment.ApplicationName}.staticwebassets.endpoints.json");
        dashboard.UseEndpoints(endpoints =>
        {
            endpoints.MapStaticAssets(manifest);
            endpoints.MapRazorComponents<DashboardApp>().AddInteractiveServerRenderMode().WithStaticAssets(manifest);
        });
        var handler = dashboard.Build();
        app.MapWhen(context => context.Request.Path.StartsWithSegments(options.Path), secured =>
        {
            // Authenticate and challenge before stripping the dashboard path, preserving the consumer's login URL.
            if (app.ApplicationServices.GetService<IAuthenticationSchemeProvider>() is not null)
                secured.UseAuthentication();
            secured.Use(async (context, nextMiddleware) =>
            {
                var policies = context.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
                var policy = options.AuthorizationPolicy is { } name
                    ? await policies.GetPolicyAsync(name) ?? throw new InvalidOperationException("Dashboard authorization policy not found.")
                    : await policies.GetFallbackPolicyAsync();
                if (policy is null) { await nextMiddleware(); return; }
                var evaluator = context.RequestServices.GetRequiredService<IPolicyEvaluator>();
                var authentication = await evaluator.AuthenticateAsync(policy, context);
                var result = await evaluator.AuthorizeAsync(policy, authentication, context, context);
                await context.RequestServices.GetRequiredService<IAuthorizationMiddlewareResultHandler>()
                    .HandleAsync(_ => nextMiddleware(), context, policy, result);
            });
            secured.Map(options.Path, branch => branch.Run(handler));
        });
        next(app);
    };
}
