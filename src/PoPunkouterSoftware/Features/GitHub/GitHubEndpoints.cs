using Microsoft.Extensions.Caching.Hybrid;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared.Validation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoPunkouterSoftware.Features.GitHub;

/// <summary>
/// Exposes a lightweight GitHub activity proxy that caches results to avoid hitting rate limits.
/// Returns last commit date and 8-week sparkline commit counts for a given public repo.
/// </summary>
internal static class GitHubEndpoints
{
    private static readonly RepoQueryValidator _repoValidator = new();

    // Successful lookups are stable for hours. Rate-limited results are overwritten
    // post-hoc with a short cooldown so the cache is never poisoned by an empty result —
    // a rate-limited response cached for hours would block ALL users for that period.
    private static readonly HybridCacheEntryOptions SuccessTtl = new()
    {
        Expiration = TimeSpan.FromHours(6),
        LocalCacheExpiration = TimeSpan.FromHours(6),
    };

    private static readonly HybridCacheEntryOptions RateLimitCooldownTtl = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    /// <summary>Wire contract for /api/github-activity — property names are the public API.</summary>
    internal sealed record GitHubActivityResult(
        [property: JsonPropertyName("lastCommitDate")] DateTime? LastCommitDate,
        [property: JsonPropertyName("weeklyCommits")] int[] WeeklyCommits,
        [property: JsonPropertyName("rateLimited")] bool RateLimited,
        [property: JsonPropertyName("healthScore")] int? HealthScore);

    internal static WebApplication MapGitHubEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("GitHub");

        group.MapGet("/github-activity", async (
            string? repo,
            IHttpClientFactory httpClientFactory,
            HybridCache cache,
            ILogger<Program> logger,
            CancellationToken ct) => await InvokeAsync(repo, httpClientFactory, cache, logger, ct))
        .WithName("GetGitHubActivity");

        return app;
    }

    /// <summary>Testable entry-point — extracted from the route handler closure.</summary>
    internal static async Task<IResult> InvokeAsync(
        string? repo,
        IHttpClientFactory httpClientFactory,
        HybridCache cache,
        ILogger<Program> logger,
        CancellationToken ct = default)
    {
        var validation = _repoValidator.Validate(repo ?? string.Empty);
        if (!validation.IsValid)
            return Results.BadRequest(new { error = validation.Errors[0].ErrorMessage });

        // Strongly-typed identifier: the raw query string never reaches a URL — every
        // interpolation below goes through the parsed owner/name pair.
        if (!PoPunkouterSoftware.Shared.Azure.RepoId.TryParse(repo, out var repoId))
            return Results.BadRequest(new { error = "Invalid repo parameter. Expected format: owner/repo" });

        var cacheKey = $"github-activity:{repoId}";
        try
        {
            // HybridCache.GetOrCreateAsync adds stampede protection: concurrent misses for
            // the same repo share a single upstream fetch instead of each hitting GitHub.
            var result = await cache.GetOrCreateAsync(
                cacheKey,
                (httpClientFactory, repoId),
                static async (state, token) => await FetchActivityAsync(state.httpClientFactory, state.repoId, token),
                SuccessTtl,
                cancellationToken: ct);

            // Outcome-dependent TTL: a rate-limited result must not occupy the cache for
            // hours — overwrite it with a 30-second cooldown that still prevents a
            // thundering herd against the exhausted API budget.
            if (result.RateLimited)
                await cache.SetAsync(cacheKey, result, RateLimitCooldownTtl, cancellationToken: ct);

            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GitHub activity fetch failed for {Repo}", repo);
            return Results.Ok(new GitHubActivityResult(null, Array.Empty<int>(), false, 0));
        }
    }

    private static async Task<GitHubActivityResult> FetchActivityAsync(
        IHttpClientFactory httpClientFactory,
        PoPunkouterSoftware.Shared.Azure.RepoId repoId,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("github");

        // ── Last commit ──────────────────────────────────────────────
        DateTime? lastCommitDate = null;
        var commitsResp = await GetTrackedAsync(client,
            $"https://api.github.com/repos/{repoId}/commits?per_page=1", ct);

        if (commitsResp.IsSuccessStatusCode)
        {
            var json = await commitsResp.Content.ReadAsStringAsync(ct);
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
            return new GitHubActivityResult(null, Array.Empty<int>(), true, null);
        }

        // ── 8-week sparkline (participation stats) ───────────────────
        int[] weeklyCommits = Array.Empty<int>();
        var statsResp = await GetTrackedAsync(client,
            $"https://api.github.com/repos/{repoId}/stats/participation", ct);

        if (statsResp.IsSuccessStatusCode)
        {
            var statsJson = await statsResp.Content.ReadAsStringAsync(ct);
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
        else if (statsResp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
              || statsResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return new GitHubActivityResult(lastCommitDate, Array.Empty<int>(), true, null);
        }

        // ── Repo metadata for health score ───────────────────────────
        bool hasReadme = false, hasDescription = false, hasLicense = false;
        int openIssues = 0;
        try
        {
            var repoResp = await GetTrackedAsync(client, $"https://api.github.com/repos/{repoId}", ct);
            if (repoResp.IsSuccessStatusCode)
            {
                var repoJson = await repoResp.Content.ReadAsStringAsync(ct);
                using var rd = JsonDocument.Parse(repoJson);
                hasDescription = !string.IsNullOrWhiteSpace(rd.RootElement.GetProperty("description").GetString());
                hasLicense = rd.RootElement.GetProperty("license").ValueKind != JsonValueKind.Null;
                openIssues = rd.RootElement.GetProperty("open_issues_count").GetInt32();
            }

            var readmeResp = await GetTrackedAsync(client, $"https://api.github.com/repos/{repoId}/readme", ct);
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

        return new GitHubActivityResult(lastCommitDate, weeklyCommits, false, score);
    }

    /// <summary>
    /// GET wrapper that records a GitHub call metric (by status_class) and the remaining
    /// rate-limit budget from the X-RateLimit-Remaining header. (question 9)
    /// </summary>
    private static async Task<HttpResponseMessage> GetTrackedAsync(HttpClient client, string url, CancellationToken ct)
    {
        var resp = await client.GetAsync(url, ct);
        Telemetry.GitHubCalls.Add(1,
            new KeyValuePair<string, object?>("status_class", Telemetry.StatusClass((int)resp.StatusCode)));
        if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && long.TryParse(values.FirstOrDefault(), out var remaining))
            Telemetry.GitHubRateLimitRemaining.Record(remaining);
        return resp;
    }
}
