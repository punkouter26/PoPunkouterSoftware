using System.Security.Cryptography;
using System.Text;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Server-side gate for mutating / expensive control endpoints.
///
/// This site has no user authentication by design (see AGENT.MD) — there is no login.
/// Instead, write and expensive control actions are enforced with the same
/// <c>FeatureFlags:EnableManagementActions</c> flag the UI reads from <c>/api/config</c>,
/// plus an optional shared key. Before this filter the flag was UI-only, so every
/// mutating endpoint (refresh, pinger toggle, cache bust) was open to anonymous callers.
/// </summary>
internal sealed class ManagementActionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var env = http.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var config = http.RequestServices.GetRequiredService<IConfiguration>();

        // Mirrors the default surfaced by /api/config: management actions are on in
        // Development / Testing, and otherwise only when explicitly enabled.
        var enabled = config.GetValue("FeatureFlags:EnableManagementActions",
            env.IsDevelopment() || env.IsEnvironment("Testing"));
        if (!enabled)
            return Results.Problem(
                detail: "Management actions are disabled in this environment.",
                statusCode: StatusCodes.Status403Forbidden);

        // Defence-in-depth: when a key is configured (e.g. via Key Vault in Production)
        // callers must present it in X-Management-Key. No key configured → flag gate only.
        var adminKey = config["Security:ManagementApiKey"];
        if (!string.IsNullOrWhiteSpace(adminKey))
        {
            var presented = http.Request.Headers["X-Management-Key"].ToString();
            if (string.IsNullOrEmpty(presented) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(adminKey)))
                return Results.Problem(
                    detail: "A valid X-Management-Key header is required for this action.",
                    statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }
}

internal static class ManagementActionFilterExtensions
{
    /// <summary>Gate a route behind <see cref="ManagementActionFilter"/>.</summary>
    public static RouteHandlerBuilder RequireManagementActions(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<ManagementActionFilter>();
}
