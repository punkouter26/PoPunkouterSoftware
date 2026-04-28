using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PoPunkouterSoftware.Shared.Azure;
using PoPunkouterSoftware.Features.Azure;
using PoPunkouterSoftware.Infrastructure;
using Azure.Security.KeyVault.Secrets;

namespace PoPunkouterSoftware.Tests.Unit;

// ─── AppKeyVaultSecretManager ─────────────────────────────────────────────────

public class AppKeyVaultSecretManagerTests
{
    private readonly AppKeyVaultSecretManager _manager = new();

    [Theory]
    [InlineData("PoPunkouterSoftware--ApplicationInsights--ConnectionString", true)]
    [InlineData("PoPunkouterSoftware--AzureBlobStorage--ConnectionString",    true)]
    [InlineData("popunkouter software--foo",                                   false)]
    [InlineData("OtherApp--Secret",                                            false)]
    [InlineData("ApplicationInsights--ConnectionString",                       false)]
    public void Load_FiltersOnlyPoPunkouterSoftwarePrefix(string secretName, bool expected)
    {
        var props = new SecretProperties(secretName);
        _manager.Load(props).Should().Be(expected);
    }

    [Theory]
    [InlineData("PoPunkouterSoftware--ApplicationInsights--ConnectionString",
                "ApplicationInsights:ConnectionString")]
    [InlineData("PoPunkouterSoftware--AzureBlobStorage--ConnectionString",
                "AzureBlobStorage:ConnectionString")]
    [InlineData("PoPunkouterSoftware--Foo",
                "Foo")]
    public void GetKey_StripsPrefix_AndReplacesDoubleDashWithColon(string secretName, string expectedKey)
    {
        var secret = SecretModelFactory.KeyVaultSecret(
            new SecretProperties(secretName), "any-value");
        _manager.GetKey(secret).Should().Be(expectedKey);
    }
}

// ─── AzureModels record constructors ─────────────────────────────────────────

public class AzureModelsTests
{
    [Fact]
    public void AzureReport_DefaultValues_AreNull()
    {
        var report = new AzureReport();
        report.GeneratedAt.Should().BeNull();
        report.Subscription.Should().BeNull();
        report.WebServices.Should().BeNull();
    }

    [Fact]
    public void WebService_DefaultStrings_AreEmpty()
    {
        var svc = new WebService();
        svc.Name.Should().BeEmpty();
        svc.Url.Should().BeEmpty();
        svc.HttpStatus.Should().BeEmpty();
    }

    [Fact]
    public void AzureReport_WithInit_SetsValues()
    {
        var report = new AzureReport
        {
            GeneratedAt  = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc),
            Subscription = new SubscriptionInfo { Name = "Punkouter26" }
        };
        report.GeneratedAt.Should().Be(new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc));
        report.Subscription!.Name.Should().Be("Punkouter26");
    }

    [Fact]
    public void WebServicesInfo_Services_DefaultsToEmptyList()
    {
        var info = new WebServicesInfo();
        info.Services.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void ByStatusInfo_DefaultsToZero()
    {
        var info = new ByStatusInfo();
        info.Active.Should().Be(0);
        info.Broken.Should().Be(0);
        info.Other.Should().Be(0);
    }
}

// ─── AppsJsonSyncer (via temp file integration) ───────────────────────────────

public class AppsJsonSyncerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public AppsJsonSyncerTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string TempPath(string filename) => Path.Combine(_tempDir, filename);

    private static AzureReport BuildReport(params (string name, string url, string type)[] services)
    {
        var svcs = services.Select(s => new WebService
        {
            Name         = s.name,
            FriendlyName = s.name,
            Url          = s.url,
            ResourceType = s.type
        }).ToList();
        return new AzureReport
        {
            WebServices = new WebServicesInfo { Services = svcs }
        };
    }

    [Fact]
    public async Task SyncAsync_NewService_IsAddedAsInactive()
    {
        var path   = TempPath("apps.json");
        var report = BuildReport(("my-new-app", "https://my-new-app.azurewebsites.net", "microsoft.web/sites"));

        await AppsJsonSyncer.SyncAsync(report, path);

        var json    = await File.ReadAllTextAsync(path);
        var doc     = JsonDocument.Parse(json);
        var apps    = doc.RootElement.GetProperty("apps").EnumerateArray().ToList();
        apps.Should().HaveCount(1);
        apps[0].GetProperty("status").GetString().Should().Be("inactive");
        apps[0].GetProperty("url").GetString().Should().Be("https://my-new-app.azurewebsites.net");
    }

    [Fact]
    public async Task SyncAsync_ExistingActiveService_StatusNotChanged()
    {
        var path = TempPath("apps.json");
        var initial = """{"apps":[{"id":"my-app","name":"MyApp","description":"desc","status":"active","url":"https://my-app.azurewebsites.net","technologies":["custom"]}]}""";
        await File.WriteAllTextAsync(path, initial);

        var report = BuildReport(("my-app", "https://my-app.azurewebsites.net", "microsoft.web/sites"));
        await AppsJsonSyncer.SyncAsync(report, path);

        var json = await File.ReadAllTextAsync(path);
        var doc  = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("apps")[0].GetProperty("status").GetString().Should().Be("active");
    }

    [Fact]
    public async Task SyncAsync_ExistingService_GenericTech_IsReplaced()
    {
        var path = TempPath("apps.json");
        var initial = """{"apps":[{"id":"my-app","name":"MyApp","description":"desc","status":"active","url":"https://my-app.azurewebsites.net","technologies":["Azure"]}]}""";
        await File.WriteAllTextAsync(path, initial);

        var report = BuildReport(("my-app", "https://my-app.azurewebsites.net", "microsoft.web/sites"));
        await AppsJsonSyncer.SyncAsync(report, path);

        var json   = await File.ReadAllTextAsync(path);
        var doc    = JsonDocument.Parse(json);
        var techs  = doc.RootElement.GetProperty("apps")[0].GetProperty("technologies")
            .EnumerateArray().Select(t => t.GetString()).ToList();
        techs.Should().Contain("Azure App Service");
    }

    [Fact]
    public async Task SyncAsync_ExistingService_CustomTech_IsPreserved()
    {
        var path = TempPath("apps.json");
        var initial = """{"apps":[{"id":"my-app","name":"MyApp","description":"desc","status":"active","url":"https://my-app.azurewebsites.net","technologies":["React","TypeScript"]}]}""";
        await File.WriteAllTextAsync(path, initial);

        var report = BuildReport(("my-app", "https://my-app.azurewebsites.net", "microsoft.web/sites"));
        await AppsJsonSyncer.SyncAsync(report, path);

        var json  = await File.ReadAllTextAsync(path);
        var doc   = JsonDocument.Parse(json);
        var techs = doc.RootElement.GetProperty("apps")[0].GetProperty("technologies")
            .EnumerateArray().Select(t => t.GetString()).ToList();
        techs.Should().Contain("React").And.Contain("TypeScript");
    }

    [Fact]
    public async Task SyncAsync_StaticWebApp_InfersTechCorrectly()
    {
        var path   = TempPath("apps.json");
        var report = BuildReport(("my-swa", "https://my-swa.azurestaticapps.net", "microsoft.web/staticsites"));
        await AppsJsonSyncer.SyncAsync(report, path);

        var json   = await File.ReadAllTextAsync(path);
        var doc    = JsonDocument.Parse(json);
        var techs  = doc.RootElement.GetProperty("apps")[0].GetProperty("technologies")
            .EnumerateArray().Select(t => t.GetString()).ToList();
        techs.Should().Contain("Azure Static Web Apps").And.Contain("JavaScript");
    }

    [Fact]
    public async Task SyncAsync_ContainerApp_InfersTechCorrectly()
    {
        var path   = TempPath("apps.json");
        var report = BuildReport(("my-container", "https://my-container.azurecontainerapps.io", "microsoft.app/containerapps"));
        await AppsJsonSyncer.SyncAsync(report, path);

        var json   = await File.ReadAllTextAsync(path);
        var doc    = JsonDocument.Parse(json);
        var techs  = doc.RootElement.GetProperty("apps")[0].GetProperty("technologies")
            .EnumerateArray().Select(t => t.GetString()).ToList();
        techs.Should().Contain("Azure Container Apps").And.Contain("Docker");
    }

    [Fact]
    public async Task SyncAsync_UnknownType_FallsBackToAzure()
    {
        var path   = TempPath("apps.json");
        var report = BuildReport(("my-func", "https://my-func.azurewebsites.net", "microsoft.web/functions"));
        await AppsJsonSyncer.SyncAsync(report, path);

        var json  = await File.ReadAllTextAsync(path);
        var doc   = JsonDocument.Parse(json);
        var techs = doc.RootElement.GetProperty("apps")[0].GetProperty("technologies")
            .EnumerateArray().Select(t => t.GetString()).ToList();
        techs.Should().ContainSingle().Which.Should().Be("Azure");
    }

    [Fact]
    public async Task SyncAsync_MultipleServices_ActiveAppearsFirst()
    {
        var path = TempPath("apps.json");
        var initial =
            "{\"apps\":[" +
            "{\"id\":\"beta-app\",\"name\":\"Beta\",\"description\":\"desc\",\"status\":\"inactive\",\"url\":\"https://beta.azurewebsites.net\",\"technologies\":[\"Azure\"]}," +
            "{\"id\":\"alpha-app\",\"name\":\"Alpha\",\"description\":\"desc\",\"status\":\"active\",\"url\":\"https://alpha.azurewebsites.net\",\"technologies\":[\"Azure\"]}" +
            "]}";
        await File.WriteAllTextAsync(path, initial);

        var report = BuildReport(
            ("alpha-app", "https://alpha.azurewebsites.net", "microsoft.web/sites"),
            ("beta-app",  "https://beta.azurewebsites.net",  "microsoft.web/sites"));
        await AppsJsonSyncer.SyncAsync(report, path);

        var json  = await File.ReadAllTextAsync(path);
        var doc   = JsonDocument.Parse(json);
        var apps  = doc.RootElement.GetProperty("apps").EnumerateArray().ToList();
        apps[0].GetProperty("status").GetString().Should().Be("active");
    }

    [Fact]
    public async Task SyncAsync_EmptyReport_PreservesExistingApps()
    {
        var path = TempPath("apps.json");
        var initial = """{"apps":[{"id":"my-app","name":"MyApp","description":"desc","status":"active","url":"https://my-app.azurewebsites.net","technologies":[]}]}""";
        await File.WriteAllTextAsync(path, initial);

        var report = new AzureReport { WebServices = new WebServicesInfo { Services = new List<WebService>() } };
        await AppsJsonSyncer.SyncAsync(report, path);

        var json = await File.ReadAllTextAsync(path);
        var doc  = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("apps").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task SyncAsync_MissingAppsJson_CreatesNewFile()
    {
        var path   = TempPath("missing-apps.json");
        var report = BuildReport(("new-svc", "https://new-svc.azurewebsites.net", "microsoft.web/sites"));

        await AppsJsonSyncer.SyncAsync(report, path);

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task SyncAsync_ServiceWithEmptyUrl_IsSkipped()
    {
        var path   = TempPath("apps.json");
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services = new List<WebService>
                {
                    new WebService { Name = "no-url-app", Url = "" }
                }
            }
        };
        await AppsJsonSyncer.SyncAsync(report, path);

        var json  = await File.ReadAllTextAsync(path);
        var doc   = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("apps").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task SyncAsync_GameNamedApp_CategoryIsGames()
    {
        var path   = TempPath("apps.json");
        var report = BuildReport(("po-quiz-game", "https://po-quiz-game.azurewebsites.net", "microsoft.web/sites"));
        await AppsJsonSyncer.SyncAsync(report, path);

        var json = await File.ReadAllTextAsync(path);
        var doc  = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("apps")[0].GetProperty("category").GetString().Should().Be("games");
    }

    [Fact]
    public async Task SyncAsync_AiNamedApp_CategoryIsAi()
    {
        var path   = TempPath("apps.json");
        var report = BuildReport(("po-robot-ai", "https://po-robot-ai.azurewebsites.net", "microsoft.web/sites"));
        await AppsJsonSyncer.SyncAsync(report, path);

        var json = await File.ReadAllTextAsync(path);
        var doc  = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("apps")[0].GetProperty("category").GetString().Should().Be("ai");
    }

    [Fact]
    public async Task SyncAsync_HealthApp_CategoryIsHealth()
    {
        var path   = TempPath("apps.json");
        var report = BuildReport(("po-runner", "https://po-runner.azurewebsites.net", "microsoft.web/sites"));
        await AppsJsonSyncer.SyncAsync(report, path);

        var json = await File.ReadAllTextAsync(path);
        var doc  = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("apps")[0].GetProperty("category").GetString().Should().Be("health");
    }

    [Fact]
    public async Task SyncAsync_ProductivityApp_CategoryIsProductivity()
    {
        var path   = TempPath("apps.json");
        var report = BuildReport(("po-links", "https://po-links.azurewebsites.net", "microsoft.web/sites"));
        await AppsJsonSyncer.SyncAsync(report, path);

        var json = await File.ReadAllTextAsync(path);
        var doc  = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("apps")[0].GetProperty("category").GetString().Should().Be("productivity");
    }

    [Fact]
    public async Task SyncAsync_AppWithAzureInternalDescription_IsReplaced()
    {
        var path    = TempPath("apps.json");
        var report  = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services = new List<WebService>
                {
                    new WebService { Name = "stapp-abc123", FriendlyName = "StappAbc123",
                        Url = "https://stapp-abc123.azurestaticapps.net",
                        Description  = "stapp-abc123 app",
                        ResourceType = "microsoft.web/staticsites" }
                }
            }
        };
        await AppsJsonSyncer.SyncAsync(report, path);

        var json = await File.ReadAllTextAsync(path);
        var doc  = JsonDocument.Parse(json);
        var desc = doc.RootElement.GetProperty("apps")[0].GetProperty("description").GetString();
        desc.Should().NotBe("stapp-abc123 app", "Azure internal names should be sanitized");
    }

    [Fact]
    public async Task SyncAsync_AppWithRealDescription_IsPreserved()
    {
        var path   = TempPath("apps.json");
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services = new List<WebService>
                {
                    new WebService { Name = "po-links", FriendlyName = "PoLinks",
                        Url         = "https://po-links.azurewebsites.net",
                        Description = "A link management dashboard for Azure resources",
                        ResourceType = "microsoft.web/sites" }
                }
            }
        };
        await AppsJsonSyncer.SyncAsync(report, path);

        var json = await File.ReadAllTextAsync(path);
        var doc  = JsonDocument.Parse(json);
        var desc = doc.RootElement.GetProperty("apps")[0].GetProperty("description").GetString();
        desc.Should().Be("A link management dashboard for Azure resources");
    }

    [Fact]
    public async Task SyncAsync_DuplicateUrls_OnlyFirstEntryKept()
    {
        var path   = TempPath("apps.json");
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services = new List<WebService>
                {
                    new WebService { Name = "app-a", FriendlyName = "AppA", Url = "https://same.azurewebsites.net", ResourceType = "microsoft.web/sites" },
                    new WebService { Name = "app-b", FriendlyName = "AppB", Url = "https://same.azurewebsites.net", ResourceType = "microsoft.web/sites" }
                }
            }
        };
        await AppsJsonSyncer.SyncAsync(report, path);

        var json  = await File.ReadAllTextAsync(path);
        var doc   = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("apps").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task SyncAsync_NullReport_WebServices_DoesNotThrow()
    {
        var path   = TempPath("apps.json");
        var report = new AzureReport { WebServices = null };

        var act = async () => await AppsJsonSyncer.SyncAsync(report, path);
        await act.Should().NotThrowAsync();
    }
}

// ─── AzureReportStore — unit-level (no Blob Storage, tests null-path behavior) ─

public class AzureReportStoreTests
{
    [Fact]
    public async Task LoadAsync_WhenConnectionStringIsEmpty_ReturnsNull()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["AzureBlobStorage:ConnectionString"] = ""
            })
            .Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureReportStore>.Instance;
        var store  = new AzureReportStore(logger, config);

        var result = await store.LoadAsync();
        result.Should().BeNull();
    }
}
