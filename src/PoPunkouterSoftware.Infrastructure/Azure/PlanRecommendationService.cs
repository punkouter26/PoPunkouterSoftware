using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.Infrastructure.Azure;

/// <summary>
/// Analyses each App Service against F1/B2 tier capabilities and produces
/// upgrade/downgrade/keep recommendations with cost impact and reasoning.
/// </summary>
public class PlanRecommendationService(ILogger<PlanRecommendationService> logger)
{
    private static readonly Dictionary<string, int> SkuMonthlyCosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["F1"] = 0,   ["FREE"] = 0,   ["D1"] = 0,
        ["B1"] = 54,  ["B2"] = 108,  ["B3"] = 216,
        ["S1"] = 73,  ["S2"] = 146,  ["S3"] = 292,
        ["P1V2"] = 146, ["P1V3"] = 107, ["P2V2"] = 292,
        ["P2V3"] = 214, ["P3V2"] = 584, ["P3V3"] = 428,
    };

    public List<PlanRecommendation> Analyze(
        List<WebService> services,
        List<ServiceDowntimeDiagnosis>? diagnoses,
        List<ConfigDriftItem>? configDrift)
    {
        var driftsByService = (configDrift ?? [])
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var diagsByService = (diagnoses ?? [])
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var recommendations = new List<PlanRecommendation>();

        foreach (var svc in services)
        {
            if (svc.ResourceType is not ("Microsoft.Web/sites" or "Microsoft.Web/sites/functions"))
                continue;

            var currentSku = svc.AppServicePlanSku;
            if (string.IsNullOrWhiteSpace(currentSku))
                continue;

            var planName = svc.AppServicePlan ?? "unknown";
            var rec = AnalyzeService(svc, currentSku, planName, driftsByService, diagsByService);
            if (rec is not null)
                recommendations.Add(rec);
        }

        logger.LogDebug("Generated {Count} plan recommendations from {Total} services",
            recommendations.Count, services.Count);

        return recommendations
            .OrderBy(r => r.Priority switch { "high" => 0, "medium" => 1, _ => 2 })
            .ThenBy(r => r.ServiceName)
            .ToList();
    }

    private PlanRecommendation? AnalyzeService(
        WebService svc,
        string currentSku,
        string planName,
        Dictionary<string, List<ConfigDriftItem>> driftMap,
        Dictionary<string, ServiceDowntimeDiagnosis> diagMap)
    {
        var triggers = new List<string>();
        var isFree = currentSku is "F1" or "FREE" or "D1";
        var isPaid = !isFree;
        var currentCost = SkuMonthlyCosts.TryGetValue(currentSku, out var cc) ? cc : (int?)null;

        diagMap.TryGetValue(svc.Name, out var diag);
        driftMap.TryGetValue(svc.Name, out var drifts);
        var allIssues = drifts?.SelectMany(d => d.Issues ?? []).ToList() ?? [];

        // ─── Upgrade path: F1 → B1/B2 ──────────────────────────────────
        if (isFree)
        {
            if (diag?.IsSuspended == true)
                triggers.Add("Quota-suspended — exceeded free-tier limits");

            if (diag?.UsageState == "Exceeded")
                triggers.Add("Usage state Exceeded — at free-tier CPU/memory limit");

            if (allIssues.Any(i => i.Issue.Contains("Always-On", StringComparison.OrdinalIgnoreCase)))
                triggers.Add("Always-On disabled (causing cold starts) — requires paid plan");

            if (svc.Metrics7Days is { Requests: > 0 })
            {
                if (svc.Metrics7Days.AverageResponseTime > 2000)
                    triggers.Add($"High avg response time ({svc.Metrics7Days.AverageResponseTime:F0}ms) — possible resource contention");

                if (svc.Metrics7Days.Http5xx > 0)
                    triggers.Add($"{svc.Metrics7Days.Http5xx} server errors in 7 days — possible memory/CPU exhaustion");
            }

            if (allIssues.Any(i => i.Issue.Contains("HTTP/2 disabled", StringComparison.OrdinalIgnoreCase)))
                triggers.Add("HTTP/2 disabled — enable for better performance");

            if (svc.Url.Contains(".azurewebsites.net") && HasCustomDomainIndicators(svc))
                triggers.Add("Likely needs custom domain — not supported on F1");

            if (triggers.Count > 0)
            {
                var targetSku = currentSku is "D1" ? "B1" : "B2";
                var targetCost = SkuMonthlyCosts.TryGetValue(targetSku, out var tc) ? tc : 108;

                return new PlanRecommendation
                {
                    ServiceName = svc.Name,
                    FriendlyName = svc.FriendlyName,
                    ResourceGroup = svc.ResourceGroup,
                    CurrentPlanName = planName,
                    CurrentPlanSku = currentSku,
                    RecommendedPlanSku = targetSku,
                    Action = "upgrade",
                    Reason = string.Join("; ", triggers),
                    Priority = triggers.Any(t => t.Contains("quota", StringComparison.OrdinalIgnoreCase) || t.Contains("suspended", StringComparison.OrdinalIgnoreCase)) ? "high" : "medium",
                    Triggers = triggers,
                    MonthlyCostImpact = currentCost.HasValue ? $"+${targetCost - currentCost:0}/mo" : $"+${targetCost:0}/mo",
                    CurrentMonthlyCost = currentCost.HasValue ? $"${currentCost:0}/mo" : null,
                    RecommendedMonthlyCost = $"${targetCost:0}/mo",
                };
            }

            // No triggers → keep
            return new PlanRecommendation
            {
                ServiceName = svc.Name,
                FriendlyName = svc.FriendlyName,
                ResourceGroup = svc.ResourceGroup,
                CurrentPlanName = planName,
                CurrentPlanSku = currentSku,
                RecommendedPlanSku = currentSku,
                Action = "keep",
                Reason = "App fits well within F1 limits — no issues detected.",
                Priority = "low",
                MonthlyCostImpact = null,
                CurrentMonthlyCost = $"$0/mo",
                RecommendedMonthlyCost = $"$0/mo",
            };
        }

        // ─── Downgrade path: Paid → F1 ─────────────────────────────────
        if (isPaid)
        {
            var isZombie = svc.Metrics7Days is { Requests: 0 } && !svc.Name.Contains("function", StringComparison.OrdinalIgnoreCase);
            var isLightweight = svc.Metrics7Days is { Requests: > 0, AverageResponseTime: < 500, Http5xx: 0 };

            if (isZombie)
                triggers.Add("Zero requests in 7 days — unused app wasting paid SKU");

            if (isLightweight && !HasCustomDomainIndicators(svc))
                triggers.Add($"Low-traffic app ({svc.Metrics7Days!.Requests} reqs/wk, {svc.Metrics7Days.AverageResponseTime:F0}ms avg) — fits F1 limits");

            if (!allIssues.Any(i => i.Issue.Contains("Always-On", StringComparison.OrdinalIgnoreCase)))
                triggers.Add("Always-On not required — F1 suitable");

            if (triggers.Count >= 2 || isZombie)
            {
                return new PlanRecommendation
                {
                    ServiceName = svc.Name,
                    FriendlyName = svc.FriendlyName,
                    ResourceGroup = svc.ResourceGroup,
                    CurrentPlanName = planName,
                    CurrentPlanSku = currentSku,
                    RecommendedPlanSku = "F1",
                    Action = "downgrade",
                    Reason = string.Join("; ", triggers),
                    Priority = isZombie ? "high" : "medium",
                    Triggers = triggers,
                    MonthlyCostImpact = currentCost.HasValue ? $"-${currentCost:0}/mo" : "cost reduction",
                    CurrentMonthlyCost = currentCost.HasValue ? $"${currentCost:0}/mo" : null,
                    RecommendedMonthlyCost = "$0/mo",
                };
            }

            // Paid and no reason to change
            return new PlanRecommendation
            {
                ServiceName = svc.Name,
                FriendlyName = svc.FriendlyName,
                ResourceGroup = svc.ResourceGroup,
                CurrentPlanName = planName,
                CurrentPlanSku = currentSku,
                RecommendedPlanSku = currentSku,
                Action = "keep",
                Reason = triggers.Count > 0
                    ? string.Join("; ", triggers) + " — but upgrade/downgrade not strongly indicated."
                    : "App is well-provisioned on current paid tier.",
                Priority = "low",
                MonthlyCostImpact = null,
                CurrentMonthlyCost = currentCost.HasValue ? $"${currentCost:0}/mo" : null,
                RecommendedMonthlyCost = currentCost.HasValue ? $"${currentCost:0}/mo" : null,
            };
        }

        return null;
    }

    private static bool HasCustomDomainIndicators(WebService svc)
        => !string.IsNullOrEmpty(svc.Url)
        && !svc.Url.Contains(".azurewebsites.net", StringComparison.OrdinalIgnoreCase)
        && !svc.Url.Contains("azure-api.net", StringComparison.OrdinalIgnoreCase);
}
