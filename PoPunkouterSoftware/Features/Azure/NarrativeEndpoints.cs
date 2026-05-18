using Microsoft.Extensions.Caching.Memory;
using PoPunkouterSoftware.Shared.Azure;
using PoPunkouterSoftware.Infrastructure.Azure;
using System.Text.Json;

namespace PoPunkouterSoftware.Features.Azure;

/// <summary>
/// Generates an AI-authored executive narrative summarising the health and activity
/// of the entire Azure service portfolio. Returned as a short paragraph for the
/// public-facing Index page.
/// </summary>
internal static class NarrativeEndpoints
{
    internal static WebApplication MapNarrativeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/narrative", async (
            AzureReportStore repository,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            IConfiguration config,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            const string cacheKey = "narrative";

            // ── Feature flag + OpenAI config guard ────────────────────────────
            var (blocked, ai) = AiEndpointGuard.Validate(config);
            if (blocked is not null) return blocked;

            // ── Cache hit ─────────────────────────────────────────────────────
            if (cache.TryGetValue(cacheKey, out string? cached))
                return Results.Ok(new { narrative = cached, generatedAt = (DateTime?)null, cached = true });

            // ── Load latest report ────────────────────────────────────────────
            var result = await repository.LoadAsync(ct);
            if (!result.IsSuccess || result.Value is null)
                return Results.Ok(new
                {
                    narrative = (string?)null,
                    disabled = false,
                    message = "No Azure report available yet. Run a refresh first."
                });

            var report = result.Value;
            var services = report.WebServices?.Services ?? new List<WebService>();

            // ── Build prompt ─────────────────────────────────────────────────
            var active = services.Count(s => s.HttpStatus == "active");
            var broken = services.Count(s => s.HttpStatus is "broken" or "unreachable");
            var total = services.Count;

            var fastestSvc = services
                .Where(s => s.Metrics7Days?.AverageResponseTime > 0)
                .OrderBy(s => s.Metrics7Days!.AverageResponseTime)
                .FirstOrDefault();

            var svcList = string.Join(", ",
                services.OrderByDescending(s => s.HttpStatus == "active" ? 1 : 0)
                        .Take(8)
                        .Select(s => $"{s.FriendlyName ?? s.Name} ({s.HttpStatus})"));

            var cost = report.Cost?.TotalFormatted ?? "unknown";
            var burn = report.BurnRate?.ProjectedFormatted ?? "unknown";
            var generatedAt = report.GeneratedAt?.ToString("yyyy-MM-dd") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");

            var systemPrompt =
                "You are an expert cloud architect writing an executive portfolio narrative. " +
                "Write 2–3 flowing sentences (≤80 words) summarising the current health and activity of an Azure service portfolio. " +
                "Be factual, confident, and use plain English — no bullet points, no markdown headers. " +
                "Do not start with 'I' or 'The portfolio'. Start directly with an observation.";

            var userPrompt =
                $"Report date: {generatedAt}. " +
                $"Portfolio: {total} services — {active} active, {broken} broken/unreachable. " +
                $"Services: {svcList}. " +
                $"Last 30-day cost: {cost}, projected monthly: {burn}. " +
                (fastestSvc is not null ? $"Best-performing service: {fastestSvc.FriendlyName ?? fastestSvc.Name} at {fastestSvc.Metrics7Days!.AverageResponseTime}ms avg. " : "") +
                "Write the portfolio narrative.";

            // ── Call Azure OpenAI ─────────────────────────────────────────────
            try
            {
                var narrative = await AzureOpenAiClient.GetCompletionAsync(
                    httpClientFactory,
                    ai!.Endpoint,
                    ai.ApiKey,
                    ai.Deployment,
                    systemPrompt,
                    userPrompt,
                    maxTokens: 200,
                    temperature: 0.5,
                    logger,
                    ct);

                if (narrative is null)
                    return Results.Problem(detail: "Azure OpenAI call failed.", statusCode: 502);

                cache.Set(cacheKey, narrative, TimeSpan.FromHours(24));
                logger.LogInformation("Narrative generated ({Chars} chars) and cached 24h", narrative.Length);

                return Results.Ok(new { narrative, generatedAt = DateTime.UtcNow, cached = false });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Narrative generation failed");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        })
        .WithName("GetPortfolioNarrative")
        .WithTags("AI");

        return app;
    }
}
