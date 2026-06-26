using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Infrastructure.Azure;

namespace PoPunkouterSoftware.Features.Azure;

/// <summary>
/// Exposes pinger status and per-service toggle via /api/pinger/*.
/// </summary>
internal static class PingerEndpoints
{
    internal static WebApplication MapPingerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pinger").WithTags("Pinger");

        // ── Current snapshot of all last ping results ─────────────────────────
        group.MapGet("/status", (ServicePingerService pinger) =>
        {
            var snap = pinger.CurrentSnapshot();
            if (snap is null)
                return Results.Ok(new { swept = false, results = Array.Empty<object>() });

            return Results.Ok(new
            {
                swept = true,
                sweptAt = snap.SweptAt,
                results = snap.Results.Select(r => new
                {
                    r.Name,
                    r.FriendlyName,
                    r.Url,
                    r.Status,
                    r.ResponseTimeMs,
                    r.Error,
                    r.PingedAt,
                    disabled = pinger.IsDisabled(r.Name),
                }),
            });
        });

        // ── Toggle a specific service on or off ───────────────────────────────
        // POST /api/pinger/toggle/{name}?disable=true
        group.MapPost("/toggle/{name}", (string name, bool? disable, ServicePingerService pinger) =>
        {
            var shouldDisable = disable ?? true;
            pinger.SetDisabled(name, shouldDisable);
            return Results.Ok(new { name, disabled = shouldDisable });
        })
        .RequireManagementActions();

        return app;
    }
}
