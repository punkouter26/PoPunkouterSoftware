using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoPunkouterSoftware.Features.Azure;

/// <summary>
/// After each Azure refresh, merges live connectivity + resource-type data back into
/// apps.json so the home page status is always accurate without manual edits.
/// </summary>
public static class AppsJsonSyncer
{
    private static readonly JsonSerializerOptions _readOpts  = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions _writeOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task SyncAsync(AzureReport report, string appsJsonPath, CancellationToken ct = default)
    {
        // ── Load existing apps.json ───────────────────────────────────────────
        AppsWrapper wrapper;
        if (File.Exists(appsJsonPath))
        {
            var raw = await File.ReadAllTextAsync(appsJsonPath, ct);
            wrapper  = JsonSerializer.Deserialize<AppsWrapper>(raw, _readOpts) ?? new AppsWrapper();
        }
        else
        {
            wrapper = new AppsWrapper();
        }

        var apps = wrapper.Apps ??= new List<AppEntry>();

        // ── Index existing entries by URL ─────────────────────────────────────
        var byUrl = apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Url))
            .GroupBy(a => a.Url!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var services = report.WebServices?.Services ?? new List<WebService>();
        var discoveredUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Process every service discovered in Azure ─────────────────────────
        foreach (var svc in services)
        {
            if (string.IsNullOrWhiteSpace(svc.Url)) continue;
            discoveredUrls.Add(svc.Url);

            var techs = InferTechnologies(svc.ResourceType);

            if (byUrl.TryGetValue(svc.Url, out var existing))
            {
                // NOTE: Do NOT touch existing.Status — that is user-controlled intent.
                // Active/disabled is the user's decision; the live HTTP check result
                // is shown as a separate overlay badge via GetLiveStatus() in the UI.

                // Update technologies if they are still generic/empty
                if (existing.Technologies == null || existing.Technologies.Count == 0 ||
                    IsGenericTech(existing.Technologies))
                {
                    existing.Technologies = techs;
                }

                // Back-fill description from Azure if blank
                if (string.IsNullOrWhiteSpace(existing.Description) && !string.IsNullOrWhiteSpace(svc.Description))
                    existing.Description = svc.Description;
            }
            else
            {
                // Newly discovered service — add it as 'inactive' (user can promote to 'active')
                var entry = new AppEntry
                {
                    Id           = Slugify(svc.Name),
                    Name         = NiceName(svc.FriendlyName.Length > 0 ? svc.FriendlyName : svc.Name),
                    Description  = !string.IsNullOrWhiteSpace(svc.Description) ? svc.Description : $"{NiceName(svc.Name)} app",
                    Status       = "inactive",
                    Url          = svc.Url,
                    Category     = InferCategory(svc.Name),
                    Technologies = techs
                };
                apps.Add(entry);
                byUrl[svc.Url] = entry;
            }
        }

        // ── Apps in apps.json that no longer exist in Azure ───────────────────
        // We deliberately do NOT auto-demote apps that aren't found — the user
        // controls status. The live HTTP badge in the UI shows real-time state.
        // (Deleted Azure resources stay visible until the user manually removes them.)

        // ── Sort and write back ───────────────────────────────────────────────
        wrapper.Apps = apps
            .OrderBy(a => a.Status?.ToLower() == "active" ? 0 : 1)
            .ThenBy(a => a.Name)
            .ToList();

        var outJson = JsonSerializer.Serialize(wrapper, _writeOpts);
        await File.WriteAllTextAsync(appsJsonPath, outJson, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns true if the technology list is a generic placeholder that can be replaced.</summary>
    private static bool IsGenericTech(List<string> techs)
    {
        if (techs.Count == 1 && techs[0] == "Azure") return true;
        return false;
    }

    private static List<string> InferTechnologies(string? resourceType)
    {
        return (resourceType?.ToLowerInvariant()) switch
        {
            "microsoft.web/sites"        => new List<string> { "Azure App Service", "C#", "Blazor" },
            "microsoft.web/staticsites"  => new List<string> { "Azure Static Web Apps", "JavaScript" },
            "microsoft.app/containerapps"=> new List<string> { "Azure Container Apps", "Docker" },
            _                            => new List<string> { "Azure" }
        };
    }

    private static string InferCategory(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("game") || n.Contains("quiz") || n.Contains("race") || n.Contains("tag") || n.Contains("joker") || n.Contains("fun"))
            return "games";
        if (n.Contains("ai") || n.Contains("robot") || n.Contains("trump"))
            return "ai";
        if (n.Contains("runner") || n.Contains("weight") || n.Contains("face") || n.Contains("married"))
            return "health";
        return "productivity";
    }

    /// <summary>Turns "my-app-name-abc123" into "MyAppName Abc123" style clean display name.</summary>
    private static string NiceName(string raw)
    {
        // Strip leading "app-", "po" prefixes then title-case
        var s = raw.Trim();
        if (s.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        // Split kebab/camel, title-case each word
        var parts = System.Text.RegularExpressions.Regex.Split(s, @"[-_\s]+");
        return string.Concat(parts.Select(p => p.Length > 0 ? char.ToUpper(p[0]) + p[1..] : ""));
    }

    private static string Slugify(string name)
    {
        var s = name.ToLowerInvariant();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9]+", "-");
        return s.Trim('-');
    }

    // ── Local model types (only used for apps.json serialisation) ─────────────

    private class AppsWrapper
    {
        [JsonPropertyName("apps")]
        public List<AppEntry> Apps { get; set; } = new();
    }

    private class AppEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("technologies")]
        public List<string>? Technologies { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
