using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;

namespace PoPunkouterSoftware.Tests.Integration;

// ─── Helpers ─────────────────────────────────────────────────────────────────
// File-scoped copies of the Unit\GitHubActivityTests.cs helpers: file-scoped types
// are invisible across files, so the moved validation tests carry their own minimal set.

file static class TestHybridCache
{
    /// <summary>Mirrors the production registration in Program.cs (sized MemoryCache + HybridCache).</summary>
    public static HybridCache Create()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache(o => o.SizeLimit = 2048);
        services.AddHybridCache(o => o.MaximumKeyLength = 256);
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }
}

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
        var statsJson = """{"all":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,2,3,4,5,6,7,8]}""";
        var repoJson = """{"description":"d","license":{"key":"mit"},"open_issues_count":0}""";

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
        var cache = TestHybridCache.Create();
        var factory = GitHubClientFactory.Create(handler);
        var logger = NullLogger<Program>.Instance;
        return await PoPunkouterSoftware.Features.GitHub.GitHubEndpoints
            .InvokeAsync(repo, factory, cache, logger);
    }
}
