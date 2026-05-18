using Microsoft.Extensions.Caching.Memory;
using PoPunkouterSoftware.Shared.Azure;
using PoPunkouterSoftware.Infrastructure.Azure;
using System.Text;
using System.Text.Json;

namespace PoPunkouterSoftware.Features.Azure;

/// <summary>
/// Generates an AI-powered, step-by-step repair plan for a broken or unhealthy Azure App Service.
/// Grounded in the latest AzureReport data to minimise hallucinations.
/// </summary>
internal static class FixPlanEndpoints
{
    internal static WebApplication MapFixPlanEndpoints(this WebApplication app)
    {
        // NOTE: GET not POST — this is an idempotent read operation.
        // The client (FixPlanPanel.razor) sends GET. The AI call is cached
        // server-side, so repeated GETs do not re-invoke OpenAI.
        app.MapGet("/api/diag/fix-plan/{serviceName}", async (
            string serviceName,
            AzureReportStore repository,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            IConfiguration config,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            // ── Feature flag + OpenAI config guard ──────────────────────────────────
            var (blocked, ai) = AiEndpointGuard.Validate(config);
            if (blocked is not null) return blocked;

            // ── Cache hit ─────────────────────────────────────────────────────
            var cacheKey = $"fix-plan:{serviceName.ToLowerInvariant()}";
            if (cache.TryGetValue(cacheKey, out string? cached))
                return Results.Ok(new { plan = cached });

            // ── Load report & locate service ──────────────────────────────────
            var reportResult = await repository.LoadAsync(ct);
            if (!reportResult.IsSuccess || reportResult.Value is null)
                return Results.NotFound(new { error = reportResult.Error ?? "No Azure report found. Run a refresh first." });

            var report = reportResult.Value;
            var service = report.WebServices?.Services.FirstOrDefault(s =>
                s.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase) ||
                s.FriendlyName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));

            if (service is null)
                return Results.NotFound(new { error = $"Service '{serviceName}' not found in the latest report." });


            // ── Build prompt from report data ─────────────────────────────────
            var driftItems = report?.ConfigDrift?
                .Where(d => d.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase) ||
                            (d.FriendlyName?.Equals(serviceName, StringComparison.OrdinalIgnoreCase) == true))
                .ToList() ?? new();

            var prompt = BuildPrompt(service, driftItems);

            // ── Call Azure OpenAI ─────────────────────────────────────────────
            try
            {
                var plan = await AzureOpenAiClient.GetCompletionAsync(
                    httpClientFactory,
                    ai!.Endpoint,
                    ai.ApiKey,
                    ai.Deployment,
                    systemPrompt: "You are an Azure infrastructure expert. Produce concise, actionable fix plans for broken or unhealthy Azure App Services. Use numbered steps. Be specific and include az CLI commands where applicable. Do not include preamble — start directly with the numbered list.",
                    userPrompt: prompt,
                    maxTokens: 800,
                    temperature: 0.3,
                    logger,
                    ct);

                if (plan is null)
                    return Results.Problem(
                        detail: "Azure OpenAI call failed. Check your endpoint, key, and deployment name.",
                        statusCode: 502);

                cache.Set(cacheKey, plan, TimeSpan.FromHours(4));
                return Results.Ok(new { plan });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fix plan generation failed for service {ServiceName}", serviceName);
                return Results.Problem(ex.Message, statusCode: 500);
            }
        })
        .WithName("GenerateFixPlan")
        .WithTags("Diag");

        return app;
    }

    private static string BuildPrompt(WebService service, List<ConfigDriftItem> driftItems)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Azure App Service name: {service.Name}");
        if (!string.IsNullOrEmpty(service.FriendlyName))
            sb.AppendLine($"Friendly name: {service.FriendlyName}");
        sb.AppendLine($"Resource group: {service.ResourceGroup}");
        sb.AppendLine($"Resource type: {service.ResourceType}");
        sb.AppendLine($"HTTP status: {service.HttpStatus}");
        sb.AppendLine($"Platform state: {service.PlatformState ?? "Unknown"}");

        if (service.Connectivity is not null)
        {
            sb.AppendLine($"Connectivity success: {service.Connectivity.Success}");
            sb.AppendLine($"Response time: {service.Connectivity.ResponseTime} ms");
            if (!string.IsNullOrEmpty(service.Connectivity.Error))
                sb.AppendLine($"Connectivity error: {service.Connectivity.Error}");
            if (service.Connectivity.IsAzureErrorPage == true)
                sb.AppendLine("Note: Service is serving an Azure default error/splash page.");
        }

        if (service.Metrics7Days is not null)
        {
            sb.AppendLine($"7-day requests: {service.Metrics7Days.Requests}");
            sb.AppendLine($"7-day 5xx errors: {service.Metrics7Days.Http5xx}");
            sb.AppendLine($"7-day avg response time: {service.Metrics7Days.AverageResponseTime:F0} ms");
        }

        if (driftItems.Count > 0)
        {
            sb.AppendLine("\nConfiguration issues detected:");
            foreach (var d in driftItems)
                foreach (var issue in d.Issues ?? new())
                    sb.AppendLine($"  [{issue.Severity}] {issue.Issue}");
        }

        sb.AppendLine("\nProvide a numbered, step-by-step fix plan with specific az CLI commands where applicable.");
        return sb.ToString();
    }
}
