using System.Text.Json;
using Microsoft.JSInterop;
using PoPunkouterSoftware.Client;
using PoPunkouterSoftware.Shared.Session;

namespace PoPunkouterSoftware.Tests.Unit;

public class PoAppSessionTests
{
    [Fact]
    public async Task SaveAsync_WritesSerializedSession_ToBrowserStorage()
    {
        var jsRuntime = new FakeJsRuntime();
        var session = new PoAppSession(jsRuntime);
        var expected = new PoUserSession("Guest", "GUEST1234", "guest1234@local.dev", true, true, "LOGGED IN");

        await session.SaveAsync(expected);

        jsRuntime.Storage.Should().ContainKey("po.session");
        var stored = JsonSerializer.Deserialize<PoUserSession>(
            jsRuntime.Storage["po.session"],
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        stored.Should().Be(expected);
    }

    [Fact]
    public async Task GetAsync_ReturnsPersistedSession_FromBrowserStorage()
    {
        var jsRuntime = new FakeJsRuntime();
        var expected = new PoUserSession("Microsoft", "MICROSOFT USER", "user@example.com", false, true, "LOGGED IN");
        jsRuntime.Storage["po.session"] = JsonSerializer.Serialize(expected);
        var session = new PoAppSession(jsRuntime);

        var actual = await session.GetAsync();

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task ClearAsync_RemovesPersistedSession_FromBrowserStorage()
    {
        var jsRuntime = new FakeJsRuntime();
        jsRuntime.Storage["po.session"] = "{\"displayName\":\"GUEST9999\"}";
        var session = new PoAppSession(jsRuntime);

        await session.ClearAsync();

        jsRuntime.Storage.Should().NotContainKey("po.session");
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        public Dictionary<string, string> Storage { get; } = new(StringComparer.Ordinal);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            return identifier switch
            {
                "poAppStorage.get" => new ValueTask<TValue>((TValue)(object?)Storage.GetValueOrDefault((string)args![0]!)!),
                "poAppStorage.set" => SetValue<TValue>(args),
                "poAppStorage.remove" => RemoveValue<TValue>(args),
                _ => throw new NotSupportedException($"Unsupported JS interop call: {identifier}")
            };
        }

        private ValueTask<TValue> SetValue<TValue>(object?[]? args)
        {
            Storage[(string)args![0]!] = (string)args[1]!;
            return ValueTask.FromResult(default(TValue)!);
        }

        private ValueTask<TValue> RemoveValue<TValue>(object?[]? args)
        {
            Storage.Remove((string)args![0]!);
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
