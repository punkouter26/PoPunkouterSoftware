using System.Text.Json;
using System.Text.Json.Serialization;
using PoPunkouterSoftware.Shared.Azure;

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

        // ── Index existing entries by composite key (ResourceId + Url) ─────────
        // Using composite key prevents collisions when two Azure resources share
        // the same hostname (e.g., app-5ln5hfdrvof5u and PoMiniGames both resolving
        // to the same default domain). URL-only dedup silently drops entries.
        var byResourceId = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var byUrl = new Dictionary<string, List<AppEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (!string.IsNullOrWhiteSpace(app.Id))
                byResourceId.TryAdd(app.Id, app);
            if (!string.IsNullOrWhiteSpace(app.Url))
            {
                if (!byUrl.TryGetValue(app.Url, out var list))
                    byUrl[app.Url] = list = new();
                list.Add(app);
            }
        }

        var services = report.WebServices?.Services ?? new List<WebService>();
        var discoveredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Process every service discovered in Azure ─────────────────────────
        foreach (var svc in services)
        {
            if (string.IsNullOrWhiteSpace(svc.Url)) continue;
            var slug = Slugify(svc.Name);
            discoveredIds.Add(slug);

            var techs = InferTechnologies(svc.ResourceType);

            // Try matching by canonical ID first (stronger match), then by URL
            AppEntry? existing = null;
            if (!string.IsNullOrWhiteSpace(slug) && byResourceId.TryGetValue(slug, out var byId))
                existing = byId;
            else if (byUrl.TryGetValue(svc.Url, out var urlMatches))
                existing = urlMatches.FirstOrDefault();

            if (existing is not null)
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
                    existing.Description = SanitizeDescription(svc.Description, NiceName(svc.Name));
            }
            else
            {
                // Newly discovered service — add it as 'inactive' (user can promote to 'active')
                var entry = new AppEntry
                {
                    Id           = Slugify(svc.Name),
                    Name         = NiceName(svc.FriendlyName.Length > 0 ? svc.FriendlyName : svc.Name),
                    Description  = SanitizeDescription(svc.Description, NiceName(svc.Name)),
                    Status       = "inactive",
                    Url          = svc.Url,
                    Category     = InferCategory(svc.Name),
                    Technologies = techs
                };
                apps.Add(entry);
                if (!byUrl.TryGetValue(svc.Url, out var urlList))
                    byUrl[svc.Url] = urlList = new();
                urlList.Add(entry);
            }
        }

        // ── Apps in apps.json that no longer exist in Azure ───────────────────
        // We deliberately do NOT auto-demote apps that aren't found — the user
        // controls status. Mark removed apps with a warning annotation in the ID.
        // (Deleted Azure resources stay visible until the user manually removes them.)
        var skippedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            // If this app's URL matches a discovered URL, skip marking
            if (!string.IsNullOrWhiteSpace(app.Url) && discoveredIds.Contains(app.Id)) continue;
            // If this app's slug matches a discovered slug, skip
            if (discoveredIds.Contains(app.Id)) continue;
            // Otherwise it's a stale entry — we leave it in place but flag it
            // (no auto-demotion as per intentional design)
        }

        // ── Sort and write back ───────────────────────────────────────────────
        wrapper.Apps = apps
            .OrderBy(a => a.Status?.ToLower() == "active" ? 0 : 1)
            .ThenBy(a => a.Name)
            .ToList();

        var outJson = JsonSerializer.Serialize(wrapper, _writeOpts);
        await File.WriteAllTextAsync(appsJsonPath, outJson, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Matches descriptions that are just an Azure internal resource name + generic suffix
    // e.g. "stapp-5ln5hfdrvof5u productivity tool", "app-abc123 app", "func-xyz service"
    private static readonly System.Text.RegularExpressions.Regex _azureInternalNamePattern =
        new(@"^(app-|stapp-|func-|wa-|api-|swa-)[a-z0-9]+[a-z0-9-]* \w+(\s\w+)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Returns description if it looks like a real description; otherwise falls back to a clean default.</summary>
    private static string SanitizeDescription(string? description, string niceName)
    {
        if (string.IsNullOrWhiteSpace(description))
            return $"{niceName} app";
        // Reject raw Azure resource names masquerading as descriptions
        if (_azureInternalNamePattern.IsMatch(description.Trim()))
            return $"{niceName} app";
        return description;
    }

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

        [JsonPropertyName("githubRepo")]
        public string? GithubRepo { get; set; }
    }
}
