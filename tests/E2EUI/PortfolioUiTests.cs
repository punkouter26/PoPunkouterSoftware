using Microsoft.Playwright;

namespace PoPunkouterSoftware.E2EUI;

/// <summary>
/// E2EUI — C# Playwright tests driving a real browser against the running app.
/// Point at a live instance via BASE_URL (default http://localhost:8000); start it with
/// F5 or `dotnet run`. These are run locally / on demand — never in CI/CD.
/// First run requires browsers: pwsh bin/Debug/net10.0/playwright.ps1 install
/// </summary>
public class PortfolioUiTests : IAsyncLifetime
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:8000";

    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;

    public async Task InitializeAsync()
    {
        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _pw.Dispose();
    }

    [Fact]
    public async Task Portfolio_Home_LoadsAndShowsBrand()
    {
        var page = await _browser.NewPageAsync();
        var response = await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        (await page.TitleAsync()).Should().Contain("PoPunkouterSoftware");
        await Assertions.Expect(page.GetByText("PoPunkouterSoftware").First).ToBeVisibleAsync();
    }
}
