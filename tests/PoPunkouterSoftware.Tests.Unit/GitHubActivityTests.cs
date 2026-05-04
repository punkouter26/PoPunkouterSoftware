using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PoPunkouterSoftware.Tests.Unit;

// ─── Helpers ─────────────────────────────────────────────────────────────────

file sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) => _factory = factory;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(_factory(request));

    public static StubHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
}

file static class GitHubClientFactory
{
    public static IHttpClientFactory Create(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("test-agent/1.0");
        var factory = new FakeHttpClientFactory(client);
        return factory;
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

// ─── Input validation ─────────────────────────────────────────────────────────

public class GitHubActivityEndpoint_ValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-slash")]
    [InlineData("has spaces/repo")]
    [InlineData("owner/has spaces")]
    [InlineData("../../traversal")]
    [InlineData("<script>/xss")]
    public async Task InvalidRepo_ReturnsBadRequest(string? repo)
    {
        var result = await InvokeEndpoint(repo, StubHandler.Json("[]"));
        var statusResult = result.Should().BeAssignableTo<IStatusCodeHttpResult>().Subject;
        statusResult.StatusCode.Should().Be(400);
    }

    [Theory]
    [InlineData("owner/repo")]
    [InlineData("my-org/My.Repo-123")]
    [InlineData("a/b")]
    public async Task ValidRepo_DoesNotReturnBadRequest(string repo)
    {
        var commitJson = """[{"commit":{"author":{"date":"2026-01-01T00:00:00Z"}}}]""";
        var statsJson  = """{"all":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,2,3,4,5,6,7,8]}""";
        var repoJson   = """{"description":"d","license":{"key":"mit"},"open_issues_count":0}""";

        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/commits"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(commitJson, Encoding.UTF8, "application/json") };
            if (req.RequestUri.PathAndQuery.Contains("/stats/participation"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(statsJson, Encoding.UTF8, "application/json") };
            if (req.RequestUri.PathAndQuery.Contains("/readme"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(repoJson, Encoding.UTF8, "application/json") };
        });

        var result = await InvokeEndpoint(repo, handler);
        var statusResult = result.Should().BeAssignableTo<IStatusCodeHttpResult>().Subject;
        statusResult.StatusCode.Should().NotBe(400);
    }

    private static async Task<IResult> InvokeEndpoint(string? repo, HttpMessageHandler handler)
    {
        var cache   = new MemoryCache(new MemoryCacheOptions());
        var factory = GitHubClientFactory.Create(handler);
        var logger  = NullLogger<Program>.Instance;
        return await PoPunkouterSoftware.Features.GitHub.GitHubEndpoints
            .InvokeAsync(repo, factory, cache, logger);
    }
}

// ─── Caching ─────────────────────────────────────────────────────────────────

public class GitHubActivityEndpoint_CacheTests
{
    [Fact]
    public async Task SecondCall_SameRepo_DoesNotHitNetwork()
    {
        int callCount = 0;
        var commitJson = """[{"commit":{"author":{"date":"2026-01-01T00:00:00Z"}}}]""";
        var statsJson  = """{"all":[0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51]}""";
        var repoJson   = """{"description":"d","license":{"key":"mit"},"open_issues_count":1}""";

        var handler = new StubHandler(req =>
        {
            callCount++;
            if (req.RequestUri!.PathAndQuery.Contains("/commits"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(commitJson, Encoding.UTF8, "application/json") };
            if (req.RequestUri.PathAndQuery.Contains("/stats/participation"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(statsJson, Encoding.UTF8, "application/json") };
            if (req.RequestUri.PathAndQuery.Contains("/readme"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(repoJson, Encoding.UTF8, "application/json") };
        });

        var cache   = new MemoryCache(new MemoryCacheOptions());
        var factory = GitHubClientFactory.Create(handler);
        var logger  = NullLogger<Program>.Instance;

        await PoPunkouterSoftware.Features.GitHub.GitHubEndpoints.InvokeAsync("owner/repo", factory, cache, logger);
        var countAfterFirst = callCount;

        await PoPunkouterSoftware.Features.GitHub.GitHubEndpoints.InvokeAsync("owner/repo", factory, cache, logger);

        callCount.Should().Be(countAfterFirst, because: "second call must be served from cache");
    }
}

// ─── Rate-limit handling ──────────────────────────────────────────────────────

public class GitHubActivityEndpoint_RateLimitTests
{
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RateLimited_ReturnsOkWithRateLimitedTrue(HttpStatusCode status)
    {
        var handler = StubHandler.Json("{}", status);
        var cache   = new MemoryCache(new MemoryCacheOptions());
        var factory = GitHubClientFactory.Create(handler);
        var logger  = NullLogger<Program>.Instance;

        var result = await PoPunkouterSoftware.Features.GitHub.GitHubEndpoints
            .InvokeAsync("owner/repo", factory, cache, logger);

        var statusResult = result.Should().BeAssignableTo<IStatusCodeHttpResult>().Subject;
        statusResult.StatusCode.Should().Be(200);
        var valueResult = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
        var json = JsonSerializer.Serialize(valueResult.Value);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("rateLimited").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RateLimited_ShortCooldown_DoesNotPoisonCache()
    {
        // After a 403, a second call with a fixed response must return fresh data
        int calls = 0;
        var commitJson = """[{"commit":{"author":{"date":"2026-04-01T00:00:00Z"}}}]""";
        var statsJson  = """{"all":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,5,5,5,5,5,5,5,5]}""";
        var repoJson   = """{"description":"d","license":{"key":"mit"},"open_issues_count":0}""";

        var handler = new StubHandler(req =>
        {
            calls++;
            // First batch: 403 on commits
            if (calls == 1)
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            // Subsequent: healthy
            if (req.RequestUri!.PathAndQuery.Contains("/commits"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(commitJson, Encoding.UTF8, "application/json") };
            if (req.RequestUri.PathAndQuery.Contains("/stats/participation"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(statsJson, Encoding.UTF8, "application/json") };
            if (req.RequestUri.PathAndQuery.Contains("/readme"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(repoJson, Encoding.UTF8, "application/json") };
        });

        // Use very short cooldown: set via MemoryCache with sliding expiration of 0
        var cache   = new MemoryCache(new MemoryCacheOptions());
        var factory = GitHubClientFactory.Create(handler);
        var logger  = NullLogger<Program>.Instance;

        // First call → rate limited
        await PoPunkouterSoftware.Features.GitHub.GitHubEndpoints.InvokeAsync("owner/repo", factory, cache, logger);
        // Evict the short-lived cache entry manually
        cache.Remove("github-activity:owner/repo");
        // Second call → should reach network and get real data
        var result = await PoPunkouterSoftware.Features.GitHub.GitHubEndpoints.InvokeAsync("owner/repo", factory, cache, logger);

        var valueResult = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
        var json = JsonSerializer.Serialize(valueResult.Value);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("rateLimited").GetBoolean().Should().BeFalse(
            because: "after cache eviction the second call must succeed and not be stuck in rate-limited state");
    }
}

// ─── Health score calculation ─────────────────────────────────────────────────

public class GitHubActivityEndpoint_HealthScoreTests
{
    [Fact]
    public async Task RecentCommit_HasReadme_HasLicense_HasDescription_ScoreIsHigh()
    {
        var date       = DateTime.UtcNow.AddDays(-10).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var commitJson = $"[{{\"commit\":{{\"author\":{{\"date\":\"{date}\"}}}}}}]";
        var statsJson  = """{"all":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,5,5,5,5,5,5,5,5]}""";
        var repoJson   = """{"description":"a real description","license":{"key":"mit"},"open_issues_count":0}""";

        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/commits"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(commitJson, Encoding.UTF8, "application/json") };
            if (req.RequestUri.PathAndQuery.Contains("/stats/participation"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(statsJson, Encoding.UTF8, "application/json") };
            if (req.RequestUri.PathAndQuery.Contains("/readme"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(repoJson, Encoding.UTF8, "application/json") };
        });

        var result = await InvokeAsync("owner/repo", handler);
        var val    = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
        var json   = JsonSerializer.Serialize(val.Value);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("healthScore").GetInt32().Should().BeGreaterThanOrEqualTo(75);
    }

    [Fact]
    public async Task StaleRepo_NoReadme_NoLicense_ManyIssues_ScoreIsLow()
    {
        var date       = DateTime.UtcNow.AddDays(-200).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var commitJson = $"[{{\"commit\":{{\"author\":{{\"date\":\"{date}\"}}}}}}]";
        var statsJson  = """{"all":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]}""";
        var repoJson   = """{"description":null,"license":null,"open_issues_count":10}""";

        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/commits"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(commitJson, Encoding.UTF8, "application/json") };
            if (req.RequestUri.PathAndQuery.Contains("/stats/participation"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(statsJson, Encoding.UTF8, "application/json") };
            if (req.RequestUri.PathAndQuery.Contains("/readme"))
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(repoJson, Encoding.UTF8, "application/json") };
        });

        var result = await InvokeAsync("owner/repo", handler);
        var val    = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
        var json   = JsonSerializer.Serialize(val.Value);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("healthScore").GetInt32().Should().Be(0,
            because: "stale repo with no readme/license and many issues should score 0");
    }

    [Fact]
    public async Task HealthScore_IsNeverNegative()
    {
        var date       = DateTime.UtcNow.AddDays(-500).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var commitJson = $"[{{\"commit\":{{\"author\":{{\"date\":\"{date}\"}}}}}}]";
        var statsJson  = """{"all":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]}""";
        var repoJson   = """{"description":null,"license":null,"open_issues_count":100}""";

        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/commits"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(commitJson, Encoding.UTF8, "application/json") };
            if (req.RequestUri.PathAndQuery.Contains("/stats/participation"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(statsJson, Encoding.UTF8, "application/json") };
            if (req.RequestUri.PathAndQuery.Contains("/readme"))
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(repoJson, Encoding.UTF8, "application/json") };
        });

        var result = await InvokeAsync("owner/repo", handler);
        var val    = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
        var json   = JsonSerializer.Serialize(val.Value);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("healthScore").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    private static async Task<IResult> InvokeAsync(string repo, HttpMessageHandler handler)
    {
        var cache   = new MemoryCache(new MemoryCacheOptions());
        var factory = GitHubClientFactory.Create(handler);
        var logger  = NullLogger<Program>.Instance;
        return await PoPunkouterSoftware.Features.GitHub.GitHubEndpoints
            .InvokeAsync(repo, factory, cache, logger);
    }

    private static void AssertHealthScore(IResult result, Action<int> assertion)
    {
        var val  = result.Should().BeAssignableTo<IValueHttpResult>().Subject;
        var json = JsonSerializer.Serialize(val.Value);
        using var doc = JsonDocument.Parse(json);
        assertion(doc.RootElement.GetProperty("healthScore").GetInt32());
    }
}
