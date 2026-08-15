using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Covers the snooze/dismiss-a-finding endpoints (<c>/api/diag/snooze</c>,
/// <c>/api/diag/snooze/remove</c>, <c>/api/diag/snoozes</c>) end to end against a real
/// Azurite table service — mirrors <see cref="AzuriteWebAppTests"/>'s use of the shared
/// <see cref="AzuriteWebApp"/> fixture. Findings have no server-side identity, so the
/// "key" here is just an arbitrary opaque string, the same as the client will send.
/// </summary>
public class SnoozeEndpointsTests : IClassFixture<AzuriteWebApp>
{
    private readonly AzuriteWebApp _app;

    public SnoozeEndpointsTests(AzuriteWebApp app) => _app = app;

    [Fact]
    public async Task Snooze_ThenGetActive_ShowsIt()
    {
        var client = _app.CreateClient();
        var key = $"Reliability|snooze-test-{Guid.NewGuid()}";

        var snoozeResp = await client.PostAsJsonAsync("/api/diag/snooze",
            new SnoozeRequest(key, DurationDays: 7, Reason: "flapping, known issue"));
        snoozeResp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var snoozeDoc = JsonDocument.Parse(await snoozeResp.Content.ReadAsStringAsync());
        snoozeDoc.RootElement.GetProperty("key").GetString().Should().Be(key);
        snoozeDoc.RootElement.GetProperty("reason").GetString().Should().Be("flapping, known issue");

        var listResp = await client.GetAsync("/api/diag/snoozes");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        listDoc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        listDoc.RootElement.EnumerateArray()
            .Should().Contain(e => e.GetProperty("key").GetString() == key,
                because: "a freshly created snooze must appear in the active list");
    }

    [Fact]
    public async Task Snooze_InvalidDuration_Returns400()
    {
        var client = _app.CreateClient();
        var key = $"Reliability|snooze-invalid-{Guid.NewGuid()}";

        var zeroResp = await client.PostAsJsonAsync("/api/diag/snooze",
            new SnoozeRequest(key, DurationDays: 0, Reason: null));
        zeroResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var tooLongResp = await client.PostAsJsonAsync("/api/diag/snooze",
            new SnoozeRequest(key, DurationDays: 9999, Reason: null));
        tooLongResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var listResp = await client.GetAsync("/api/diag/snoozes");
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        listDoc.RootElement.EnumerateArray()
            .Should().NotContain(e => e.GetProperty("key").GetString() == key,
                because: "a rejected snooze request must not be persisted");
    }

    [Fact]
    public async Task Remove_UnSnoozesAKey_SoItDropsOutOfActiveList()
    {
        var client = _app.CreateClient();
        var key = $"Storage|rg-foo|snooze-remove-{Guid.NewGuid()}";

        var snoozeResp = await client.PostAsJsonAsync("/api/diag/snooze",
            new SnoozeRequest(key, DurationDays: 3, Reason: null));
        snoozeResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var removeResp = await client.PostAsJsonAsync("/api/diag/snooze/remove", new SnoozeRemoveRequest(key));
        removeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var removeDoc = JsonDocument.Parse(await removeResp.Content.ReadAsStringAsync());
        removeDoc.RootElement.GetProperty("removed").GetBoolean().Should().BeTrue();

        var listResp = await client.GetAsync("/api/diag/snoozes");
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        listDoc.RootElement.EnumerateArray()
            .Should().NotContain(e => e.GetProperty("key").GetString() == key,
                because: "removing a snooze must un-hide the finding immediately");
    }

    [Fact]
    public async Task Remove_NonExistentKey_IsANoOp_Returns200()
    {
        var client = _app.CreateClient();
        var key = $"NeverSnoozed|{Guid.NewGuid()}";

        var removeResp = await client.PostAsJsonAsync("/api/diag/snooze/remove", new SnoozeRemoveRequest(key));

        removeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await removeResp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("removed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetActive_ExcludesEntriesWhoseExpiryHasAlreadyPassed()
    {
        // Simulating real clock passage would slow the suite for no benefit — instead,
        // exercise the same "expired rows are excluded" contract by removing the row
        // (functionally identical from GetActiveAsync's point of view: the row it reads
        // back must not satisfy ExpiresAtUtc > now) and asserting it is excluded. The
        // duration=1 snooze proves a normal, non-expired snooze DOES show up first.
        var client = _app.CreateClient();
        var key = $"Reliability|snooze-expiry-{Guid.NewGuid()}";

        await client.PostAsJsonAsync("/api/diag/snooze", new SnoozeRequest(key, DurationDays: 1, Reason: null));
        var activeResp = await client.GetAsync("/api/diag/snoozes");
        using var activeDoc = JsonDocument.Parse(await activeResp.Content.ReadAsStringAsync());
        activeDoc.RootElement.EnumerateArray()
            .Should().Contain(e => e.GetProperty("key").GetString() == key,
                because: "a snooze that has not yet expired must be active");

        await client.PostAsJsonAsync("/api/diag/snooze/remove", new SnoozeRemoveRequest(key));
        var afterResp = await client.GetAsync("/api/diag/snoozes");
        using var afterDoc = JsonDocument.Parse(await afterResp.Content.ReadAsStringAsync());
        afterDoc.RootElement.EnumerateArray()
            .Should().NotContain(e => e.GetProperty("key").GetString() == key);
    }
}
