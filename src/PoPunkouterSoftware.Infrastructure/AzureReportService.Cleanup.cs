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

    private async Task<List<OrphanedResource>> GetOrphanedResourcesAsync(
        List<GenericResourceData> allResources, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return [];
        var client = _httpClientFactory.CreateClient("azure-arm");

        // Fetches the ARM detail for one resource and hands the parsed JSON to `evaluate`,
        // which returns the orphan finding (or null when the resource is not orphaned).
        // Shared by all three categories below — they previously hand-rolled the same
        // request/parse/try-catch around three different type filters and evaluations.
        // Concurrency is gated by the shared BoundedParallelAsync call below, not here.
        async Task<OrphanedResource?> CheckAsync(
            GenericResourceData resource, string urlSuffix, string category,
            Func<JsonElement, GenericResourceData, OrphanedResource?> evaluate)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://management.azure.com{resource.Id}{urlSuffix}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    return null;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                return evaluate(doc.RootElement, resource);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{Category} orphan check failed for {Name}", category, resource.Name);
                return null;
            }
        }

        var diskItems = allResources
            .Where(r => r.ResourceType.ToString().Equals("Microsoft.Compute/disks", StringComparison.OrdinalIgnoreCase))
            .Select(disk => (Resource: disk, UrlSuffix: "?api-version=2023-10-02", Category: "Disk", Evaluate: (Func<JsonElement, GenericResourceData, OrphanedResource?>)((root, r) =>
            {
                if (!root.TryGetProperty("properties", out var props))
                    return null;
                if (!props.TryGetProperty("diskState", out var state) || state.GetString() != "Unattached")
                    return null;

                var sizeGb = props.TryGetProperty("diskSizeGB", out var sz) ? sz.GetInt32() : 0;
                var sku = root.TryGetProperty("sku", out var skuEl) &&
                             skuEl.TryGetProperty("name", out var skuName) ? skuName.GetString() : null;
                return new OrphanedResource
                {
                    Name = r.Name,
                    ResourceGroup = r.Id?.ResourceGroupName,
                    Type = "Managed Disk",
                    Reason = $"Unattached ({sizeGb} GB, {sku ?? "unknown SKU"})",
                    EstimatedMonthlyCost = sizeGb > 0 ? $"~${sizeGb * 0.04:F2}/mo" : null,
                    Command = $"az disk delete --name \"{r.Name}\" --resource-group \"{r.Id?.ResourceGroupName}\" --yes",
                };
            })));

        var ipItems = allResources
            .Where(r => r.ResourceType.ToString().Equals("Microsoft.Network/publicIPAddresses", StringComparison.OrdinalIgnoreCase))
            .Select(ip => (Resource: ip, UrlSuffix: "?api-version=2023-11-01", Category: "Public IP", Evaluate: (Func<JsonElement, GenericResourceData, OrphanedResource?>)((root, r) =>
            {
                if (!root.TryGetProperty("properties", out var props))
                    return null;

                var hasIpConfig = props.TryGetProperty("ipConfiguration", out _);
                var hasNatGateway = props.TryGetProperty("natGateway", out _);
                if (hasIpConfig || hasNatGateway)
                    return null;

                var sku = root.TryGetProperty("sku", out var skuEl) &&
                          skuEl.TryGetProperty("name", out var skuName) ? skuName.GetString() : null;
                return new OrphanedResource
                {
                    Name = r.Name,
                    ResourceGroup = r.Id?.ResourceGroupName,
                    Type = "Public IP",
                    Reason = $"Not associated with any NIC or NAT gateway (SKU: {sku ?? "—"})",
                    EstimatedMonthlyCost = sku == "Standard" ? "~$3.65/mo" : null,
                    Command = $"az network public-ip delete --name \"{r.Name}\" --resource-group \"{r.Id?.ResourceGroupName}\"",
                };
            })));

        var farmItems = allResources
            .Where(r => r.ResourceType.ToString().Equals("Microsoft.Web/serverFarms", StringComparison.OrdinalIgnoreCase))
            .Select(farm => (Resource: farm, UrlSuffix: "/sites?api-version=2023-12-01", Category: "App Service Plan", Evaluate: (Func<JsonElement, GenericResourceData, OrphanedResource?>)((root, r) =>
            {
                var siteCount = root.TryGetProperty("value", out var v) ? v.GetArrayLength() : 1;
                if (siteCount > 0)
                    return null;

                var sku = r.Sku?.Name?.ToString() ?? "unknown";
                return new OrphanedResource
                {
                    Name = r.Name,
                    ResourceGroup = r.Id?.ResourceGroupName,
                    Type = "App Service Plan",
                    Reason = $"No apps deployed (SKU: {sku})",
                    EstimatedMonthlyCost = sku is "F1" or "FREE" ? "$0/mo (Free)" : "Paid tier — check portal",
                    Command = $"az appservice plan delete --name \"{r.Name}\" --resource-group \"{r.Id?.ResourceGroupName}\" --yes",
                };
            })));

        // BoundedParallelAsync preserves input order (disks, then IPs, then plans) regardless
        // of completion order, so the result ordering matches the previous sequential passes.
        var results = await BoundedParallelAsync(
            diskItems.Concat(ipItems).Concat(farmItems), maxConcurrency: 6,
            item => CheckAsync(item.Resource, item.UrlSuffix, item.Category, item.Evaluate), ct);
        return results.OfType<OrphanedResource>().ToList();
    }
}
