using PoPunkouterSoftware.Shared.Azure;
using PoPunkouterSoftware.Client.Components.Pages.Models;
using Radzen;

namespace PoPunkouterSoftware.Client.Components.Pages;

// Partial class — pure static portfolio-building and inference helpers extracted
// from AzureDashboard.razor.cs to keep the interactive part under ~400 lines.
public partial class AzureDashboard
{
    // ── Records ───────────────────────────────────────────────────────────────
    private record ConsolidatedService(
        string Key,
        string DisplayName,
        string ResourceTypeSummary,
        string HttpStatus,
        int Requests7d,
        int Http5xx7d,
        int? ResponseTimeMs,
        int HealthScore,
        int ReliabilityScore,
        string Actionability,
        bool HasAnomaly,
        string Owner,
        string Environment,
        string Criticality,
        string? ResourceGroup,
        string? Command
    );

    private record PriorityQueueItem(
        string Actionability,
        string Item,
        string Source,
        int ImpactScore,
        string Confidence,
        string Reason,
        string Owner,
        string Environment,
        string? Command
    );

    // ── Label / badge helpers ─────────────────────────────────────────────────
    private static string TypeLabel(string? t) => t switch
    {
        "Microsoft.Web/sites" => "App Service",
        "Microsoft.App/containerApps" => "Container App",
        "Microsoft.Web/staticSites" => "Static Web App",
        _ => t?.Split('/').LastOrDefault() ?? "—",
    };

    private static BadgeStyle HttpBadgeStyle(string? s) => s switch
    {
        "active" => BadgeStyle.Success,
        "broken" => BadgeStyle.Danger,
        _ => BadgeStyle.Warning,
    };

    // ── Safe-to-remove analysis ───────────────────────────────────────────────
    private static List<SafeToRemoveItem> BuildSafeToRemove(AzureReport? r)
    {
        if (r == null)
            return new();
        var items = new List<SafeToRemoveItem>();
        foreach (var svc in r.WebServices?.Services ?? new())
        {
            var zero = svc.Metrics7Days?.Requests == 0;
            var broken = svc.HttpStatus == "broken";
            var unreachable = svc.HttpStatus == "unreachable";
            var stopped = svc.PlatformState == "Stopped";
            var azErr = svc.Connectivity?.IsAzureErrorPage == true;
            if ((broken || unreachable || stopped || azErr) && zero)
            {
                var reasons = new List<string>();
                if (broken) reasons.Add("HTTP broken");
                if (unreachable) reasons.Add("Unreachable (timeout)");
                if (stopped) reasons.Add("Platform Stopped");
                if (azErr) reasons.Add("Serving Azure error page");
                if (zero) reasons.Add("0 requests in 7 days");
                items.Add(new SafeToRemoveItem
                {
                    Name = svc.Name,
                    ResourceGroup = svc.ResourceGroup,
                    Type = TypeLabel(svc.ResourceType),
                    Source = "Connectivity + Metrics",
                    Reason = string.Join(", ", reasons),
                    Confidence = broken ? "high" : "medium",
                    Command = svc.ResourceType == "Microsoft.Web/sites"
                        ? $"az webapp delete --name \"{svc.Name}\" --resource-group \"{svc.ResourceGroup}\""
                        : null,
                });
            }
        }

        foreach (var orphan in r.OrphanedResources ?? new())
        {
            items.Add(new SafeToRemoveItem
            {
                Name = orphan.Name,
                ResourceGroup = orphan.ResourceGroup,
                Type = orphan.Type,
                Source = "Orphaned resource scan",
                Reason = orphan.Reason,
                Confidence = orphan.EstimatedMonthlyCost?.Contains("Paid", StringComparison.OrdinalIgnoreCase) == true ? "high" : "medium",
                EstimatedMonthlyCost = orphan.EstimatedMonthlyCost,
                Command = orphan.Command,
            });
        }

        foreach (var plan in r.AppServicePlanInventory.Where(p => p.AppCount == 0))
        {
            items.Add(new SafeToRemoveItem
            {
                Name = plan.Name,
                ResourceGroup = plan.ResourceGroup,
                Type = "App Service Plan",
                Source = "Plan inventory",
                Reason = $"No apps assigned (SKU: {plan.Sku ?? "unknown"})",
                Confidence = "high",
                EstimatedMonthlyCost = string.Equals(plan.Sku, "F1", StringComparison.OrdinalIgnoreCase) ? "$0/mo" : "Paid tier",
                Command = $"az appservice plan delete --name \"{plan.Name}\" --resource-group \"{plan.ResourceGroup}\" --yes",
            });
        }

        foreach (var ai in r.AiServicesInventory.Where(a => a.RiskLevel is "cleanup"))
        {
            items.Add(new SafeToRemoveItem
            {
                Name = ai.Name,
                ResourceGroup = ai.ResourceGroup,
                Type = "AI Services",
                Source = "AI inventory",
                Reason = ai.Recommendation,
                Confidence = "medium",
                EstimatedMonthlyCost = ai.Sku is "S0" ? "Usage-based S0" : null,
                Command = $"az cognitiveservices account delete --name \"{ai.Name}\" --resource-group \"{ai.ResourceGroup}\"",
            });
        }

        foreach (var workspace in r.LogAnalyticsInventory.Where(w => w.RiskLevel is "cost"))
        {
            items.Add(new SafeToRemoveItem
            {
                Name = workspace.Name,
                ResourceGroup = workspace.ResourceGroup,
                Type = "Log Analytics",
                Source = "Log Analytics policy",
                Reason = workspace.Recommendation,
                Confidence = "medium",
                EstimatedMonthlyCost = "Variable ingestion",
                Command = null,
            });
        }

        items.Sort((a, b) =>
        {
            var o = new Dictionary<string, int> { ["high"] = 0, ["medium"] = 1, ["low"] = 2 };
            return o.GetValueOrDefault(a.Confidence) - o.GetValueOrDefault(b.Confidence);
        });
        return items
            .GroupBy(i => $"{i.Type}|{i.ResourceGroup}|{i.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    // ── Consolidated portfolio building ───────────────────────────────────────
    private static List<ConsolidatedService> BuildConsolidatedServices(AzureReport? r)
    {
        if (r is null)
            return new();
        var sourceServices = r.WebServices?.Services ?? new List<WebService>();
        if (sourceServices.Count == 0)
            return new();

        var grouped = sourceServices
            .GroupBy(s => CanonicalAppKey(s.FriendlyName, s.Name))
            .ToList();

        var raw = new List<ConsolidatedService>();
        foreach (var group in grouped)
        {
            var entries = group.ToList();
            var first = entries[0];
            var requests7d = entries.Sum(x => x.Metrics7Days?.Requests ?? 0);
            var http5xx7d = entries.Sum(x => x.Metrics7Days?.Http5xx ?? 0);
            var hasBroken = entries.Any(x =>
                x.HttpStatus is "broken" or "unreachable" ||
                x.Connectivity?.IsAzureErrorPage == true ||
                x.PlatformState == "Stopped");
            var rtCandidates = entries.Where(x => x.Connectivity?.Success == true)
                .Select(x => x.Connectivity!.ResponseTime).ToList();
            var responseMs = rtCandidates.Count > 0 ? (int?)Math.Round(rtCandidates.Average()) : null;
            var status = hasBroken ? "broken" : entries.Any(x => x.HttpStatus == "active") ? "active" : "other";

            var reliability = 100;
            reliability -= hasBroken ? 35 : 0;
            reliability -= Math.Min(30, http5xx7d * 3);
            reliability -= responseMs is > 3000 ? 20 : responseMs is > 1200 ? 10 : 0;
            reliability -= requests7d == 0 ? 10 : 0;
            reliability = Math.Clamp(reliability, 0, 100);

            var health = reliability;
            if (entries.Any(x => x.FreeTierCheck?.CanGoFree == true))
                health -= 5;
            health = Math.Clamp(health, 0, 100);

            raw.Add(new ConsolidatedService(
                Key: group.Key,
                DisplayName: string.IsNullOrWhiteSpace(first.FriendlyName) ? first.Name : first.FriendlyName,
                ResourceTypeSummary: string.Join(" + ", entries.Select(x => TypeLabel(x.ResourceType)).Distinct()),
                HttpStatus: status,
                Requests7d: requests7d,
                Http5xx7d: http5xx7d,
                ResponseTimeMs: responseMs,
                HealthScore: health,
                ReliabilityScore: reliability,
                Actionability: ToActionability(status, http5xx7d, requests7d),
                HasAnomaly: false,
                Owner: InferOwner(first.ResourceGroup, first.Name),
                Environment: InferEnvironment(first.ResourceGroup, first.Name),
                Criticality: InferCriticality(first.ResourceGroup, first.Name),
                ResourceGroup: first.ResourceGroup,
                Command: first.ResourceType == "Microsoft.Web/sites"
                    ? $"az webapp show --name \"{first.Name}\" --resource-group \"{first.ResourceGroup}\""
                    : null
            ));
        }

        var medianReq = Median(raw.Select(x => x.Requests7d).ToList());
        var medianRt = Median(raw.Where(x => x.ResponseTimeMs.HasValue)
            .Select(x => (double)x.ResponseTimeMs!.Value).ToList());

        return raw
            .Select(x => x with { HasAnomaly = IsAnomalous(x, medianReq, medianRt) })
            .OrderBy(x => x.HealthScore)
            .ThenBy(x => x.DisplayName)
            .ToList();
    }

    private static List<PriorityQueueItem> BuildPriorityQueue(
        AzureReport? r,
        List<ConsolidatedService> consolidated,
        List<SafeToRemoveItem> safe)
    {
        var items = new List<PriorityQueueItem>();

        foreach (var c in consolidated.Where(x => x.Actionability is "Fix Now" or "Fix Soon"))
        {
            items.Add(new PriorityQueueItem(
                Actionability: c.Actionability,
                Item: c.DisplayName,
                Source: "Reliability",
                ImpactScore: 100 - c.HealthScore,
                Confidence: c.HealthScore < 50 ? "high" : "medium",
                Reason: $"Status={c.HttpStatus}; 7d 5xx={c.Http5xx7d}; Reliability={c.ReliabilityScore}%",
                Owner: c.Owner,
                Environment: c.Environment,
                Command: c.Command
            ));
        }

        foreach (var s in safe)
        {
            items.Add(new PriorityQueueItem(
                Actionability: "Remove Candidate",
                Item: s.Name,
                Source: "SafeToRemove",
                ImpactScore: s.Confidence == "high" ? 75 : 55,
                Confidence: s.Confidence,
                Reason: s.Reason,
                Owner: "unassigned",
                Environment: "unknown",
                Command: s.Command
            ));
        }

        foreach (var ssl in r?.SslExpiry?.Where(x => x.DaysLeft is < 60).Take(20) ?? Enumerable.Empty<SslEntry>())
        {
            var days = ssl.DaysLeft ?? 0;
            items.Add(new PriorityQueueItem(
                Actionability: days < 14 ? "Fix Now" : "Fix Soon",
                Item: ssl.Name,
                Source: "SSL",
                ImpactScore: days < 14 ? 90 : 65,
                Confidence: "high",
                Reason: $"Certificate expires in {days} days",
                Owner: "unassigned",
                Environment: InferEnvironment(ssl.Name, ssl.Name),
                Command: null
            ));
        }

        foreach (var orphan in r?.OrphanedResources ?? new List<OrphanedResource>())
        {
            items.Add(new PriorityQueueItem(
                Actionability: "Remove Candidate",
                Item: orphan.Name,
                Source: "Orphaned",
                ImpactScore: 60,
                Confidence: "medium",
                Reason: orphan.Reason,
                Owner: "unassigned",
                Environment: InferEnvironment(orphan.ResourceGroup, orphan.Name),
                Command: orphan.Command
            ));
        }

        return items
            .OrderByDescending(i => i.ImpactScore)
            .ThenBy(i => i.Actionability)
            .Take(150)
            .ToList();
    }

    // ── Pure inference helpers ────────────────────────────────────────────────
    private static bool IsAnomalous(ConsolidatedService service, double medianReq, double medianRt)
    {
        var requestAnomaly = medianReq > 0 && service.Requests7d > medianReq * 3;
        var responseAnomaly = service.ResponseTimeMs.HasValue &&
                              medianRt > 0 &&
                              service.ResponseTimeMs.Value > medianRt * 2;
        var errorAnomaly = service.Http5xx7d > 5;
        return requestAnomaly || responseAnomaly || errorAnomaly;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        var ordered = values.OrderBy(v => v).ToList();
        var mid = ordered.Count / 2;
        return ordered.Count % 2 == 0 ? (ordered[mid - 1] + ordered[mid]) / 2 : ordered[mid];
    }

    private static double Median(List<int> values) => Median(values.Select(v => (double)v).ToList());

    private static string CanonicalAppKey(string? friendly, string? name)
    {
        var source = string.IsNullOrWhiteSpace(friendly) ? (name ?? "unknown") : friendly;
        return source.Trim().ToLowerInvariant();
    }

    private static string InferEnvironment(string? resourceGroup, string? name)
    {
        var text = $"{resourceGroup} {name}".ToLowerInvariant();
        if (text.Contains("prod") || text.Contains("production"))
            return "prod";
        if (text.Contains("dev") || text.Contains("test") || text.Contains("staging"))
            return "dev";
        return "shared";
    }

    private static string InferOwner(string? resourceGroup, string? name)
    {
        var token = (resourceGroup ?? name ?? "")
            .Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) ? "unassigned" : token.ToLowerInvariant();
    }

    private static string InferCriticality(string? resourceGroup, string? name)
    {
        var text = $"{resourceGroup} {name}".ToLowerInvariant();
        if (text.Contains("prod") || text.Contains("core") || text.Contains("api"))
            return "high";
        if (text.Contains("dev") || text.Contains("test"))
            return "low";
        return "medium";
    }

    private static string ToActionability(string status, int http5xx7d, int req7d)
    {
        if (status == "broken" || http5xx7d > 10)
            return "Fix Now";
        if (http5xx7d > 0 || (status != "active" && req7d > 0))
            return "Fix Soon";
        if (status != "active" && req7d == 0)
            return "Remove Candidate";
        return "Watch";
    }
}
