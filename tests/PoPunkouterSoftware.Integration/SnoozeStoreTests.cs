using Microsoft.Extensions.Configuration;
using PoPunkouterSoftware.Infrastructure;
using Testcontainers.Azurite;

namespace PoPunkouterSoftware.Integration;

public class SnoozeStoreNoConnectionTests
{
    [Fact]
    public async Task UpsertAsync_WhenConnectionStringIsEmpty_ReturnsFailureResult()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureTableStorage:ConnectionString"] = "",
                ["AzureTableStorage:Endpoint"] = "",
            })
            .Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<SnoozeStore>.Instance;
        var store = new SnoozeStore(logger, config);

        var result = await store.UpsertAsync("Reliability|no-connection", DateTimeOffset.UtcNow.AddDays(1), null);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Table client not available");
    }
}

/// <summary>
/// Exercises <see cref="SnoozeStore"/> directly against a real Azurite table service so an
/// already-expired entry can be seeded (the <c>/api/diag/snooze</c> endpoint rejects
/// DurationDays &lt;= 0, so genuine past-expiry coverage requires calling the store, not the
/// endpoint, with an explicit past <c>expiresAtUtc</c>).
/// </summary>
public class SnoozeStoreAzuriteTests : IAsyncLifetime
{
    private readonly AzuriteContainer _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private SnoozeStore BuildStore()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureTableStorage:ConnectionString"] = _container.GetConnectionString()
            })
            .Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<SnoozeStore>.Instance;
        return new SnoozeStore(logger, config);
    }

    [Fact]
    public async Task UpsertThenGetActive_RoundTrips_KeyReasonAndExpiry()
    {
        var store = BuildStore();
        var key = "Reliability|my-app-name";
        var expiresAtUtc = DateTimeOffset.UtcNow.AddDays(5);

        var upsertResult = await store.UpsertAsync(key, expiresAtUtc, "known flaky deploy", default);
        upsertResult.IsSuccess.Should().BeTrue();
        upsertResult.Value!.Key.Should().Be(key);
        upsertResult.Value.Reason.Should().Be("known flaky deploy");

        var activeResult = await store.GetActiveAsync();
        activeResult.IsSuccess.Should().BeTrue();
        activeResult.Value.Should().ContainSingle(e => e.Key == key);
        var entry = activeResult.Value!.Single(e => e.Key == key);
        entry.ExpiresAtUtc.Should().BeCloseTo(expiresAtUtc, TimeSpan.FromSeconds(2));
        entry.Reason.Should().Be("known flaky deploy");
    }

    [Fact]
    public async Task Upsert_WithKeyContainingUnsafeRowKeyCharacters_RoundTrips()
    {
        // Table Storage RowKey forbids '/', '\', '#', '?' — the client's opaque key can
        // contain any of these (e.g. the '|'-delimited scheme in the feature spec). The
        // store must hash the RowKey and still return the raw key unmangled.
        var store = BuildStore();
        var key = "Storage|rg-foo/bar|my-storage-acct#1?x";

        var upsertResult = await store.UpsertAsync(key, DateTimeOffset.UtcNow.AddDays(1), null);
        upsertResult.IsSuccess.Should().BeTrue();

        var activeResult = await store.GetActiveAsync();
        activeResult.Value.Should().Contain(e => e.Key == key);
    }

    [Fact]
    public async Task GetActiveAsync_ExcludesEntriesPastTheirExpiry()
    {
        var store = BuildStore();
        var expiredKey = "Reliability|already-expired";
        var activeKey = "Reliability|still-active";

        await store.UpsertAsync(expiredKey, DateTimeOffset.UtcNow.AddMinutes(-5), "stale");
        await store.UpsertAsync(activeKey, DateTimeOffset.UtcNow.AddDays(1), "fresh");

        var result = await store.GetActiveAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain(e => e.Key == activeKey);
        result.Value.Should().NotContain(e => e.Key == expiredKey,
            because: "an entry whose ExpiresAtUtc is in the past must not be reported as active");
    }
}
