using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

namespace PoPunkouterSoftware.Infrastructure;

public class AzureClientFactory : IAzureClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AzureClientFactory(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public HttpClient CreateHealthClient()
    {
        return _httpClientFactory.CreateClient("health");
    }

    public HttpClient CreateAzureProbeClient()
    {
        return _httpClientFactory.CreateClient("azure-probe");
    }
}