using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Steps 10-12: the cleanup candidates - zombie apps, apps.json drift, and orphaned resources.
/// </summary>
public partial class AzureReportService
{
    private static List<ZombieApp> DetectZombies(List<RawService> services, Dictionary<string, MetricsInfo> metricsMap)
        => services
            .Where(s => s.ResourceTypeRaw == "Microsoft.Web/sites" && s.ResourceId is not null)
            // Exclude WebSocket-only, SignalR, and background worker services:
            // - SignalR hubs (kind: "signalr") don't serve HTTP pages
            // - WebJob/background services are not front-end web apps
            // - Kind containing "functionapp" are Azure Functions, not web apps
            .Where(s => !string.IsNullOrEmpty(s.Kind) &&
                        !s.Kind.Contains("signalr", StringComparison.OrdinalIgnoreCase) &&
                        !s.Kind.Contains("functionapp", StringComparison.OrdinalIgnoreCase) &&
                        !s.Kind.Contains("workflowapp", StringComparison.OrdinalIgnoreCase) &&
                        s.PlatformState != "Stopped")
            .Where(s => metricsMap.TryGetValue(s.ResourceId!, out var m) && m.Requests == 0)
            .Select(s => new ZombieApp
            {
                Name = s.Name,
                ResourceGroup = s.ResourceGroup,
                HttpStatus = s.HttpStatus,
                PlatformState = s.PlatformState,
                Recommendation = $"az webapp stop --name \"{s.Name}\" --resource-group \"{s.ResourceGroup}\"",
            })
            .ToList();

    /// <summary>Diffs current discovered services against the snapshot stored in apps.json.</summary>
    private async Task<AppsJsonDiffInfo?> DiffAppsJsonAsync(List<RawService> services, CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(_env.WebRootPath, "data", "apps.json");
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, ct);
            using var doc = JsonDocument.Parse(json);
            var existing = doc.RootElement.TryGetProperty("apps", out var appsEl)
                ? appsEl.EnumerateArray()
                    .Select(a => a.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .Where(id => id is not null)
                    .ToHashSet()!
                : new HashSet<string?>();

            var discovered = services.Select(s => GetCanonicalName(s.Name)).ToHashSet();
            return new AppsJsonDiffInfo
            {
                CurrentCount = existing.Count,
                DiscoveredCount = discovered.Count,
                NewApps = discovered.Except(existing).ToList()!,
                RemovedApps = existing.Except(discovered).ToList()!,
                UpdatedApps = discovered.Intersect(existing).ToList()!,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "apps.json diff failed");
            return null;
        }
    }

    private async Task<List<OrphanedResource>> GetOrphanedResourcesAsync(
        List<GenericResourceData> allResources, string? armToken, CancellationToken ct)
    {
        var orphans = new List<OrphanedResource>();
        if (armToken is null)
            return orphans;
        var client = _httpClientFactory.CreateClient("azure-arm");

        // 1 — Unattached managed disks
        foreach (var disk in allResources.Where(r =>
            r.ResourceType.ToString().Equals("Microsoft.Compute/disks", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://management.azure.com{disk.Id}?api-version=2023-10-02");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken!);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("properties", out var props))
                    continue;
                if (!props.TryGetProperty("diskState", out var state) || state.GetString() != "Unattached")
                    continue;

                var sizeGb = props.TryGetProperty("diskSizeGB", out var sz) ? sz.GetInt32() : 0;
                var sku = doc.RootElement.TryGetProperty("sku", out var skuEl) &&
                             skuEl.TryGetProperty("name", out var skuName) ? skuName.GetString() : null;
                orphans.Add(new OrphanedResource
                {
                    Name = disk.Name,
                    ResourceGroup = disk.Id?.ResourceGroupName,
                    Type = "Managed Disk",
                    Reason = $"Unattached ({sizeGb} GB, {sku ?? "unknown SKU"})",
                    EstimatedMonthlyCost = sizeGb > 0 ? $"~${sizeGb * 0.04:F2}/mo" : null,
                    Command = $"az disk delete --name \"{disk.Name}\" --resource-group \"{disk.Id?.ResourceGroupName}\" --yes",
                });
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Disk orphan check failed for {Name}", disk.Name); }
        }

        // 2 — Unattached public IPs
        foreach (var ip in allResources.Where(r =>
            r.ResourceType.ToString().Equals("Microsoft.Network/publicIPAddresses", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://management.azure.com{ip.Id}?api-version=2023-11-01");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken!);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("properties", out var props))
                    continue;

                var hasIpConfig = props.TryGetProperty("ipConfiguration", out _);
                var hasNatGateway = props.TryGetProperty("natGateway", out _);
                if (hasIpConfig || hasNatGateway)
                    continue;

                var sku = doc.RootElement.TryGetProperty("sku", out var skuEl) &&
                          skuEl.TryGetProperty("name", out var skuName) ? skuName.GetString() : null;
                orphans.Add(new OrphanedResource
                {
                    Name = ip.Name,
                    ResourceGroup = ip.Id?.ResourceGroupName,
                    Type = "Public IP",
                    Reason = $"Not associated with any NIC or NAT gateway (SKU: {sku ?? "—"})",
                    EstimatedMonthlyCost = sku == "Standard" ? "~$3.65/mo" : null,
                    Command = $"az network public-ip delete --name \"{ip.Name}\" --resource-group \"{ip.Id?.ResourceGroupName}\"",
                });
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Public IP orphan check failed for {Name}", ip.Name); }
        }

        // 3 — Empty App Service Plans
        foreach (var farm in allResources.Where(r =>
            r.ResourceType.ToString().Equals("Microsoft.Web/serverFarms", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://management.azure.com{farm.Id}/sites?api-version=2023-12-01");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken!);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var siteCount = doc.RootElement.TryGetProperty("value", out var v) ? v.GetArrayLength() : 1;
                if (siteCount > 0)
                    continue;

                var sku = farm.Sku?.Name?.ToString() ?? "unknown";
                orphans.Add(new OrphanedResource
                {
                    Name = farm.Name,
                    ResourceGroup = farm.Id?.ResourceGroupName,
                    Type = "App Service Plan",
                    Reason = $"No apps deployed (SKU: {sku})",
                    EstimatedMonthlyCost = sku is "F1" or "FREE" ? "$0/mo (Free)" : "Paid tier — check portal",
                    Command = $"az appservice plan delete --name \"{farm.Name}\" --resource-group \"{farm.Id?.ResourceGroupName}\" --yes",
                });
            }
            catch (Exception ex) { _logger.LogDebug(ex, "App Service Plan orphan check failed for {Name}", farm.Name); }
        }

        return orphans;
    }
}
