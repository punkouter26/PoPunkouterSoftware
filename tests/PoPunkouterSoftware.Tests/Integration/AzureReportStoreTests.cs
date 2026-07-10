using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Configuration;
using PoPunkouterSoftware.Infrastructure.Azure;
using PoPunkouterSoftware.Shared.Azure;
using Testcontainers.Azurite;

namespace PoPunkouterSoftware.IntegrationTests;

public class AzureReportStoreNoConnectionTests
{
    [Fact]
    public async Task LoadAsync_WhenConnectionStringIsEmpty_ReturnsFailureResult()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureTableStorage:ConnectionString"] = "",
                ["AzureTableStorage:Endpoint"] = "",
            })
            .Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureReportStore>.Instance;
        var store = new AzureReportStore(logger, config);
        var result = await store.LoadAsync();
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Table client not available");
    }
}

public class AzureReportStoreAzuriteTests : IAsyncLifetime
{
    private readonly AzuriteContainer _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private AzureReportStore BuildStore()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureTableStorage:ConnectionString"] = _container.GetConnectionString()
            })
            .Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureReportStore>.Instance;
        return new AzureReportStore(logger, config);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips_Report()
    {
        var store = BuildStore();
        var original = new AzureReport
        {
            GeneratedAt = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc),
            Subscription = new SubscriptionInfo { Name = "Test-Sub" },
            WebServices = new WebServicesInfo
            {
                Total = 2,
                Services = new List<WebService>
                {
                    new() { Name = "svc-a", Url = "https://svc-a.azurewebsites.net", HttpStatus = "200" },
                    new() { Name = "svc-b", Url = "https://svc-b.azurewebsites.net", HttpStatus = "404" }
                }
            }
        };

        await store.SaveAsync(original);
        var result = await store.LoadAsync();
        result.IsSuccess.Should().BeTrue();
        var loaded = result.Value;
        loaded.Should().NotBeNull();
        loaded!.Subscription!.Name.Should().Be("Test-Sub");
        loaded.WebServices!.Services.Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveTwice_OnlyLatestDataRemains()
    {
        var store = BuildStore();
        await store.SaveAsync(new AzureReport { Subscription = new SubscriptionInfo { Name = "First" } });
        await store.SaveAsync(new AzureReport { Subscription = new SubscriptionInfo { Name = "Second" } });
        var result = await store.LoadAsync();
        result.IsSuccess.Should().BeTrue();
        result.Value!.Subscription!.Name.Should().Be("Second");
    }

    [Fact]
    public async Task Load_EmptyTable_ReturnsNull()
    {
        var store = BuildStore();
        var result = await store.LoadAsync();
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }
}

public class AzureReportStoreHistoryTests : IAsyncLifetime
{
    private readonly AzuriteContainer _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private AzureReportStore BuildStore()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureTableStorage:ConnectionString"] = _container.GetConnectionString()
            })
            .Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureReportStore>.Instance;
        return new AzureReportStore(logger, config);
    }

    [Fact]
    public async Task SaveThenSave_HistoryContainsPreviousReport()
    {
        var store = BuildStore();

        var first = new AzureReport
        {
            GeneratedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Subscription = new SubscriptionInfo { Name = "History-Sub-1" }
        };
        await store.SaveAsync(first);

        var second = new AzureReport
        {
            GeneratedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Subscription = new SubscriptionInfo { Name = "History-Sub-2" }
        };
        await store.SaveAsync(second);

        var latest = await store.LoadAsync();
        latest.IsSuccess.Should().BeTrue();
        latest.Value!.Subscription!.Name.Should().Be("History-Sub-2");

        var history = await store.LoadHistoryAsync();
        history.IsSuccess.Should().BeTrue();
        history.Value.Should().NotBeNull();
        history.Value!.Should().Contain(r => r.Subscription!.Name == "History-Sub-1",
            because: "the previous report must be persisted to history on each save");
    }

    [Fact]
    public async Task ConcurrentSaves_DoNotThrow_LastWriterWins()
    {
        var store = BuildStore();

        await store.SaveAsync(new AzureReport { Subscription = new SubscriptionInfo { Name = "seed" } });

        var tasks = Enumerable.Range(1, 5).Select(i => store.SaveAsync(new AzureReport
        {
            GeneratedAt = DateTime.UtcNow,
            Subscription = new SubscriptionInfo { Name = $"concurrent-{i}" }
        }));

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r =>
            r.IsSuccess.Should().BeTrue(because: "concurrent saves must not throw"));

        var final = await store.LoadAsync();
        final.IsSuccess.Should().BeTrue();
        final.Value.Should().NotBeNull();
        final.Value!.Subscription!.Name.Should().StartWith("concurrent-");
    }
}