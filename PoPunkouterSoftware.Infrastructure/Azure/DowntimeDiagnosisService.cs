using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared.Azure;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PoPunkouterSoftware.Infrastructure.Azure;

/// <summary>
/// Diagnoses the root cause of downtime for broken or unreachable App Services
/// by inspecting ARM state, App Service Plan status, recent deployments, and
/// the Azure Activity Log. Extracted from <see cref="AzureReportService"/> to
/// keep that class focused on orchestration.
/// </summary>
public class DowntimeDiagnosisService
{
    private readonly ILogger<DowntimeDiagnosisService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public DowntimeDiagnosisService(
        ILogger<DowntimeDiagnosisService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    internal async Task<List<ServiceDowntimeDiagnosis>> DiagnoseAsync(
        List<RawService> brokenServices, string subscriptionId, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return [];
        var client = _httpClientFactory.CreateClient();
        using var gate = new SemaphoreSlim(4);

        var tasks = brokenServices.Select(async svc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(25));
                var tok = cts.Token;

                string? availState = null, usageState = null, serverFarmId = null;
                bool isSuspended = false;
                DateTime? suspendedTill = null;
                string? planName = null, planStatus = null, planSku = null;
                bool planStopped = false;

                // 1 — Site ARM details
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://management.azure.com{svc.ResourceId}?api-version=2023-12-01");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var resp = await client.SendAsync(req, tok);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(tok);
                        var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("properties", out var props))
                        {
                            availState = props.TryGetProperty("availabilityState", out var av) ? av.GetString() : null;
                            usageState = props.TryGetProperty("usageState", out var us) ? us.GetString() : null;
                            serverFarmId = props.TryGetProperty("serverFarmId", out var sfId) ? sfId.GetString() : null;
                            if (props.TryGetProperty("suspendedTill", out var susp) && susp.ValueKind == JsonValueKind.String)
                            {
                                if (DateTime.TryParse(susp.GetString(), out var suspDate))
                                {
                                    suspendedTill = suspDate;
                                    isSuspended = suspDate > DateTime.UtcNow;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Site ARM detail failed for {Name}", svc.Name); }

                // 2 — App Service Plan state
                if (serverFarmId is not null)
                {
                    try
                    {
                        planName = serverFarmId.Split('/').LastOrDefault();
                        using var req = new HttpRequestMessage(HttpMethod.Get,
                            $"https://management.azure.com{serverFarmId}?api-version=2023-12-01");
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                        using var resp = await client.SendAsync(req, tok);
                        if (resp.IsSuccessStatusCode)
                        {
                            var json = await resp.Content.ReadAsStringAsync(tok);
                            var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("properties", out var props))
                                planStatus = props.TryGetProperty("status", out var st) ? st.GetString() : null;
                            if (doc.RootElement.TryGetProperty("sku", out var sku))
                                planSku = sku.TryGetProperty("name", out var skuName) ? skuName.GetString() : null;
                            planStopped = planStatus is "Stopped";
                        }
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Plan ARM detail failed for {Name}", svc.Name); }
                }

                // 3 — Recent deployments (last 5)
                var deployments = new List<DeploymentEntry>();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://management.azure.com{svc.ResourceId}/deployments?api-version=2023-12-01");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var resp = await client.SendAsync(req, tok);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(tok);
                        var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("value", out var arr))
                        {
                            foreach (var item in arr.EnumerateArray().Take(5))
                            {
                                if (!item.TryGetProperty("properties", out var p))
                                    continue;
                                int? statusCode = p.TryGetProperty("status", out var sc) && sc.ValueKind == JsonValueKind.Number
                                    ? sc.GetInt32() : null;
                                DateTime? depAt = p.TryGetProperty("end_time", out var et) && et.ValueKind == JsonValueKind.String
                                    && DateTime.TryParse(et.GetString(), out var dt) ? dt : null;
                                var statusText = statusCode switch
                                {
                                    4 => "Success",
                                    3 => "Failed",
                                    2 => "Deploying",
                                    1 => "Building",
                                    0 => "Pending",
                                    _ => statusCode?.ToString()
                                };
                                deployments.Add(new DeploymentEntry
                                {
                                    DeploymentId = item.TryGetProperty("name", out var nm) ? nm.GetString() : null,
                                    Active = p.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.True,
                                    StatusCode = statusCode,
                                    StatusText = statusText,
                                    Message = p.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                                        ? msg.GetString() : null,
                                    DeployedAt = depAt,
                                    Author = p.TryGetProperty("author_email", out var auth) && auth.ValueKind == JsonValueKind.String
                                        ? auth.GetString() : null,
                                });
                            }
                        }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Deployments fetch failed for {Name}", svc.Name); }

                // 4 — Activity log (last 48 hours)
                var activityLog = new List<ActivityLogEntry>();
                try
                {
                    var since = DateTime.UtcNow.AddHours(-48).ToString("yyyy-MM-ddTHH:mm:ssZ");
                    var filter = Uri.EscapeDataString(
                        $"eventTimestamp ge '{since}' and resourceId eq '{svc.ResourceId}'");
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Insights/eventtypes/management/values?api-version=2015-04-01&$filter={filter}&$top=15");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var resp = await client.SendAsync(req, tok);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(tok);
                        var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("value", out var arr))
                        {
                            foreach (var ev in arr.EnumerateArray().Take(15))
                            {
                                var opName = ev.TryGetProperty("operationName", out var op)
                                    && op.TryGetProperty("localizedValue", out var lv) ? lv.GetString() : null;
                                var status = ev.TryGetProperty("status", out var st)
                                    && st.TryGetProperty("localizedValue", out var stv) ? stv.GetString() : null;
                                DateTime? ts = ev.TryGetProperty("eventTimestamp", out var tsProp)
                                    && tsProp.ValueKind == JsonValueKind.String
                                    && DateTime.TryParse(tsProp.GetString(), out var dt) ? dt : null;
                                var caller = ev.TryGetProperty("caller", out var cal) && cal.ValueKind == JsonValueKind.String
                                    ? cal.GetString() : null;
                                var level = ev.TryGetProperty("level", out var lvl) && lvl.ValueKind == JsonValueKind.String
                                    ? lvl.GetString() : null;
                                activityLog.Add(new ActivityLogEntry
                                {
                                    OperationName = opName,
                                    Status = status,
                                    EventTimestamp = ts,
                                    Caller = caller,
                                    Level = level,
                                });
                            }
                        }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Activity log fetch failed for {Name}", svc.Name); }

                // Determine most likely cause + suggested fix
                string likelyCause;
                string? suggestedFix;

                if (usageState == "Exceeded" || isSuspended)
                {
                    likelyCause = "Free-tier CPU/memory quota exceeded — app is suspended until the next quota cycle";
                    suggestedFix = suspendedTill.HasValue
                        ? $"App will auto-resume around {suspendedTill.Value.ToUniversalTime():yyyy-MM-dd HH:mm} UTC. Upgrade to B1 to prevent recurrence."
                        : "Upgrade to B1 plan or wait for the daily quota reset (midnight UTC).";
                }
                else if (planStopped)
                {
                    likelyCause = $"App Service Plan '{planName ?? "unknown"}' is stopped — all apps on this plan are down";
                    suggestedFix = $"az appservice plan update --name \"{planName}\" --resource-group \"{svc.ResourceGroup}\" --status Running";
                }
                else if (svc.PlatformState == "Stopped")
                {
                    likelyCause = "App Service is stopped";
                    suggestedFix = $"az webapp start --name \"{svc.Name}\" --resource-group \"{svc.ResourceGroup}\"";
                }
                else if (deployments.Any(d => d.StatusCode == 3))
                {
                    var failed = deployments.First(d => d.StatusCode == 3);
                    likelyCause = "Last deployment failed — app may be running a broken build or unable to start";
                    var msg = failed.Message;
                    suggestedFix = msg is not null && msg.Length > 0
                        ? (msg.Length > 250 ? msg[..250] + "…" : msg)
                        : "Check Kudu deployment logs for the error details.";
                }
                else if (availState is not null && availState != "Normal")
                {
                    likelyCause = $"Azure platform reports availability state: {availState}";
                    suggestedFix = "Check Azure Service Health for ongoing incidents in your region.";
                }
                else if (activityLog.Any(a => a.OperationName?.Contains("Stop", StringComparison.OrdinalIgnoreCase) == true
                    && a.Status?.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) == true))
                {
                    var stopEvent = activityLog.First(a => a.OperationName!.Contains("Stop", StringComparison.OrdinalIgnoreCase));
                    likelyCause = $"App was recently stopped via management API"
                        + (stopEvent.Caller is not null ? $" by {stopEvent.Caller}" : "")
                        + (stopEvent.EventTimestamp.HasValue ? $" at {stopEvent.EventTimestamp.Value.ToUniversalTime():yyyy-MM-dd HH:mm} UTC" : "");
                    suggestedFix = $"az webapp start --name \"{svc.Name}\" --resource-group \"{svc.ResourceGroup}\"";
                }
                else if (svc.HttpStatus == "unreachable")
                {
                    likelyCause = "App is unreachable — TCP connection failed or DNS not resolving";
                    suggestedFix = "Verify the App Service hostname in the portal and check if the app is Running. Test with: curl -I " + (svc.Url ?? "https://<app>.azurewebsites.net");
                }
                else
                {
                    likelyCause = "No obvious infrastructure cause found — likely an application-level error (crash loop, bad startup config, or missing secrets)";
                    suggestedFix = $"Check Application Insights for exceptions or stream live logs at: https://{svc.Name}.scm.azurewebsites.net/api/logstream";
                }

                return new ServiceDowntimeDiagnosis
                {
                    Name = svc.Name,
                    FriendlyName = svc.FriendlyName,
                    ResourceGroup = svc.ResourceGroup,
                    HttpStatus = svc.HttpStatus,
                    AvailabilityState = availState,
                    UsageState = usageState,
                    IsSuspended = isSuspended,
                    SuspendedTill = suspendedTill,
                    PlanName = planName,
                    PlanStatus = planStatus,
                    PlanSku = planSku,
                    PlanStopped = planStopped,
                    RecentDeployments = deployments,
                    RecentActivity = activityLog,
                    LikelyCause = likelyCause,
                    SuggestedFix = suggestedFix,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Downtime diagnosis failed for {Name}", svc.Name);
                return new ServiceDowntimeDiagnosis
                {
                    Name = svc.Name,
                    FriendlyName = svc.FriendlyName,
                    ResourceGroup = svc.ResourceGroup,
                    HttpStatus = svc.HttpStatus,
                    LikelyCause = "Diagnosis step failed — see application logs",
                    SuggestedFix = ex.Message,
                };
            }
            finally
            {
                gate.Release();
            }
        });

        return (await Task.WhenAll(tasks)).ToList();
    }
}
