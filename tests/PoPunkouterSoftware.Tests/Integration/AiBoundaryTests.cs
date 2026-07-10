using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;

namespace PoPunkouterSoftware.Tests.Integration;

/// <summary>
/// Proves the Testing-environment AI mock boundary: the "azure-openai" typed client's
/// primary handler is the in-process <c>TestingAiStubHandler</c>, so no test run can
/// reach Azure AI Foundry (no token spend, no secrets, no sockets).
/// </summary>
[Collection("WebApp")]
public class AiBoundaryTests(TestWebApp factory)
{
    [Fact]
    public async Task AzureOpenAiClient_InTestingEnv_IsAnsweredByStub_WithoutNetwork()
    {
        var httpFactory = factory.Services.GetRequiredService<IHttpClientFactory>();
        var client = httpFactory.CreateClient("azure-openai");

        // The .invalid TLD can never resolve — if this request left the process it
        // would fail DNS, so a 200 proves the stub answered in-process.
        var resp = await client.PostAsync(
            "https://ai-boundary-must-not-resolve.invalid/openai/deployments/x/chat/completions",
            new StringContent("""{"messages":[]}""", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("stub-model");
        body.Should().Contain("stubbed AI response");
    }
}
