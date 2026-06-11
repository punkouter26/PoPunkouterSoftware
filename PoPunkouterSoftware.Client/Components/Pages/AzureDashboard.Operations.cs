using PoPunkouterSoftware.Client.Components.Pages.Models;
using PoPunkouterSoftware.Shared.Azure;
using Radzen;

namespace PoPunkouterSoftware.Client.Components.Pages;

public partial class AzureDashboard
{
    private record CostWatchItem(string Name, double Cost, string Area);

    private record HealthReportCard(
        int TotalResources,
        int PaidPlanCount,
        int CleanupCandidates,
        int AiCostRisks,
        int LogAnalyticsRisks,
        string MonthlyCost,
        string ProjectedCost,
        string Summary,
        List<string> Recommendations);

    private List<SafeToRemoveItem> CleanupCandidates =>
        safeToRemove
            .OrderBy(i => ConfidenceRank(i.Confidence))
            .ThenBy(i => i.Type)
            .ThenBy(i => i.Name)
            .ToList();

    private List<AiServiceInventoryItem> AiServices =>
        report?.AiServicesInventory
            .OrderByDescending(a => a.RiskLevel is "cost" or "cleanup")
            .ThenBy(a => a.Name)
            .ToList() ?? new();

    private List<LogAnalyticsWorkspaceItem> LogAnalyticsWorkspaces =>
        report?.LogAnalyticsInventory
            .OrderByDescending(w => w.RiskLevel == "cost")
            .ThenBy(w => w.Name)
            .ToList() ?? new();

    private List<CostWatchItem> AiCostDrivers =>
        BuildCostWatchItems("AI", "cognitive", "openai", "ai services", "azure ai");

    private List<CostWatchItem> LogAnalyticsCostDrivers =>
        BuildCostWatchItems("Log Analytics", "log analytics", "monitor", "operational insights");

    private HealthReportCard HealthReport => BuildHealthReport(report);

    private bool NeedsMaintenanceInventoryRefresh => ReportNeedsMaintenanceInventoryRefresh(report);

    private string MaintenanceInventoryRefreshReason =>
        report?.GeneratedAt is DateTime generatedAt && DateTime.UtcNow - generatedAt > TimeSpan.FromHours(12)
            ? $"Latest report was generated {FormatAge(DateTime.UtcNow - generatedAt)}."
            : "Latest report was generated before AI Services and Log Analytics inventory fields were available.";

    private static int ConfidenceRank(string? confidence) => confidence switch
    {
        "high" => 0,
        "medium" => 1,
        "low" => 2,
        _ => 3,
    };

    private static BadgeStyle ConfidenceBadge(string? confidence) => confidence switch
    {
        "high" => BadgeStyle.Danger,
        "medium" => BadgeStyle.Warning,
        "low" => BadgeStyle.Info,
        _ => BadgeStyle.Light,
    };

    private static BadgeStyle RiskBadge(string? risk) => risk switch
    {
        "cleanup" => BadgeStyle.Danger,
        "cost" => BadgeStyle.Warning,
        "watch" => BadgeStyle.Info,
        "ok" => BadgeStyle.Success,
        _ => BadgeStyle.Light,
    };

    private List<CostWatchItem> BuildCostWatchItems(string area, params string[] needles)
    {
        return report?.Cost?.TopCostDrivers
            .Where(d => needles.Any(n => d.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(d => d.Cost)
            .Select(d => new CostWatchItem(d.Name, Math.Round(d.Cost, 2), area))
            .ToList() ?? new();
    }

    private static HealthReportCard BuildHealthReport(AzureReport? current)
    {
        if (current is null)
        {
            return new HealthReportCard(0, 0, 0, 0, 0, "$0.00", "unknown", "No Azure report is loaded.", []);
        }

        var paidPlans = current.AppServicePlanInventory
            .Count(p => !string.Equals(p.Sku, "F1", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(p.Sku, "Free", StringComparison.OrdinalIgnoreCase));
        var cleanup = (current.OrphanedResources?.Count ?? 0)
            + (current.ZombieApps?.Count ?? 0)
            + current.AppServicePlanInventory.Count(p => p.AppCount == 0)
            + current.AiServicesInventory.Count(a => a.RiskLevel == "cleanup");
        var aiRisks = current.AiServicesInventory.Count(a => a.RiskLevel is "cost" or "cleanup");
        var logRisks = current.LogAnalyticsInventory.Count(w => w.RiskLevel == "cost");
        var broken = current.WebServices?.ByStatus?.Broken ?? 0;
        var total = current.WebServices?.Total ?? 0;

        var recommendations = new List<string>();
        if (cleanup > 0)
            recommendations.Add($"Review {cleanup} cleanup candidate(s) before deleting anything.");
        if (aiRisks > 0)
            recommendations.Add($"Audit {aiRisks} AI service cost risk(s), especially S0 accounts and deployments.");
        if (logRisks > 0)
            recommendations.Add($"Set or verify daily caps on {logRisks} Log Analytics workspace(s).");
        if (paidPlans > 1)
            recommendations.Add("Check whether paid App Service Plans can be consolidated.");
        if (broken > 0)
            recommendations.Add($"Fix or stop {broken} broken service(s) so they do not hide real incidents.");
        if (recommendations.Count == 0)
            recommendations.Add("No immediate cleanup actions. Keep monitoring weekly.");

        var summary = total == 0
            ? "No web services discovered in the latest scan."
            : $"{total - broken}/{total} services healthy, {cleanup} cleanup candidate(s), {aiRisks + logRisks} spend-control risk(s).";

        return new HealthReportCard(
            TotalResources: current.AllResourceSummary?.Total ?? 0,
            PaidPlanCount: paidPlans,
            CleanupCandidates: cleanup,
            AiCostRisks: aiRisks,
            LogAnalyticsRisks: logRisks,
            MonthlyCost: current.Cost?.TotalFormatted ?? "$0.00",
            ProjectedCost: current.BurnRate?.ProjectedFormatted ?? "unknown",
            Summary: summary,
            Recommendations: recommendations);
    }

    private static bool ReportNeedsMaintenanceInventoryRefresh(AzureReport? current)
    {
        if (current is null)
            return false;

        if (current.GeneratedAt is DateTime generatedAt && DateTime.UtcNow - generatedAt > TimeSpan.FromHours(12))
            return true;

        var resources = current.AllResourceSummary?.ResourcesByType.Values.SelectMany(x => x).ToList() ?? [];
        var hasAiOrLogs = resources.Any(r =>
            string.Equals(r.Type, "Microsoft.CognitiveServices/accounts", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Type, "Microsoft.OperationalInsights/workspaces", StringComparison.OrdinalIgnoreCase));

        return hasAiOrLogs &&
            current.AiServicesInventory.Count == 0 &&
            current.LogAnalyticsInventory.Count == 0;
    }
}
