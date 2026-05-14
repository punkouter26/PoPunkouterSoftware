using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoPunkouterSoftware.Features.GitHub;

/// <summary>
/// Exposes a lightweight GitHub activity proxy that caches results to avoid hitting rate limits.
/// Returns last commit date and 8-week sparkline commit counts for a given public repo.
/// </summary>
internal static class GitHubEndpoints
{
    private static readonly Regex _repoPattern = new(@"^[a-zA-Z0-9_.\-]+/[a-zA-Z0-9_.\-]+$", RegexOptions.Compiled);

    internal static WebApplication MapGitHubEndpoints(this WebApplication app)
    {
        app.MapGet("/api/github-activity", async (
            string? repo,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            ILogger<Program> logger) => await InvokeAsync(repo, httpClientFactory, cache, logger))
        .WithName("GetGitHubActivity")
        .WithTags("GitHub");

        return app;
    }

    /// <summary>Testable entry-point — extracted from the route handler closure.</summary>
    internal static async Task<IResult> InvokeAsync(
        string? repo,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(repo) || !_repoPattern.IsMatch(repo))
            return Results.BadRequest(new { error = "Invalid repo parameter. Expected format: owner/repo" });

        var cacheKey = $"github-activity:{repo}";
        if (cache.TryGetValue(cacheKey, out object? cached))
            return Results.Ok(cached);

        try
        {
            var client = httpClientFactory.CreateClient("github");

            // ── Last commit ──────────────────────────────────────────────
            DateTime? lastCommitDate = null;
            var commitsResp = await client.GetAsync(
                $"https://api.github.com/repos/{repo}/commits?per_page=1");

            if (commitsResp.IsSuccessStatusCode)
            {
                var json = await commitsResp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array
                    && doc.RootElement.GetArrayLength() > 0)
                {
                    var dateStr = doc.RootElement[0]
                        .GetProperty("commit")
                        .GetProperty("author")
                        .GetProperty("date")
                        .GetString();
                    if (DateTime.TryParse(dateStr, out var dt))
                        lastCommitDate = dt.ToUniversalTime();
                }
            }
            else if (commitsResp.StatusCode == System.Net.HttpStatusCode.Forbidden
                  || commitsResp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // Rate limited — return empty result with NO cache (only a 30-second
                // cooldown to avoid a thundering herd) so the cache is never poisoned.
                // Previously this cached the empty result for 15 minutes, which meant
                // one rate-limited request would block ALL users for that period.
                var limited = new { lastCommitDate = (DateTime?)null, weeklyCommits = Array.Empty<int>(), rateLimited = true };
                cache.Set(cacheKey, limited, TimeSpan.FromSeconds(30));
                return Results.Ok(limited);
            }

            // ── 8-week sparkline (participation stats) ───────────────────
            int[] weeklyCommits = Array.Empty<int>();
            var statsResp = await client.GetAsync(
                $"https://api.github.com/repos/{repo}/stats/participation");

            if (statsResp.IsSuccessStatusCode)
            {
                var statsJson = await statsResp.Content.ReadAsStringAsync();
                // GitHub may return 202 (computing) on first call — fall back gracefully
                if (statsJson.Trim().StartsWith("{"))
                {
                    using var doc = JsonDocument.Parse(statsJson);
                    if (doc.RootElement.TryGetProperty("all", out var allEl))
                    {
                        var all = allEl.EnumerateArray()
                            .Select(e => e.GetInt32())
                            .ToArray();
                        // Take last 8 weeks of the 52-week array
                        weeklyCommits = all.Length >= 8 ? all[^8..] : all;
                    }
                }
            }
            // Also handle 429 on stats endpoint — don't cache empty results
            else if (statsResp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                  || statsResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return Results.Ok(new { lastCommitDate, weeklyCommits = Array.Empty<int>(), rateLimited = true });
            }

            // ── Repo metadata for health score ───────────────────────────
            bool hasReadme = false, hasDescription = false, hasLicense = false;
            int openIssues = 0;
            try
            {
                var repoResp = await client.GetAsync($"https://api.github.com/repos/{repo}");
                if (repoResp.IsSuccessStatusCode)
                {
                    var repoJson = await repoResp.Content.ReadAsStringAsync();
                    using var rd = JsonDocument.Parse(repoJson);
                    hasDescription = !string.IsNullOrWhiteSpace(rd.RootElement.GetProperty("description").GetString());
                    hasLicense = rd.RootElement.GetProperty("license").ValueKind != JsonValueKind.Null;
                    openIssues = rd.RootElement.GetProperty("open_issues_count").GetInt32();
                }

                var readmeResp = await client.GetAsync($"https://api.github.com/repos/{repo}/readme");
                hasReadme = readmeResp.IsSuccessStatusCode;
            }
            catch { /* non-fatal */ }

            // ── Compute health score (0–100) ─────────────────────────────
            // • Recent commit (≤90d): +40   • Active commits sparkline: +20
            // • Has README: +15             • Has description: +10
            // • Has license: +10            • Deduct 5 per open issue (max -15)
            var score = 0;
            if (lastCommitDate.HasValue && (DateTime.UtcNow - lastCommitDate.Value).TotalDays <= 90)
                score += 40;
            if (weeklyCommits.Sum() > 0)
                score += 20;
            if (hasReadme)
                score += 15;
            if (hasDescription)
                score += 10;
            if (hasLicense)
                score += 10;
            score -= Math.Min(15, openIssues * 5);
            score = Math.Max(0, score);

            var result = new { lastCommitDate, weeklyCommits, rateLimited = false, healthScore = score };
            cache.Set(cacheKey, result, TimeSpan.FromHours(6));
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GitHub activity fetch failed for {Repo}", repo);
            return Results.Ok(new { lastCommitDate = (DateTime?)null, weeklyCommits = Array.Empty<int>(), healthScore = 0 });
        }
    }
}
