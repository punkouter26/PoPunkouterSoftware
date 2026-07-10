namespace PoPunkouterSoftware.Features.Config;

/// <summary>
/// Lets the client discover the canonical API base URL, environment mode, and
/// feature/model availability. This site has no authentication — there are
/// deliberately no auth/login flags here.
/// </summary>
internal static class ConfigEndpoints
{
    internal static WebApplication MapConfigEndpoints(this WebApplication app)
    {
        // isMockMode=true tells the UI to display the "MOCK DATA" banner (rule 10).
        // Activated when ASPNETCORE_ENVIRONMENT is "Testing" (integration / E2E test runs).
        app.MapGet("/api/config",
            (HttpContext ctx, IWebHostEnvironment env, IConfiguration config) => Results.Ok(new
            {
                apiBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api",
                isMockMode = env.IsEnvironment("Testing"),
                isProduction = env.IsProduction(),
                aiIntegrationEnabled = config.GetValue<bool>("FeatureFlags:EnableAiIntegration"),
                azureOpenAIConfigured =
                    !string.IsNullOrWhiteSpace(config["AzureOpenAI:Endpoint"]) &&
                    !string.IsNullOrWhiteSpace(config["AzureOpenAI:ApiKey"]) &&
                    !string.IsNullOrWhiteSpace(config["AzureOpenAI:DeploymentName"]),
                managementActionsEnabled = config.GetValue<bool>("FeatureFlags:EnableManagementActions", env.IsDevelopment() || env.IsEnvironment("Testing")),
                modelCatalog = new
                {
                    remote = new[]
                    {
                        new { id = "azure-gpt-5.4-nano", label = "Azure OpenAI GPT-5.4 Nano" }
                    },
                    browser = new[]
                    {
                        new { id = "browser-summarizer", label = "Browser Summarizer" },
                        new { id = "browser-writer", label = "Browser Writer" }
                    },
                    ollama = new[]
                    {
                        new { id = "ollama-llama3.1", label = "Ollama llama3.1" },
                        new { id = "ollama-qwen2.5", label = "Ollama qwen2.5" }
                    }
                }
            }))
            .WithName("GetConfig").WithTags("Config");

        return app;
    }
}
