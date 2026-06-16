using PoPunkouterSoftware.Shared.Azure;
using Radzen.Blazor;
using Radzen;

namespace PoPunkouterSoftware.Client.Components.Pages;

// Partial class — chart data helpers and badge-style helpers extracted from the
// main AzureDashboard.razor.cs to reduce its cognitive surface area.
public partial class AzureDashboard
{
    // ── Chart records ─────────────────────────────────────────────────────────
    private record ChartPoint(string Label, double Value);
    private record DailyCostChartPoint(string Date, double Cost);
    private record PlanUsagePoint(string Plan, int Apps, int Requests, int Errors);

    // ── Fleet health donut ────────────────────────────────────────────────────
    private List<ChartPoint> FleetHealthDonutData
    {
        get
        {
            var ws = report?.WebServices;
            if (ws == null)
                return new();
            return new List<ChartPoint>
            {
                new("Active", ws.ByStatus?.Active ?? 0),
                new("Broken", ws.ByStatus?.Broken ?? 0),
                new("Other",  ws.ByStatus?.Other  ?? 0),
            }.Where(p => p.Value > 0).ToList();
        }
    }

    // ── Bar chart data ────────────────────────────────────────────────────────
    private List<ChartPoint> CostDriversBarData =>
        report?.Cost?.TopCostDrivers
            .Where(d => d.Cost > 0)
            .Take(10)
            .Select(d => new ChartPoint(
                d.Name.Length > 40 ? d.Name[..37] + "…" : d.Name,
                Math.Round(d.Cost, 2)))
            .ToList() ?? new();

    private List<ChartPoint> ResponseTimeBarData =>
        services
            .Where(s => s.Connectivity?.ResponseTime > 0)
            .OrderByDescending(s => s.Connectivity!.ResponseTime)
            .Take(15)
            .Select(s =>
            {
                var lbl = s.FriendlyName ?? s.Name;
                return new ChartPoint(lbl.Length > 28 ? lbl[..25] + "…" : lbl, s.Connectivity!.ResponseTime);
            })
            .ToList();

    private List<ChartPoint> ErrorRateBarData =>
        services
            .Where(s => s.Metrics7Days?.Http5xx > 0)
            .OrderByDescending(s => s.Metrics7Days!.Http5xx)
            .Take(15)
            .Select(s =>
            {
                var lbl = s.FriendlyName ?? s.Name;
                return new ChartPoint(lbl.Length > 28 ? lbl[..25] + "…" : lbl, s.Metrics7Days!.Http5xx);
            })
            .ToList();

    private List<PlanUsagePoint> PlanUsageByPlan =>
        services
            .GroupBy(s => string.IsNullOrWhiteSpace(s.AppServicePlan)
                ? "Unassigned plan"
                : $"{s.AppServicePlan} ({s.AppServicePlanSku ?? "SKU unknown"})")
            .Select(g => new PlanUsagePoint(
                g.Key.Length > 36 ? g.Key[..33] + "…" : g.Key,
                g.Count(),
                g.Sum(s => s.Metrics7Days?.Requests ?? 0),
                g.Sum(s => s.Metrics7Days?.Http5xx ?? 0)))
            .OrderByDescending(p => p.Requests)
            .Take(8)
            .ToList();

    private List<ChartPoint> TopAppRequestChartData =>
        services
            .Where(s => (s.Metrics7Days?.Requests ?? 0) > 0)
            .OrderByDescending(s => s.Metrics7Days!.Requests)
            .Take(10)
            .Select(s => new ChartPoint(
                (s.FriendlyName ?? s.Name).Length > 30 ? (s.FriendlyName ?? s.Name)[..27] + "…" : (s.FriendlyName ?? s.Name),
                s.Metrics7Days!.Requests))
            .ToList();

    private static string HumanizeResourceType(string type) => type switch
    {
        "storageAccounts"        => "Storage Accounts",
        "sites"                  => "App Services",
        "userAssignedIdentities" => "Managed Identities",
        "workspaces"             => "Log Analytics",
        "vaults"                 => "Key Vaults",
        "components"             => "App Insights",
        "serverFarms"            => "App Service Plans",
        "accounts"               => "Cognitive Services",
        "servers"                => "SQL Servers",
        "databases"              => "SQL Databases",
        _                        => type,
    };

    private List<ChartPoint> ResourceTypeChartData =>
        report?.AllResourceSummary?.ByType
            .OrderByDescending(kv => kv.Value).Take(10)
            .Select(kv => new ChartPoint(HumanizeResourceType(kv.Key), kv.Value)).ToList() ?? new();

    private List<ChartPoint> ResourceTypeTableData =>
        report?.AllResourceSummary?.ByType
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new ChartPoint(HumanizeResourceType(kv.Key), kv.Value)).ToList() ?? new();

    private List<ChartPoint> CostByRgData
    {
        get
        {
            if (report?.Cost?.TopCostDrivers is null)
                return new();
            var byRg = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in report.Cost.TopCostDrivers)
            {
                var m = System.Text.RegularExpressions.Regex.Match(d.Name, @"\(([^)]+)\)$");
                var rg = m.Success ? m.Groups[1].Value : "Other";
                byRg[rg] = byRg.GetValueOrDefault(rg) + d.Cost;
            }
            return byRg.OrderByDescending(kv => kv.Value).Take(12)
                .Select(kv => new ChartPoint(kv.Key, Math.Round(kv.Value, 2))).ToList();
        }
    }

    private List<DailyCostChartPoint> BurnRateChartData =>
        report?.BurnRate?.DailyCosts
            .Select(d => new DailyCostChartPoint(
                DateTime.TryParse(d.Date, out var dt) ? dt.ToString("MMM d") : d.Date,
                d.Cost)).ToList() ?? new();

    private long StepTimingTotalMs => report?.StepTimings?.Sum(x => x.ElapsedMs) ?? 0;
    private StepTimingEntry? SlowestStepTiming => report?.StepTimings?.OrderByDescending(x => x.ElapsedMs).FirstOrDefault();

    private int BurnRateLabelStep =>
        BurnRateChartData.Count > 20 ? 5 :
        BurnRateChartData.Count > 10 ? 3 : 1;

    // ── CI/CD badge helpers ───────────────────────────────────────────────────
    private static BadgeStyle TargetBadge(string? target) => target switch
    {
        "Container Apps" => BadgeStyle.Info,
        "App Service" => BadgeStyle.Primary,
        "Static Web Apps" => BadgeStyle.Success,
        "Azure Functions" => BadgeStyle.Warning,
        "AKS" => BadgeStyle.Danger,
        "Container Instance" => BadgeStyle.Light,
        "ARM/Bicep Deploy" => BadgeStyle.Secondary,
        _ => BadgeStyle.Light,
    };

    private static BadgeStyle TriggerBadge(string trigger) => trigger switch
    {
        "push" => BadgeStyle.Primary,
        "pull_request" => BadgeStyle.Success,
        "workflow_dispatch" => BadgeStyle.Warning,
        "schedule" => BadgeStyle.Info,
        "release" => BadgeStyle.Danger,
        _ => BadgeStyle.Light,
    };

    private static BadgeStyle InfraFileBadge(string fileType) => fileType switch
    {
        "bicep" => BadgeStyle.Primary,
        "arm" => BadgeStyle.Info,
        "azd" => BadgeStyle.Success,
        "docker" => BadgeStyle.Warning,
        "compose" => BadgeStyle.Warning,
        _ => BadgeStyle.Light,
    };

    private static BadgeStyle ActionabilityBadge(string? tier) => tier switch
    {
        "Fix Now" => BadgeStyle.Danger,
        "Fix Soon" => BadgeStyle.Warning,
        "Remove Candidate" => BadgeStyle.Secondary,
        _ => BadgeStyle.Info,
    };
}
