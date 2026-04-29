using System.Net.Http;

namespace PoPunkouterSoftware.Infrastructure;

public interface IAzureClientFactory
{
    HttpClient CreateHealthClient();
    HttpClient CreateAzureProbeClient();
}