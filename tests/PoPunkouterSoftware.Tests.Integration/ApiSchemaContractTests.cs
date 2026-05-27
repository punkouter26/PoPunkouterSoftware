using System.Text.Json;

namespace PoPunkouterSoftware.Tests.Integration;

[Collection("WebApp")]
public class ApiSchemaContractTests
{
    private readonly HttpClient _client;

    public ApiSchemaContractTests(TestWebApp factory) => _client = factory.CreateClient();

    [Theory]
    [InlineData("status")]
    [InlineData("application")]
    [InlineData("timestamp")]
    [InlineData("checks")]
    [InlineData("environment")]
    [InlineData("config")]
    public async Task Health_HasCamelCaseProperty(string property)
    {
        var json = await _client.GetStringAsync("/api/health");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty(property, out _)
            .Should().BeTrue(because: $"/api/health must expose camelCase '{property}'");
    }

    [Fact]
    public async Task Config_HasCamelCase_ApiBase()
    {
        var json = await _client.GetStringAsync("/api/config");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("apiBase", out _)
            .Should().BeTrue(because: "/api/config must expose camelCase 'apiBase'");
    }

    [Fact]
    public async Task AzStatus_HasCamelCase_LoggedIn()
    {
        var json = await _client.GetStringAsync("/api/diag/az-status");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("loggedIn", out _)
            .Should().BeTrue(because: "/api/diag/az-status must expose camelCase 'loggedIn'");
    }

    [Theory]
    [InlineData("generatedAt")]
    [InlineData("webServices")]
    [InlineData("subscription")]
    public async Task DiagReport_WhenOk_HasCamelCaseProperty(string property)
    {
        var response = await _client.GetAsync("/api/diag/report");
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            return;

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty(property, out _)
            .Should().BeTrue(because: $"/api/diag/report must expose camelCase '{property}'");
    }

    [Fact]
    public async Task DiagReport_WhenOk_WebServices_TotalIsCamelCase()
    {
        var response = await _client.GetAsync("/api/diag/report");
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            return;

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("webServices", out var ws))
            return;

        ws.TryGetProperty("total", out _)
            .Should().BeTrue(because: "webServices.total must be camelCase");
    }
}