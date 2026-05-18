namespace PoPunkouterSoftware.Features.Azure;

internal static class AiEndpointGuard
{
    internal record AiConfig(string Endpoint, string ApiKey, string Deployment);

    internal static (IResult? Blocked, AiConfig? Config) Validate(IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("FeatureFlags:EnableAiIntegration"))
            return (Results.Ok(new { disabled = true, message = "AI integration is disabled. Set FeatureFlags:EnableAiIntegration=true to enable." }), null);

        var endpoint   = configuration["AzureOpenAI:Endpoint"];
        var apiKey     = configuration["AzureOpenAI:ApiKey"];
        var deployment = configuration["AzureOpenAI:DeploymentName"] ?? "gpt-4o";

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            return (Results.Ok(new { disabled = true, message = "Azure OpenAI is not configured. Add AzureOpenAI:Endpoint and AzureOpenAI:ApiKey to configuration or Key Vault." }), null);

        return (null, new AiConfig(endpoint, apiKey, deployment));
    }
}
