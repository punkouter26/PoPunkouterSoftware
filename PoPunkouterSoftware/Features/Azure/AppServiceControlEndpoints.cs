using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Resources;

namespace PoPunkouterSoftware.Features.Azure;

/// <summary>
/// Exposes one-click management actions (restart, scale to free) for Azure App Services.
/// Uses DefaultAzureCredential via the existing AzureClientFactory pattern.
/// </summary>
internal static class AppServiceControlEndpoints
{
    internal static WebApplication MapAppServiceControlEndpoints(this WebApplication app)
    {
        // ── Restart an App Service ────────────────────────────────────────────
        app.MapPost("/api/manage/restart/{resourceGroup}/{name}", async (
            string resourceGroup,
            string name,
            IConfiguration config,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            try
            {
                var client = GetArmClient(config);
                var subscriptionId = config["Azure:SubscriptionId"];
                if (string.IsNullOrWhiteSpace(subscriptionId))
                    return Results.Problem("Azure:SubscriptionId is not configured.", statusCode: 503);

                var siteId = WebSiteResource.CreateResourceIdentifier(subscriptionId, resourceGroup, name);
                var site   = client.GetWebSiteResource(siteId);
                await site.RestartAsync(cancellationToken: ct);

                logger.LogInformation("App Service {Name} in {ResourceGroup} restarted successfully", name, resourceGroup);
                return Results.Ok(new { success = true, message = $"{name} restart initiated." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to restart App Service {Name} in {ResourceGroup}", name, resourceGroup);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        // ── Scale App Service to Free tier (F1) ──────────────────────────────
        // POST /api/manage/scale-free/{resourceGroup}/{planName}
        app.MapPost("/api/manage/scale-free/{resourceGroup}/{planName}", async (
            string resourceGroup,
            string planName,
            IConfiguration config,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            try
            {
                var client = GetArmClient(config);
                var subscriptionId = config["Azure:SubscriptionId"];
                if (string.IsNullOrWhiteSpace(subscriptionId))
                    return Results.Problem("Azure:SubscriptionId is not configured.", statusCode: 503);

                var planId   = AppServicePlanResource.CreateResourceIdentifier(subscriptionId, resourceGroup, planName);
                var planRes  = client.GetAppServicePlanResource(planId);
                var existing = (await planRes.GetAsync(ct)).Value;

                // Build updated data with F1 SKU — CreateOrUpdate from parent resource group
                var data = existing.Data;
                data.Sku = new AppServiceSkuDescription { Name = "F1", Tier = "Free" };
                var rgResourceId = new global::Azure.Core.ResourceIdentifier(
                    $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}");
                var rg = client.GetResourceGroupResource(rgResourceId);
                await rg.GetAppServicePlans().CreateOrUpdateAsync(WaitUntil.Completed, planName, data, ct);

                logger.LogInformation("App Service Plan {PlanName} in {ResourceGroup} scaled to Free (F1)", planName, resourceGroup);
                return Results.Ok(new { success = true, message = $"{planName} scaled to Free tier (F1)." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to scale App Service Plan {PlanName} to Free", planName);
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        });

        return app;
    }

    private static ArmClient GetArmClient(IConfiguration _) =>
        new ArmClient(new DefaultAzureCredential());
}
