using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace PoPunkouterSoftware;

/// <summary>
/// Lets the client discover the canonical API base URL, environment mode, and
/// feature/model availability. The <c>/api/whoami</c> endpoint exposes the
/// current principal populated by <see cref="FakeAuthHandler"/> so the UI can
/// render its Session / Logout slot.
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

        // Whoami — returns the current FakeAuth principal for the UI header.
        // Public, anonymous, and safe: only the (already-trusted) client knows
        // what header it sent.
        app.MapGet("/api/whoami",
            (HttpContext ctx) =>
            {
                var user = ctx.User;
                var name = user.Identity?.IsAuthenticated == true
                    ? user.Identity.Name ?? FakeAuthHandler.AnonymousUser
                    : FakeAuthHandler.AnonymousUser;
                var roles = user.Claims
                    .Where(c => c.Type == ClaimTypes.Role)
                    .Select(c => c.Value)
                    .ToArray();
                var isManagement = user.IsInRole(FakeAuthHandler.ManagementRole);
                var scheme = user.Identity?.AuthenticationType ?? FakeAuthHandler.SchemeName;
                return Results.Ok(new
                {
                    name,
                    isAuthenticated = user.Identity?.IsAuthenticated ?? false,
                    roles,
                    isManagement,
                    scheme,
                });
            })
            .WithName("GetWhoami").WithTags("Config");

        // Logout — the FakeAuth scheme is in-memory per request; the canonical
        // "logout" is to drop the X-Fake-User / X-Fake-Roles headers on the
        // client and reload. The endpoint exists so the client has a peer to
        // call from the Session/Logout slot and so the audit trail is uniform.
        app.MapPost("/api/logout", () => Results.NoContent())
            .WithName("Logout").WithTags("Config");

        // Impersonation — a management-only action that lets an admin drop
        // their X-Fake-User header to temporarily take a non-elevated
        // identity. Gated by the management policy.
        app.MapPost("/api/impersonate",
            [Authorize(Policy = "Management")] (HttpContext ctx) =>
            {
                var user = ctx.User.Identity?.Name ?? FakeAuthHandler.AnonymousUser;
                return Results.Ok(new { impersonatedAs = user });
            })
            .WithName("Impersonate").WithTags("Config");

        return app;
    }
}

