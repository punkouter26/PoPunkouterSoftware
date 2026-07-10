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
        // NOTE: We deliberately do NOT echo the App Insights or Azure OpenAI connection
        // strings / keys here. The browser only needs to know which endpoints exist, not
        // whether they're configured; the latter is server-side state.
        app.MapGet("/api/config",
            (HttpContext ctx, IWebHostEnvironment env, IConfiguration config) =>
            {
                var openAiConfigured =
                    !string.IsNullOrWhiteSpace(config["AzureOpenAI:Endpoint"]) &&
                    !string.IsNullOrWhiteSpace(config["AzureOpenAI:ApiKey"]) &&
                    !string.IsNullOrWhiteSpace(config["AzureOpenAI:DeploymentName"]);
                return Results.Ok(new
                {
                    apiBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api",
                    isMockMode = env.IsEnvironment("Testing"),
                    isProduction = env.IsProduction(),
                    // Booleans only — the server side owns the secrets.
                    aiIntegrationEnabled = config.GetValue<bool>("FeatureFlags:EnableAiIntegration"),
                    azureOpenAIConfigured = openAiConfigured,
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
                });
            })
            .WithName("GetConfig").WithTags("Config");

        return app;
    }
}
