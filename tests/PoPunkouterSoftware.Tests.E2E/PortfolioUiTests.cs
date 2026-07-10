using Microsoft.Playwright;

namespace PoPunkouterSoftware.Tests.E2E;

/// <summary>
/// E2EUI — C# Playwright tests driving a real browser against the running app.
/// Point at a live instance via BASE_URL (default http://localhost:8000); start it with
/// F5 or `dotnet run`. These are run locally / on demand — never in CI/CD.
/// Every test runs at BOTH mobile-portrait and desktop-landscape form factors so the two
/// renderings cannot drift apart unnoticed.
/// First run requires browsers: pwsh bin/Debug/net10.0/playwright.ps1 install
/// </summary>
public class PortfolioUiTests : IAsyncLifetime
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:8000";

    /// <summary>label, width, height, isMobile — the two supported form factors.</summary>
    public static TheoryData<string, int, int, bool> Viewports => new()
    {
        { "mobile", 390, 844, true },
        { "desktop", 1440, 1000, false },
    };

    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;

    public async Task InitializeAsync()
    {
        _pw = await Playwright.CreateAsync();
        // Headless bundled Chromium by default; HEADED=1 shows the browser and
        // BROWSER_CHANNEL=chrome drives the installed Google Chrome instead.
        _browser = await _pw.Chromium.LaunchAsync(new()
        {
            Headless = Environment.GetEnvironmentVariable("HEADED") != "1",
            Channel = Environment.GetEnvironmentVariable("BROWSER_CHANNEL"),
        });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _pw.Dispose();
    }

    private async Task<IPage> NewPageAsync(int width, int height, bool isMobile) =>
        await _browser.NewPageAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height },
            IsMobile = isMobile,
        });

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Portfolio_Home_LoadsAndShowsBrand(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        var response = await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        (await page.TitleAsync()).Should().Contain("PoPunkouterSoftware");
        await Assertions.Expect(page.GetByText("PoPunkouterSoftware").First).ToBeVisibleAsync();
        // The Azure link keeps its "Azure status" text on desktop; at the mobile breakpoint
        // the label collapses to icon-only (Index.razor.css @max-640), so target the link
        // structurally there instead of by accessible name.
        if (isMobile)
            await Assertions.Expect(page.Locator("a.portfolio-azure-link")).ToBeVisibleAsync();
        else
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Azure status" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".app-portfolio-card").First).ToBeVisibleAsync();
        await CaptureAsync(page, $"01-home-{label}.png");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Azure_DefaultIsCompact_AndAdvancedDetailsAreOnDemand(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);
        var response = await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Services" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "All resources" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".azure-glance-grid > article")).ToHaveCountAsync(3);
        await CaptureAsync(page, $"02-azure-status-{label}.png");

        await page.GetByRole(AriaRole.Button, new() { Name = "Advanced diagnostics" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "All resources" })).ToBeVisibleAsync();
        await CaptureAsync(page, $"03-azure-advanced-{label}.png");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task CoreRoutes_ReflowWithoutHorizontalOverflow(string label, int width, int height, bool isMobile)
    {
        var page = await NewPageAsync(width, height, isMobile);

        await page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"))
            .Should().BeTrue($"home must not overflow horizontally at {label}");
        await CaptureAsync(page, $"04-home-reflow-{label}.png");

        await page.GotoAsync($"{BaseUrl}/azure", new() { WaitUntil = WaitUntilState.NetworkIdle });
        (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth"))
            .Should().BeTrue($"/azure must not overflow horizontally at {label}");
        await CaptureAsync(page, $"05-azure-reflow-{label}.png");
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
