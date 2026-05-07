using PoPunkouterSoftware.Infrastructure.Azure;

namespace PoPunkouterSoftware.Features.Azure;

/// <summary>
/// Exposes recent incident entries via /api/incidents.
/// </summary>
internal static class IncidentEndpoints
{
    internal static WebApplication MapIncidentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/incidents", async (
            IncidentService incidentService,
            int? limit,
            CancellationToken ct) =>
        {
            var entries = await incidentService.LoadRecentAsync(limit ?? 50, ct);
            return Results.Ok(entries);
        })
        .WithName("GetIncidents")
        .WithTags("Incidents");

        return app;
    }
}
