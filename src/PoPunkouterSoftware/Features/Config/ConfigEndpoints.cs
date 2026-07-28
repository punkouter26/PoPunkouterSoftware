namespace PoPunkouterSoftware.Features.Config;

/// <summary>
/// Lets the client discover the canonical API base URL, environment mode, and
/// feature/model availability. This site has no authentication — there are
/// deliberately no auth/login flags here.
/// </summary>
internal static class ConfigEndpoints
{
    internal static WebApplication MapConfigEndpoints(this WebApplication app)
    {
        // Exactly the three fields AppJsonContext.ConfigResponse deserialises — nothing
        // more. Every extra field previously returned here (isProduction, AI flags, a
        // hardcoded model catalogue) was discarded on arrival by the client.
        //
        // isMockMode=true tells the UI to display the "MOCK DATA" banner (rule 10).
        // Activated when ASPNETCORE_ENVIRONMENT is "Testing" (integration / E2E test runs).
        // NOTE: no connection strings or keys are echoed here — the browser needs to know
        // which capabilities are on, not whether a secret is present. That stays server-side.
        app.MapGet("/api/config",
            (HttpContext ctx, IWebHostEnvironment env, IConfiguration config) =>
                Results.Ok(new
                {
                    apiBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api",
                    isMockMode = env.IsEnvironment("Testing"),
                    managementActionsEnabled = config.GetValue<bool>("FeatureFlags:EnableManagementActions", env.IsDevelopment() || env.IsEnvironment("Testing")),
                }))
            .WithName("GetConfig").WithTags("Config");

        return app;
    }
}
