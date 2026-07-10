using Microsoft.Playwright;

namespace PoPunkouterSoftware.Tests.E2E;

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
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Azure status" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".app-portfolio-card").First).ToBeVisibleAsync();
        await CaptureAsync(page, "01-home.png");
    }

    [Fact]
    public async Task Azure_DefaultIsCompact_AndAdvancedDetailsAreOnDemand()
    {
        var page = await _browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var response = await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Services" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "All resources" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".azure-glance-grid > article")).ToHaveCountAsync(3);
        await CaptureAsync(page, "02-azure-status.png");

        await page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "All resources" })).ToBeVisibleAsync();
        await CaptureAsync(page, "03-azure-advanced.png");
    }

    [Fact]
    public async Task CoreRoutes_ReflowWithoutHorizontalOverflow_OnMobile()
    {
        var page = await _browser.NewPageAsync(new() { ViewportSize = new() { Width = 390, Height = 844 }, IsMobile = true });

        await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"))
            .Should().BeTrue();
        await CaptureAsync(page, "04-home-mobile.png");

        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"))
            .Should().BeTrue();
        await CaptureAsync(page, "05-azure-mobile.png");
    }

    private static async Task CaptureAsync(IPage page, string filename)
    {
        if (Environment.GetEnvironmentVariable("CAPTURE_SCREENSHOTS") != "1")
            return;

        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ux-audit"));
        Directory.CreateDirectory(directory);
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, filename), FullPage = true });
    }
}
